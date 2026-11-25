//! Gathers the raw changes of the watched folders into the changes the host
//! is told about: the last state of each path, a rename as one change, and a
//! folder with too many changes as "read it again".
//!
//! It keeps no thread and no clock of its own: the watcher's thread pushes
//! changes in, asks when the next batch is due and takes it.

use super::{coalesce_key, is_ignored_watcher_path, EntryNow, WatchChange};
use simplefile_core::models::FileChangeEvent;
use std::collections::HashMap;
use std::path::Path;
use std::time::{Duration, Instant};

/// A batch is told once no change has arrived for this long.
pub(super) const QUIET: Duration = Duration::from_millis(150);
/// A batch is told this long after its first change at the latest.
pub(super) const MAX_BATCH_AGE: Duration = Duration::from_millis(500);
/// More pending changes than this in one folder are told as a change of the
/// folder itself: the host reads it again.
pub(super) const STORM_LIMIT: usize = 512;

/// What the engine read from the system for one watched folder.
#[derive(Debug, Clone)]
pub(super) enum RawChange {
    /// An entry of `root` was created, changed or removed.
    Entry {
        root: String,
        path: String,
        kind: &'static str,
        now: EntryNow,
    },
    /// An entry of `root` was renamed inside it.
    Renamed {
        root: String,
        from: String,
        to: String,
        now: EntryNow,
    },
    /// The system could not list every change of `root`.
    Overflow { root: String },
    /// `root` itself is no longer at its path.
    Gone { root: String },
}

#[derive(Debug)]
struct Pending {
    path: String,
    kind: &'static str,
    now: EntryNow,
    /// The path this entry was renamed from, in this batch.
    renamed_from: Option<String>,
    /// The order of the rename among the batch's renames.
    rename_order: u64,
    /// The key of the watched folder the path is in.
    root_key: String,
}

#[derive(Default)]
pub(super) struct Coalescer {
    /// The watched folders: key and path.
    roots: Vec<(String, String)>,
    /// The pending changes of entries, by the key of their path.
    pending: HashMap<String, Pending>,
    /// Pending changes per watched folder, by its key.
    pending_in: HashMap<String, usize>,
    /// The pending changes of watched folders themselves, by their key: the
    /// kind to tell. Kept apart from the entries, as a watched folder may
    /// also be an entry of another watched folder.
    pending_roots: HashMap<String, &'static str>,
    batch_started: Option<Instant>,
    quiet_until: Option<Instant>,
    renames: u64,
}

impl Coalescer {
    /// Names the folders watched from now on. Changes still pending are the
    /// caller's to take first.
    pub(super) fn set_roots(&mut self, roots: &[String]) {
        self.roots = roots
            .iter()
            .map(|root| (coalesce_key(root), root.clone()))
            .collect();
    }

    #[cfg(test)]
    pub(super) fn is_empty(&self) -> bool {
        self.pending.is_empty() && self.pending_roots.is_empty()
    }

    /// When the pending batch is due; `None` with nothing pending.
    pub(super) fn deadline(&self) -> Option<Instant> {
        let started = self.batch_started?;
        let quiet = self.quiet_until?;
        Some(quiet.min(started + MAX_BATCH_AGE))
    }

    pub(super) fn push(&mut self, change: RawChange, now: Instant) {
        let touched = match change {
            RawChange::Entry {
                root,
                path,
                kind,
                now: entry,
            } => self.push_entry(&root, path, kind, entry),
            RawChange::Renamed {
                root,
                from,
                to,
                now: entry,
            } => self.push_rename(&root, from, to, entry),
            RawChange::Overflow { root } => self.push_root(&coalesce_key(&root), "modify"),
            RawChange::Gone { root } => self.push_root(&coalesce_key(&root), FileChangeEvent::GONE),
        };
        if !touched {
            return;
        }
        if self.batch_started.is_none() {
            self.batch_started = Some(now);
        }
        self.quiet_until = Some(now + QUIET);
    }

    /// The pending batch, when it is due at `now`.
    pub(super) fn take_due(&mut self, now: Instant) -> Vec<WatchChange> {
        match self.deadline() {
            Some(deadline) if deadline <= now => self.take_all(),
            _ => Vec::new(),
        }
    }

