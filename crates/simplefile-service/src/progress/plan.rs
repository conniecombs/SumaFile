//! Transfer destination planning and preflight sizing.

use super::parallel::ConflictAction;
use super::reporting::check_cancelled;
use crate::transfer_staging::conflict_for_existing_destination;
use simplefile_core::path_conflict::{
    path_collision_key, path_exists_no_follow, paths_refer_to_same_entry,
};
use simplefile_core::utils::{
    is_network_path, validate_existing_path_no_resolve, validate_path_no_follow,
};
use std::collections::{HashMap, HashSet};
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

#[derive(Clone, Debug)]
pub(crate) struct TransferPlan {
    pub(crate) source_path: PathBuf,
    pub(crate) final_dest: PathBuf,
    /// How an existing destination is handled for this item and, for a
    /// directory merged into an existing one, for each of its children.
    pub(crate) conflict: ConflictAction,
    pub(crate) network: bool,
    pub(crate) file_count: u64,
    pub(crate) is_dir: bool,
    pub(crate) is_symlink: bool,
    pub(crate) byte_size: u64,
}

#[derive(Default)]
pub(crate) struct TransferEstimate {
    pub(crate) bytes: u64,
    pub(crate) files: u64,
}

/// `is_network_path` queries the volume for every call; a selection of
/// thousands of items on one drive needs the answer once per drive root.
#[derive(Default)]
pub(crate) struct VolumeKindCache {
    network: HashMap<String, bool>,
}

impl VolumeKindCache {
    pub(crate) fn is_network(&mut self, path: &Path) -> bool {
        let key = volume_key(path);
        if let Some(known) = self.network.get(&key) {
            return *known;
        }
        let value = is_network_path(path);
        self.network.insert(key, value);
        value
    }
}

/// Drive root (`C:`) or UNC share (`\\server\share`) that identifies a volume.
pub(crate) fn volume_key(path: &Path) -> String {
    let raw = path.to_string_lossy().replace('/', "\\");
    let trimmed = raw.trim_start_matches("\\\\?\\");
    let trimmed = trimmed
        .strip_prefix("UNC\\")
        .map_or_else(|| trimmed.to_string(), |rest| format!("\\\\{rest}"));
    if let Some(rest) = trimmed.strip_prefix("\\\\") {
        let parts: Vec<&str> = rest.split('\\').filter(|s| !s.is_empty()).take(2).collect();
        return format!("\\\\{}", parts.join("\\")).to_lowercase();
    }
    let bytes = trimmed.as_bytes();
    if bytes.len() >= 2 && bytes[1] == b':' {
        return trimmed[..2].to_ascii_lowercase();
    }
    trimmed.to_lowercase()
}

/// Whether a move between these paths can be a rename (same volume prefix).
pub(crate) fn same_volume(source: &Path, destination: &Path) -> bool {
    source.components().next() == destination.components().next()
}

fn ensure_dir_not_inside_itself(source_path: &Path, final_dest: &Path) -> Result<(), String> {
    if let Ok(canonical_source) = source_path.canonicalize() {
        let canonical_dest = final_dest
            .parent()
            .and_then(|parent| parent.canonicalize().ok())
            .map(|parent| parent.join(final_dest.file_name().unwrap_or_default()));
        if let Some(canonical_dest) = canonical_dest {
            if canonical_dest.starts_with(&canonical_source) {
                return Err(
                    "Cannot copy or move a directory into itself or one of its subdirectories"
                        .to_string(),
                );
            }
        }
    }
    Ok(())
}

pub(crate) fn unique_destination_path(
    dest_dir: &Path,
    file_name: &std::ffi::OsStr,
    planned_destinations: &HashSet<String>,
) -> Result<PathBuf, String> {
    let original = Path::new(file_name);
    let stem = original.file_stem().map_or_else(
        || original.to_string_lossy().to_string(),
        |stem| stem.to_string_lossy().to_string(),
    );
    let ext = original
        .extension()
        .map(|extension| extension.to_string_lossy().to_string());

    for index in 1..10_000u32 {
        let candidate_name = match &ext {
            Some(ext) if !ext.is_empty() => format!("{stem} ({index}).{ext}"),
            _ => format!("{stem} ({index})"),
        };
        let candidate = dest_dir.join(candidate_name);
        let candidate_key = path_collision_key(&candidate);
        if !path_exists_no_follow(&candidate) && !planned_destinations.contains(&candidate_key) {
            return Ok(candidate);
        }
    }

    Err(format!(
        "Could not choose a unique destination for {}",
        original.to_string_lossy()
    ))
}

