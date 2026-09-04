//! Transfer destination planning and preflight sizing.

use super::reporting::check_cancelled;
use crate::transfer_staging::conflict_for_existing_destination;
use simplefile_core::path_conflict::{
    is_keep_both_action, path_collision_key, path_exists_no_follow, paths_refer_to_same_entry,
};
use simplefile_core::utils::{
    is_network_path, validate_existing_path_no_resolve, validate_path_no_follow,
};
use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

#[derive(Debug)]
pub(crate) struct TransferPlan {
    pub(crate) source_path: PathBuf,
    pub(crate) final_dest: PathBuf,
    pub(crate) replace_existing: bool,
    pub(crate) allow_rename: bool,
    pub(crate) network: bool,
    pub(crate) file_count: u64,
}

#[derive(Default)]
pub(crate) struct TransferEstimate {
    pub(crate) bytes: u64,
    pub(crate) files: u64,
}

pub(crate) fn ensure_not_copying_dir_into_itself(source_path: &Path, final_dest: &Path) -> Result<(), String> {
    let source_meta = fs::symlink_metadata(source_path)
        .map_err(|error| format!("Failed to stat source: {error}"))?;
    if !source_meta.file_type().is_dir() {
        return Ok(());
    }

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

pub(crate) fn resolve_destination(
    source_path: &Path,
    dest_dir: &Path,
    conflict_action: &str,
    planned_destinations: &HashSet<String>,
) -> Result<Option<(PathBuf, bool)>, String> {
    let file_name = source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let final_dest = dest_dir.join(file_name);
    let final_key = path_collision_key(&final_dest);
    let exists = path_exists_no_follow(&final_dest);
    let planned_conflict = planned_destinations.contains(&final_key);

    if !exists && !planned_conflict {
        return Ok(Some((final_dest, false)));
    }

    match conflict_action.to_ascii_lowercase().as_str() {
        "skip" => Ok(None),
        "replace" => {
            if planned_conflict {
                return Err(format!(
                    "CONFLICT: multiple sources would replace the same destination: {}",
                    final_dest.to_string_lossy()
                ));
            }
            if exists && paths_refer_to_same_entry(source_path, &final_dest) {
                return Ok(None);
            }
            Ok(Some((final_dest, exists)))
        }
        "rename" | "keep-both" | "keep_both" => Ok(Some((
            unique_destination_path(dest_dir, file_name, planned_destinations)?,
            false,
        ))),
        _ => Err(conflict_for_existing_destination(&final_dest)),
    }
}

pub(crate) fn prepare_transfer_inputs(
    sources: Vec<String>,
    destination: String,
    conflict_action: &str,
) -> Result<(Vec<TransferPlan>, PathBuf), String> {
    if sources.is_empty() {
        return Err("No sources specified".to_string());
    }

    let dest_path = validate_existing_path_no_resolve(&destination)?;
    if !dest_path.is_dir() {
        return Err(format!("Destination is not a directory: {destination}"));
    }

    let mut plans = Vec::with_capacity(sources.len());
    let mut planned_destinations = HashSet::new();
    let keep_both = is_keep_both_action(conflict_action);

    for source in sources {
        let source_path = validate_path_no_follow(&source)?;
        let Some((final_dest, replace_existing)) = resolve_destination(
            &source_path,
            &dest_path,
            conflict_action,
            &planned_destinations,
        )?
        else {
            continue;
        };

        ensure_not_copying_dir_into_itself(&source_path, &final_dest)?;
        let network = is_network_path(&source_path) || is_network_path(&dest_path);
        planned_destinations.insert(path_collision_key(&final_dest));
        plans.push(TransferPlan {
            source_path,
            final_dest,
            replace_existing,
            allow_rename: !keep_both,
            network,
            file_count: 0,
        });
    }

    Ok((plans, dest_path))
}

pub(crate) fn ensure_destination_available(plan: &TransferPlan) -> Result<(), String> {
    if !plan.replace_existing && path_exists_no_follow(&plan.final_dest) {
        return Err(conflict_for_existing_destination(&plan.final_dest));
    }
    Ok(())
}

pub(crate) fn choose_next_keep_both_destination(
    plan: &mut TransferPlan,
    reserved_destinations: &mut HashSet<String>,
) -> Result<(), String> {
    reserved_destinations.remove(&path_collision_key(&plan.final_dest));
    let dest_dir = plan
        .final_dest
        .parent()
        .ok_or_else(|| "Cannot get destination directory".to_string())?;
    let file_name = plan
        .source_path
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?;
    let next_dest = unique_destination_path(dest_dir, file_name, reserved_destinations)?;
    ensure_not_copying_dir_into_itself(&plan.source_path, &next_dest)?;
    reserved_destinations.insert(path_collision_key(&next_dest));
    plan.final_dest = next_dest;
    Ok(())
}

pub(crate) fn prime_path_transfer(path: &Path, cancel: &Arc<AtomicBool>) -> Result<TransferEstimate, String> {
    check_cancelled(cancel)?;
    let meta = fs::symlink_metadata(path)
        .map_err(|error| format!("Failed to stat {}: {error}", path.display()))?;
    let file_type = meta.file_type();
    if file_type.is_symlink() {
        return Ok(TransferEstimate { bytes: 0, files: 1 });
    }
    if file_type.is_file() {
        return Ok(TransferEstimate {
            bytes: meta.len(),
            files: 1,
        });
    }
    if !file_type.is_dir() {
        return Ok(TransferEstimate::default());
    }
    Ok(TransferEstimate::default())
}

pub(crate) fn prime_transfer(
    plans: &mut [TransferPlan],
    cancel: &Arc<AtomicBool>,
) -> Result<TransferEstimate, String> {
    let mut total = TransferEstimate::default();
    for plan in plans {
        check_cancelled(cancel)?;
        let estimate = prime_path_transfer(&plan.source_path, cancel)?;
        plan.file_count = estimate.files;
        total.bytes = total.bytes.saturating_add(estimate.bytes);
        total.files = total.files.saturating_add(estimate.files);
    }
    Ok(total)
}