    /// The pending batch, due or not. Renames come first, in the order they
    /// happened: each takes its old name out of a view and puts the new one
    /// in, and the last state of every other path is applied on top of that.
    /// The changes of watched folders themselves come last.
    pub(super) fn take_all(&mut self) -> Vec<WatchChange> {
        self.batch_started = None;
        self.quiet_until = None;
        self.pending_in.clear();
        let mut pending: Vec<Pending> = self.pending.drain().map(|(_, change)| change).collect();
        pending.sort_by(|left, right| {
            let rank = |change: &Pending| {
                if change.renamed_from.is_some() {
                    (0, change.rename_order)
                } else {
                    (1, 0)
                }
            };
            rank(left)
                .cmp(&rank(right))
                .then_with(|| left.path.cmp(&right.path))
        });
        let mut batch: Vec<WatchChange> = pending
            .into_iter()
            .map(|change| WatchChange {
                event: FileChangeEvent {
                    path: change.path,
                    kind: change.kind.to_string(),
                },
                now: change.now,
                renamed_from: change.renamed_from,
            })
            .collect();
        let mut roots: Vec<(String, &'static str)> = self
            .pending_roots
            .drain()
            .collect::<Vec<_>>()
            .into_iter()
            .map(|(key, kind)| (self.root_path_of(&key), kind))
            .collect();
        roots.sort();
        batch.extend(roots.into_iter().map(|(path, kind)| WatchChange {
            event: FileChangeEvent {
                path,
                kind: kind.to_string(),
            },
            now: EntryNow::Unknown,
            renamed_from: None,
        }));
        batch
    }

    fn push_entry(&mut self, root: &str, path: String, kind: &'static str, now: EntryNow) -> bool {
        if is_ignored_watcher_path(Path::new(&path)) {
            return false;
        }
        let root_key = coalesce_key(root);
        if !self.accepts(&root_key) {
            return false;
        }
        // A path that was renamed to in this batch stays the arrival of that
        // rename, with what it is now.
        let key = coalesce_key(&path);
        let (renamed_from, rename_order) = match self.pending.get(&key) {
            Some(earlier) if earlier.renamed_from.is_some() => {
                (earlier.renamed_from.clone(), earlier.rename_order)
            }
            _ => (None, 0),
        };
        self.insert(
            key,
            Pending {
                path,
                kind,
                now,
                renamed_from,
                rename_order,
                root_key,
            },
        );
        true
    }

    fn push_rename(&mut self, root: &str, from: String, to: String, now: EntryNow) -> bool {
        // A temporary name is not told: saving through one is the arrival of
        // the final name, and renaming to one is the departure of the old.
        let from_ignored = is_ignored_watcher_path(Path::new(&from));
        let to_ignored = is_ignored_watcher_path(Path::new(&to));
        if from_ignored || to_ignored {
            let mut touched = false;
            if !from_ignored {
                touched |= self.push_entry(root, from, "rename", EntryNow::Gone);
            }
            if !to_ignored {
                touched |= self.push_entry(root, to, "rename", now);
            }
            return touched;
        }

        let root_key = coalesce_key(root);
        if !self.accepts(&root_key) {
            return false;
        }
        let from_key = coalesce_key(&from);
        let to_key = coalesce_key(&to);
        // A name that was itself renamed to in this batch passes on where it
        // came from: a -> b -> c is told as a -> c. Back at the name it had,
        // no rename is left to tell.
        let origin = match self.pending.get(&from_key) {
            Some(earlier) if earlier.renamed_from.is_some() => earlier.renamed_from.clone(),
            _ => Some(from.clone()),
        }
        .filter(|origin| *origin != to);

        self.renames += 1;
        let order = self.renames;
        if from_key != to_key {
            self.insert(
                from_key,
                Pending {
                    path: from,
                    kind: "rename",
                    now: EntryNow::Gone,
                    renamed_from: None,
                    rename_order: 0,
                    root_key: root_key.clone(),
                },
            );
        }
        self.insert(
            to_key,
            Pending {
                path: to,
                kind: "rename",
                now,
                renamed_from: origin,
                rename_order: order,
                root_key,
            },
        );
        true
    }

    /// A change of a watched folder itself. It replaces the changes pending
    /// inside the folder, which the host learns by reading it again (or no
    /// longer needs, for a folder that is gone).
    fn push_root(&mut self, root_key: &str, kind: &'static str) -> bool {
        if !self.roots.iter().any(|(key, _)| key == root_key) {
            return false;
        }
        if self.pending_roots.get(root_key) == Some(&FileChangeEvent::GONE) {
            // Gone is the last word about a folder.
            return false;
        }
        self.pending.retain(|_, change| change.root_key != root_key);
        self.pending_in.remove(root_key);
        self.pending_roots.insert(root_key.to_string(), kind);
        true
    }

    /// Whether a change inside the folder `root_key` is still worth keeping:
    /// the folder is watched and is not itself pending to be read again.
    fn accepts(&self, root_key: &str) -> bool {
        self.roots.iter().any(|(key, _)| key == root_key)
            && !self.pending_roots.contains_key(root_key)
    }

    fn insert(&mut self, key: String, change: Pending) {
        let root_key = change.root_key.clone();
        if self.pending.insert(key, change).is_none() {
            let count = self.pending_in.entry(root_key.clone()).or_insert(0);
            *count += 1;
            if *count > STORM_LIMIT {
                self.push_root(&root_key, "modify");
            }
        }
    }

    fn root_path_of(&self, root_key: &str) -> String {
        self.roots
            .iter()
            .find(|(key, _)| key == root_key)
            .map(|(_, path)| path.clone())
            .unwrap_or_else(|| root_key.to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const ROOT: &str = r"C:\work";

    fn coalescer() -> Coalescer {
        let mut coalescer = Coalescer::default();
        coalescer.set_roots(&[ROOT.to_string(), r"C:\other".to_string()]);
        coalescer
    }

    fn entry(name: &str, kind: &'static str) -> RawChange {
        RawChange::Entry {
            root: ROOT.to_string(),
            path: format!(r"{ROOT}\{name}"),
            kind,
            now: EntryNow::Unknown,
        }
    }

    fn rename(from: &str, to: &str) -> RawChange {
        RawChange::Renamed {
            root: ROOT.to_string(),
            from: format!(r"{ROOT}\{from}"),
            to: format!(r"{ROOT}\{to}"),
            now: EntryNow::Unknown,
        }
    }

    /// The batch as `kind name` lines, a rename as `rename new <- old`.
    fn told(changes: &[WatchChange]) -> Vec<String> {
        let name = |path: &str| {
            path.strip_prefix(ROOT)
                .map(|rest| rest.trim_start_matches('\\').to_string())
                .filter(|rest| !rest.is_empty())
                .unwrap_or_else(|| path.to_string())
        };
        changes
            .iter()
            .map(|change| match &change.renamed_from {
                Some(from) => format!(
                    "{} {} <- {}",
                    change.event.kind,
                    name(&change.event.path),
                    name(from)
                ),
                None => format!("{} {}", change.event.kind, name(&change.event.path)),
            })
            .collect()
    }

    #[test]
    fn the_last_change_of_a_path_is_the_one_told_whatever_its_letter_case() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(entry("File.txt", "create"), now);
        coalescer.push(entry("file.txt", "modify"), now);
        coalescer.push(entry("other.txt", "remove"), now);
        let batch = coalescer.take_all();
        #[cfg(windows)]
        assert_eq!(told(&batch), ["modify file.txt", "remove other.txt"]);
        #[cfg(not(windows))]
        assert_eq!(batch.len(), 3);
        assert!(coalescer.is_empty());
    }

    #[test]
    fn a_batch_is_due_after_a_quiet_spell_and_at_its_greatest_age_at_the_latest() {
        let mut coalescer = coalescer();
        let start = Instant::now();
        assert_eq!(coalescer.deadline(), None);
        coalescer.push(entry("a.txt", "create"), start);
        assert_eq!(coalescer.deadline(), Some(start + QUIET));
        assert!(coalescer.take_due(start + QUIET / 2).is_empty());

        // Changes that keep arriving put the batch off, but not past its greatest age.
        let mut at = start;
        while at + QUIET / 2 < start + MAX_BATCH_AGE {
            at += QUIET / 2;
            coalescer.push(entry("a.txt", "modify"), at);
        }
        assert_eq!(coalescer.deadline(), Some(start + MAX_BATCH_AGE));
        assert!(coalescer
            .take_due(start + MAX_BATCH_AGE - Duration::from_millis(1))
            .is_empty());
        assert_eq!(
            told(&coalescer.take_due(start + MAX_BATCH_AGE)),
            ["modify a.txt"]
        );
        assert_eq!(coalescer.deadline(), None);
    }

    #[test]
    fn temporary_and_shell_bookkeeping_names_are_not_told() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(entry("download.crdownload", "create"), now);
        coalescer.push(entry("Thumbs.db", "modify"), now);
        assert!(coalescer.is_empty());
        assert_eq!(coalescer.deadline(), None);

        // Saving through a temporary name is the arrival of the final name;
        // renaming to one is the departure of the old.
        coalescer.push(rename("report.tmp", "report.docx"), now);
        coalescer.push(rename("old.txt", "old.tmp"), now);
        assert_eq!(
            told(&coalescer.take_all()),
            ["rename old.txt", "rename report.docx"]
        );
    }

    #[test]
    fn a_rename_is_one_change_that_names_both_ends() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(rename("a.txt", "b.txt"), now);
        // The old name is still told, after the rename: a host that only follows paths needs it.
        assert_eq!(
            told(&coalescer.take_all()),
            ["rename b.txt <- a.txt", "rename a.txt"]
        );
    }