/// Decide where a top-level item goes. Directories merge into an existing
/// destination directory and carry the action down to their children; files
/// resolve against the existing entry here.
pub(crate) fn resolve_destination(
    source_path: &Path,
    source_is_dir: bool,
    dest_dir: &Path,
    conflict_action: &str,
    planned_destinations: &HashSet<String>,
) -> Result<Option<(PathBuf, ConflictAction)>, String> {
    let action = ConflictAction::parse(conflict_action);
    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_dir.join(file_name);
    let final_key = path_collision_key(&final_dest);
    let existing = fs::symlink_metadata(&final_dest).ok();
    let planned_conflict = planned_destinations.contains(&final_key);

    if existing.is_none() && !planned_conflict {
        return Ok(Some((final_dest, action)));
    }
    let existing_is_dir = existing
        .as_ref()
        .is_some_and(|meta| meta.file_type().is_dir() && !meta.file_type().is_symlink());

    if planned_conflict {
        return match action {
            ConflictAction::KeepBoth => Ok(Some((
                unique_destination_path(dest_dir, file_name, planned_destinations)?,
                action,
            ))),
            ConflictAction::Skip => Ok(None),
            _ => Err(format!(
                "CONFLICT: multiple sources would replace the same destination: {}",
                final_dest.to_string_lossy()
            )),
        };
    }

    if source_is_dir && existing_is_dir && action != ConflictAction::KeepBoth {
        // Merge; each child resolves its own conflict with the same action.
        return Ok(Some((final_dest, action)));
    }
    if source_is_dir != existing_is_dir && action == ConflictAction::Replace {
        return Err(format!(
            "CONFLICT: destination exists with a different type: {}",
            final_dest.to_string_lossy()
        ));
    }

    match action {
        ConflictAction::Skip => Ok(None),
        ConflictAction::Replace => {
            if paths_refer_to_same_entry(source_path, &final_dest) {
                return Ok(None);
            }
            Ok(Some((final_dest, action)))
        }
        ConflictAction::KeepBoth => Ok(Some((
            unique_destination_path(dest_dir, file_name, planned_destinations)?,
            action,
        ))),
        ConflictAction::Error => Err(conflict_for_existing_destination(&final_dest)),
    }
}

/// A top-level item that cannot be planned because of what exists at its
/// destination: `(source, destination, message)`.
pub(crate) type PlanConflict = (PathBuf, PathBuf, String);

/// A top-level item the user's conflict action left in place: `(source,
/// destination)`. Reported so a skip never silently vanishes from the outcome.
pub(crate) type PlanSkip = (PathBuf, PathBuf);

#[cfg(test)]
pub(crate) fn prepare_transfer_inputs(
    sources: Vec<String>,
    destination: String,
    conflict_action: &str,
) -> Result<(Vec<TransferPlan>, PathBuf), String> {
    let mut volumes = VolumeKindCache::default();
    let (plans, dest_path, conflicts, _skipped) =
        prepare_transfer_inputs_cached(sources, destination, conflict_action, &mut volumes)?;
    if let Some((_, _, message)) = conflicts.into_iter().next() {
        return Err(message);
    }
    Ok((plans, dest_path))
}

/// Plans every source that can be planned; conflicts are returned per item so
/// the rest of the selection proceeds and the user resolves them individually.
pub(crate) fn prepare_transfer_inputs_cached(
    sources: Vec<String>,
    destination: String,
    conflict_action: &str,
    volumes: &mut VolumeKindCache,
) -> Result<(Vec<TransferPlan>, PathBuf, Vec<PlanConflict>, Vec<PlanSkip>), String> {
    if sources.is_empty() {
        return Err("No sources specified".to_string());
    }

    let dest_path = validate_existing_path_no_resolve(&destination)?;
    if !dest_path.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }

    crate::transfer_journal::recover_directory(&dest_path)?;
    let dest_network = volumes.is_network(&dest_path);
    let mut plans = Vec::with_capacity(sources.len());
    let mut planned_destinations = HashSet::new();
    let mut conflicts = Vec::new();
    let mut skipped = Vec::new();

    for source in sources {
        // A source that cannot be inspected is reported for that item alone;
        // the rest of the selection still transfers.
        let source_path = match validate_path_no_follow(&source) {
            Ok(path) => path,
            Err(message) => {
                let fallback = PathBuf::from(source.trim());
                let name = fallback.file_name().unwrap_or_default();
                conflicts.push((fallback.clone(), dest_path.join(name), message));
                continue;
            }
        };
        let meta = match fs::symlink_metadata(&source_path) {
            Ok(meta) => meta,
            Err(error) => {
                let name = source_path.file_name().unwrap_or_default();
                conflicts.push((
                    source_path.clone(),
                    dest_path.join(name),
                    format!("Failed to stat {}: {error}", source_path.display()),
                ));
                continue;
            }
        };
        let file_type = meta.file_type();
        let is_symlink = file_type.is_symlink();
        let is_dir = file_type.is_dir() && !is_symlink;
        let is_regular_file = file_type.is_file() && !is_symlink;
        let resolved = match resolve_destination(
            &source_path,
            is_dir,
            &dest_path,
            conflict_action,
            &planned_destinations,
        ) {
            Ok(resolved) => resolved,
            Err(message) if message.starts_with("CONFLICT:") => {
                let name = source_path.file_name().unwrap_or_default();
                conflicts.push((source_path.clone(), dest_path.join(name), message));
                continue;
            }
            Err(error) => return Err(error),
        };
        let Some((final_dest, conflict)) = resolved else {
            let name = source_path.file_name().unwrap_or_default();
            skipped.push((source_path.clone(), dest_path.join(name)));
            continue;
        };

        if is_dir {
            if let Err(message) = ensure_dir_not_inside_itself(&source_path, &final_dest) {
                conflicts.push((source_path.clone(), final_dest, message));
                continue;
            }
        }
        let network = dest_network || volumes.is_network(&source_path);
        planned_destinations.insert(path_collision_key(&final_dest));
        plans.push(TransferPlan {
            source_path,
            final_dest,
            conflict,
            network,
            file_count: if is_dir { 0 } else { 1 },
            is_dir,
            is_symlink,
            byte_size: if is_regular_file { meta.len() } else { 0 },
        });
    }

    Ok((plans, dest_path, conflicts, skipped))
}

/// Sizes were captured while planning; this only sums them (and honours cancel).
pub(crate) fn prime_transfer(
    plans: &mut [TransferPlan],
    cancel: &Arc<AtomicBool>,
) -> Result<TransferEstimate, String> {
    let mut total = TransferEstimate::default();
    for plan in plans {
        check_cancelled(cancel)?;
        total.bytes = total.bytes.saturating_add(plan.byte_size);
        total.files = total.files.saturating_add(plan.file_count);
    }
    Ok(total)
}

#[cfg(test)]
mod batch_tests {
    use super::{prepare_transfer_inputs_cached, VolumeKindCache};
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn unique_temp_path(name: &str) -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("simplefile_service_plan_test_{name}_{nanos}"))
    }

    /// One source that vanished between selection and transfer is reported
    /// for that item; the rest of the selection still gets planned.
    #[test]
    fn missing_source_is_reported_per_item_not_as_a_batch_failure() {
        let src_dir = unique_temp_path("missing_src");
        let dst_dir = unique_temp_path("missing_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let present = src_dir.join("present.txt");
        let missing = src_dir.join("missing.txt");
        fs::write(&present, b"here").unwrap();

        let mut volumes = VolumeKindCache::default();
        let (plans, _, failures, _) = prepare_transfer_inputs_cached(
            vec![
                missing.to_string_lossy().to_string(),
                present.to_string_lossy().to_string(),
            ],
            dst_dir.to_string_lossy().to_string(),
            "error",
            &mut volumes,
        )
        .expect("one missing source must not abort the batch");

        assert_eq!(plans.len(), 1);
        assert_eq!(plans[0].source_path, present);
        assert_eq!(failures.len(), 1);
        assert_eq!(failures[0].0, missing);
        assert!(
            !failures[0].2.starts_with("CONFLICT:"),
            "a missing source is an error, not a conflict: {}",
            failures[0].2
        );

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }

    /// A planning-time skip (destination exists, action "skip") is part of the
    /// outcome, not silently dropped.
    #[test]
    fn planning_time_skips_are_reported() {
        let src_dir = unique_temp_path("skip_src");
        let dst_dir = unique_temp_path("skip_dst");
        fs::create_dir_all(&src_dir).unwrap();
        fs::create_dir_all(&dst_dir).unwrap();
        let fresh = src_dir.join("fresh.txt");
        let clash = src_dir.join("clash.txt");
        fs::write(&fresh, b"fresh").unwrap();
        fs::write(&clash, b"source").unwrap();
        fs::write(dst_dir.join("clash.txt"), b"destination").unwrap();

        let mut volumes = VolumeKindCache::default();
        let (plans, _, conflicts, skipped) = prepare_transfer_inputs_cached(
            vec![
                fresh.to_string_lossy().to_string(),
                clash.to_string_lossy().to_string(),
            ],
            dst_dir.to_string_lossy().to_string(),
            "skip",
            &mut volumes,
        )
        .unwrap();

        assert_eq!(plans.len(), 1);
        assert!(conflicts.is_empty());
        assert_eq!(skipped, vec![(clash.clone(), dst_dir.join("clash.txt"))]);

        let _ = fs::remove_dir_all(&src_dir);
        let _ = fs::remove_dir_all(&dst_dir);
    }
}