    #[test]
    fn a_chain_of_renames_is_told_from_the_first_name_to_the_last() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(rename("a.txt", "b.txt"), now);
        coalescer.push(rename("b.txt", "c.txt"), now);
        assert_eq!(
            told(&coalescer.take_all()),
            ["rename c.txt <- a.txt", "rename a.txt", "rename b.txt"]
        );
    }

    #[test]
    fn a_rename_and_back_is_no_rename() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(rename("a.txt", "b.txt"), now);
        coalescer.push(rename("b.txt", "a.txt"), now);
        assert_eq!(
            told(&coalescer.take_all()),
            ["rename a.txt", "rename b.txt"]
        );
    }

    #[test]
    fn renames_are_told_in_the_order_they_happened_and_before_the_other_changes() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(entry("0-first-by-name.txt", "modify"), now);
        // z takes a's place after a moved to b: told in the other order, a view would lose b.
        coalescer.push(rename("a.txt", "b.txt"), now);
        coalescer.push(rename("z.txt", "a.txt"), now);
        // The arrival of a rename that changes again keeps its place and where it came from.
        coalescer.push(entry("b.txt", "modify"), now);
        assert_eq!(
            told(&coalescer.take_all()),
            [
                "modify b.txt <- a.txt",
                "rename a.txt <- z.txt",
                "modify 0-first-by-name.txt",
                "rename z.txt",
            ]
        );
    }

    #[test]
    fn a_name_that_is_created_again_after_it_was_renamed_away_is_told_after_the_rename() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(rename("a.txt", "b.txt"), now);
        coalescer.push(entry("a.txt", "create"), now);
        assert_eq!(
            told(&coalescer.take_all()),
            ["rename b.txt <- a.txt", "create a.txt"]
        );
    }

    #[test]
    fn a_storm_in_one_folder_is_told_as_a_change_of_that_folder_only() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(
            RawChange::Entry {
                root: r"C:\other".to_string(),
                path: r"C:\other\kept.txt".to_string(),
                kind: "create",
                now: EntryNow::Unknown,
            },
            now,
        );
        for index in 0..=STORM_LIMIT {
            coalescer.push(entry(&format!("file-{index}.txt"), "create"), now);
        }
        // Later changes of the folder add nothing: it is read again anyway.
        coalescer.push(entry("late.txt", "create"), now);
        let batch = coalescer.take_all();
        assert_eq!(
            batch
                .iter()
                .map(|change| format!("{} {}", change.event.kind, change.event.path))
                .collect::<Vec<_>>(),
            [r"create C:\other\kept.txt", r"modify C:\work"]
        );
    }

    #[test]
    fn an_overflow_drops_the_changes_pending_in_its_folder_and_gone_is_the_last_word() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.push(entry("a.txt", "create"), now);
        coalescer.push(
            RawChange::Overflow {
                root: ROOT.to_string(),
            },
            now,
        );
        coalescer.push(entry("b.txt", "create"), now);
        assert_eq!(told(&coalescer.take_all()), [format!("modify {ROOT}")]);

        coalescer.push(
            RawChange::Gone {
                root: ROOT.to_string(),
            },
            now,
        );
        coalescer.push(
            RawChange::Overflow {
                root: ROOT.to_string(),
            },
            now,
        );
        coalescer.push(entry("c.txt", "create"), now);
        assert_eq!(told(&coalescer.take_all()), [format!("gone {ROOT}")]);
    }

    #[test]
    fn a_watched_folder_that_is_also_a_row_of_another_keeps_both_changes() {
        let mut coalescer = Coalescer::default();
        let inner = format!(r"{ROOT}\inner");
        coalescer.set_roots(&[ROOT.to_string(), inner.clone()]);
        let now = Instant::now();
        coalescer.push(RawChange::Gone { root: inner }, now);
        // The folder above reports the same folder as one of its rows.
        coalescer.push(entry("inner", "remove"), now);
        assert_eq!(told(&coalescer.take_all()), ["remove inner", "gone inner"]);
    }

    #[test]
    fn changes_of_a_folder_that_is_not_watched_are_dropped() {
        let mut coalescer = coalescer();
        let now = Instant::now();
        coalescer.set_roots(&[r"C:\other".to_string()]);
        coalescer.push(entry("a.txt", "create"), now);
        coalescer.push(
            RawChange::Gone {
                root: ROOT.to_string(),
            },
            now,
        );
        assert!(coalescer.is_empty());
    }
}
