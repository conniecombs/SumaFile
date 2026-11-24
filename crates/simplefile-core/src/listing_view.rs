//! A directory listing the service keeps sorted and filtered for one pane.
//!
//! The host used to receive every entry of a folder, then sort and filter the
//! whole list itself each time the view changed. A [`ListingView`] holds the
//! entries here instead: the host names the order and the filter it wants,
//! reads the rows it needs with [`ListingView::page`], and is told what moved
//! with a [`ListingViewDelta`] when a file is added, removed or changed, so a
//! change never costs a second enumeration or a second transfer of the folder.

use crate::collation::Collator;
use crate::models::{
    DirectoryListing, FileEntry, ListingViewChange, ListingViewDelta, ListingViewPage,
};
use std::cmp::Ordering;
use std::collections::HashMap;

/// A refresh that changes more rows than this is reported as a reset: the
/// host re-reads its pages instead of applying hundreds of single edits.
pub const MAX_DELTA_CHANGES: usize = 256;

/// Numbers the revisions of every view a session holds from one counter, so
/// a revision is never reused: not by another view, and not by the view that
/// later takes the same id when its pane navigates. A host that still holds
/// rows of the view before can then tell a change of the new view apart.
#[derive(Debug, Clone, Default)]
pub struct RevisionClock {
    last: std::sync::Arc<std::sync::atomic::AtomicU64>,
}

impl RevisionClock {
    /// The next revision: greater than every revision handed out before.
    pub fn next(&self) -> u64 {
        self.last.fetch_add(1, std::sync::atomic::Ordering::Relaxed) + 1
    }
}

/// The order and filter of a view. Two equal option sets give the same rows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ListingViewOptions {
    /// `name`, `size`, `modified` (or `date`), `created`, `accessed`,
    /// `attributes`, `extension` or `type`. Anything else sorts by name.
    pub sort_by: String,
    pub sort_ascending: bool,
    /// Folders before files, whatever the sort column.
    pub folders_first: bool,
    /// When false, entries with the Hidden or System attribute and names
    /// that start with a dot are left out.
    pub include_hidden: bool,
    /// A case-insensitive substring of the name; blank is no filter.
    pub filter: Option<String>,
    /// The culture whose order names sort in (for example `en-US`).
    pub locale: Option<String>,
}

impl Default for ListingViewOptions {
    fn default() -> Self {
        Self {
            sort_by: "name".to_string(),
            sort_ascending: true,
            folders_first: true,
            include_hidden: true,
            filter: None,
            locale: None,
        }
    }
}

impl ListingViewOptions {
    /// The options with the sort column lowercased and a blank filter or
    /// locale dropped. [`Self::shows`] expects normalized options.
    pub fn normalized(mut self) -> Self {
        self.sort_by = self.sort_by.trim().to_ascii_lowercase();
        if self.sort_by.is_empty() {
            self.sort_by = "name".to_string();
        }
        self.filter = self
            .filter
            .map(|filter| filter.trim().to_string())
            .filter(|filter| !filter.is_empty());
        self.locale = self
            .locale
            .map(|locale| locale.trim().to_string())
            .filter(|locale| !locale.is_empty());
        self
    }

    /// Whether a view with these (normalized) options shows `entry`.
    pub fn shows(&self, entry: &FileEntry) -> bool {
        if !self.include_hidden
            && (entry.is_hidden || entry.is_system || entry.name.starts_with('.'))
        {
            return false;
        }
        self.filter.as_deref().is_none_or(|filter| {
            crate::native_accel::contains_case_insensitive(&entry.name, filter)
        })
    }
}

enum ColumnKey {
    /// The name column: the name key is the whole comparison.
    Name,
    Number(u64),
    Bytes(Box<[u8]>),
}

struct Slot {
    entry: FileEntry,
    /// Collation key of the name; survives a change of sort column.
    name_key: Box<[u8]>,
    column: ColumnKey,
    visible: bool,
}

/// One pane's sorted, filtered listing.
pub struct ListingView {
    view_id: String,
    path: String,
    parent: Option<String>,
    is_network: bool,
    /// False for the Recycle Bin and archive folders, whose entry paths are
    /// not the folder path plus the name, so pages must carry them.
    paths_follow_names: bool,
    slots: Vec<Option<Slot>>,
    free: Vec<u32>,
    /// Slot of each entry by its identity: see [`Self::key_of`].
    by_name: HashMap<String, u32>,
    /// The visible slots in view order.
    order: Vec<u32>,
    options: ListingViewOptions,
    collator: Collator,
    clock: RevisionClock,
    revision: u64,
}

impl ListingView {
    /// Builds the view of `listing` under `options`, numbering its revisions
    /// from a clock of its own.
    pub fn new(
        view_id: impl Into<String>,
        listing: DirectoryListing,
        paths_follow_names: bool,
        options: ListingViewOptions,
    ) -> Self {
        Self::with_clock(
            view_id,
            listing,
            paths_follow_names,
            options,
            RevisionClock::default(),
        )
    }

    /// Builds the view of `listing` under `options`, numbering its revisions
    /// from `clock`, which the other views of the session share.
    pub fn with_clock(
        view_id: impl Into<String>,
        listing: DirectoryListing,
        paths_follow_names: bool,
        options: ListingViewOptions,
        clock: RevisionClock,
    ) -> Self {
        let options = options.normalized();
        let collator = Collator::for_locale(options.locale.as_deref());
        let mut view = Self {
            view_id: view_id.into(),
            path: listing.path,
            parent: listing.parent,
            is_network: listing.is_network,
            paths_follow_names,
            slots: Vec::new(),
            free: Vec::new(),
            by_name: HashMap::new(),
            order: Vec::new(),
            options,
            collator,
            revision: clock.next(),
            clock,
        };
        view.load(listing.entries);
        view
    }

    pub fn view_id(&self) -> &str {
        &self.view_id
    }

    pub fn path(&self) -> &str {
        &self.path
    }

    pub fn options(&self) -> &ListingViewOptions {
        &self.options
    }

    /// The number of rows the view shows (after the filter).
    pub fn total(&self) -> u32 {
        self.order.len() as u32
    }

    /// Changes whenever the rows or their order change.
    pub fn revision(&self) -> u64 {
        self.revision
    }

    /// Every entry of the folder, filtered or not, in no particular order:
    /// what the session cache stores for the folder.
    pub fn raw_listing(&self) -> DirectoryListing {
        DirectoryListing {
            path: self.path.clone(),
            parent: self.parent.clone(),
            entries: self
                .slots
                .iter()
                .flatten()
                .map(|slot| slot.entry.clone())
                .collect(),
            is_network: self.is_network,
        }
    }

    /// Whether the entries may go into the session's listing cache: an
    /// ordinary local folder, not a network share, an archive or the Recycle Bin.
    pub fn is_cacheable(&self) -> bool {
        self.paths_follow_names && !self.is_network
    }

    /// [`Self::raw_listing`] without copying, for a view that is closing.
    pub fn into_raw_listing(self) -> DirectoryListing {
        DirectoryListing {
            path: self.path,
            parent: self.parent,
            entries: self
                .slots
                .into_iter()
                .flatten()
                .map(|slot| slot.entry)
                .collect(),
            is_network: self.is_network,
        }
    }

    /// Re-sorts and re-filters the entries already held. Returns false, and
    /// changes nothing, when `options` are the view's own.
    pub fn set_options(&mut self, options: ListingViewOptions) -> bool {
        let options = options.normalized();
        if options == self.options {
            return false;
        }

        let locale_changed = options.locale != self.options.locale;
        if locale_changed {
            self.collator = Collator::for_locale(options.locale.as_deref());
        }
        self.options = options;
        for index in 0..self.slots.len() {
            if let Some(mut slot) = self.slots[index].take() {
                if locale_changed {
                    slot.name_key = self.collator.sort_key(&slot.entry.name);
                }
                slot.column = self.column_key(&slot.entry);
                slot.visible = self.matches(&slot.entry);
                self.slots[index] = Some(slot);
            }
        }
        self.rebuild_order();
        self.revision = self.clock.next();
        true
    }

    /// The rows `offset..offset + count` of the view. Fewer rows come back at
    /// the end of the view; none when `offset` is past it.
    pub fn page(&self, offset: u32, count: u32) -> ListingViewPage {
        let start = (offset as usize).min(self.order.len());
        let end = start.saturating_add(count as usize).min(self.order.len());
        let entries = self.order[start..end]
            .iter()
            .filter_map(|&slot| self.slots[slot as usize].as_ref())
            .map(|slot| self.wire_entry(&slot.entry))
            .collect();
        ListingViewPage {
            view_id: self.view_id.clone(),
            path: self.path.clone(),
            parent: self.parent.clone(),
            is_network: self.is_network,
            total: self.total(),
            revision: self.revision,
            offset: start as u32,
            entries,
        }
    }

    /// True for an ordinary folder, where a name is one entry and its path
    /// is the folder path plus the name. False for the Recycle Bin and
    /// archive folders: several entries may share a name there, so they are
    /// told apart by path and only [`Self::replace_entries`] can change them.
    pub fn follows_names(&self) -> bool {
        self.paths_follow_names
    }

    /// Records what one name in the folder is now: `Some` entry when it
    /// exists, `None` when it is gone. The delta lists the rows that moved;
    /// it has no changes (and the revision stays) when nothing visible did.
    /// Only for a view that [`Self::follows_names`]; any other view is left
    /// as it is.
    pub fn apply_entry(&mut self, name: &str, entry: Option<FileEntry>) -> ListingViewDelta {
        let base_revision = self.revision;
        let mut changes = Vec::new();
        if self.paths_follow_names {
            self.apply_keyed(fold_name(name), entry, &mut changes);
        }
        self.finish_delta(base_revision, changes, false)
    }

    /// What tells two entries of the folder apart: the case-folded name in
    /// an ordinary folder, the path where names may repeat (two deleted
    /// files called `notes.txt` are two rows of the Recycle Bin).
    fn key_of(&self, entry: &FileEntry) -> String {
        if self.paths_follow_names {
            fold_name(&entry.name)
        } else {
            fold_name(&entry.path)
        }
    }

    /// Replaces the folder's entries with a fresh enumeration and reports the
    /// difference. More than [`MAX_DELTA_CHANGES`] changed rows is a reset.
    pub fn replace_entries(&mut self, entries: Vec<FileEntry>) -> ListingViewDelta {
        let base_revision = self.revision;
        let mut fresh: HashMap<String, FileEntry> = HashMap::with_capacity(entries.len());
        for entry in entries {
            fresh.insert(self.key_of(&entry), entry);
        }

        let removed: Vec<String> = self
            .by_name
            .keys()
            .filter(|key| !fresh.contains_key(*key))
            .cloned()
            .collect();
        let mut upserts: Vec<FileEntry> = Vec::new();
        for (key, entry) in &fresh {
            let unchanged = self
                .by_name
                .get(key)
                .and_then(|&slot| self.slots[slot as usize].as_ref())
                .is_some_and(|slot| same_entry(&slot.entry, entry));
            if !unchanged {
                upserts.push(entry.clone());
            }
        }

        if removed.len() + upserts.len() > MAX_DELTA_CHANGES {
            self.load(fresh.into_values().collect());
            self.revision = self.clock.next();
            return ListingViewDelta {
                view_id: self.view_id.clone(),
                base_revision,
                revision: self.revision,
                total: self.total(),
                reset: true,
                changes: Vec::new(),
            };
        }

        // A fixed order, so the same difference always gives the same delta.
        let mut removed = removed;
        removed.sort();
        upserts.sort_by(|left, right| {
            left.name
                .cmp(&right.name)
                .then_with(|| left.path.cmp(&right.path))
        });
        let mut changes = Vec::new();
        for key in removed {
            self.apply_keyed(key, None, &mut changes);
        }
        for entry in upserts {
            let key = self.key_of(&entry);
            self.apply_keyed(key, Some(entry), &mut changes);
        }
        self.finish_delta(base_revision, changes, false)
    }

    fn finish_delta(
        &mut self,
        base_revision: u64,
        changes: Vec<ListingViewChange>,
        reset: bool,
    ) -> ListingViewDelta {
        if !changes.is_empty() || reset {
            self.revision = self.clock.next();
        }
        ListingViewDelta {
            view_id: self.view_id.clone(),
            base_revision,
            revision: self.revision,
            total: self.total(),
            reset,
            changes,
        }
    }

    fn apply_keyed(
        &mut self,
        key: String,
        entry: Option<FileEntry>,
        changes: &mut Vec<ListingViewChange>,
    ) {
        let existing = self.by_name.get(&key).copied();
        let old_index = existing
            .filter(|&slot| {
                self.slots[slot as usize]
                    .as_ref()
                    .is_some_and(|s| s.visible)
            })
            .and_then(|slot| self.order.iter().position(|&candidate| candidate == slot));

        if let Some(old_index) = old_index {
            self.order.remove(old_index);
        }
        if let Some(slot) = existing {
            self.slots[slot as usize] = None;
            self.free.push(slot);
            self.by_name.remove(&key);
        }

        let Some(entry) = entry else {
            if let Some(old_index) = old_index {
                changes.push(ListingViewChange::remove(old_index as u32));
            }
            return;
        };

        let slot = self.insert_slot(entry);
        let visible = self.slots[slot as usize]
            .as_ref()
            .is_some_and(|slot| slot.visible);
        if !visible {
            if let Some(old_index) = old_index {
                changes.push(ListingViewChange::remove(old_index as u32));
            }
            return;
        }

        let new_index = self
            .order
            .binary_search_by(|&candidate| self.compare_slots(candidate, slot))
            .unwrap_or_else(|index| index);
        self.order.insert(new_index, slot);
        let wire = self.slots[slot as usize]
            .as_ref()
            .map(|slot| self.wire_entry(&slot.entry))
            .expect("the slot was just filled");
        match old_index {
            Some(old_index) if old_index == new_index => {
                changes.push(ListingViewChange::update(new_index as u32, wire));
            }
            Some(old_index) => {
                changes.push(ListingViewChange::remove(old_index as u32));
                changes.push(ListingViewChange::insert(new_index as u32, wire));
            }
            None => changes.push(ListingViewChange::insert(new_index as u32, wire)),
        }
    }

    fn load(&mut self, entries: Vec<FileEntry>) {
        self.slots.clear();
        self.free.clear();
        self.by_name.clear();
        self.slots.reserve(entries.len());
        for entry in entries {
            self.insert_slot(entry);
        }
        self.rebuild_order();
    }

    /// Stores `entry`, replacing the entry with the same identity, and returns its slot.
    fn insert_slot(&mut self, entry: FileEntry) -> u32 {
        let key = self.key_of(&entry);
        let slot = Slot {
            name_key: self.collator.sort_key(&entry.name),
            column: self.column_key(&entry),
            visible: self.matches(&entry),
            entry,
        };
        let index = match self.by_name.get(&key).copied() {
            Some(existing) => existing,
            None => match self.free.pop() {
                Some(free) => free,
                None => {
                    self.slots.push(None);
                    (self.slots.len() - 1) as u32
                }
            },
        };
        self.slots[index as usize] = Some(slot);
        self.by_name.insert(key, index);
        index
    }

    fn rebuild_order(&mut self) {
        let mut order: Vec<u32> = self
            .slots
            .iter()
            .enumerate()
            .filter(|(_, slot)| slot.as_ref().is_some_and(|slot| slot.visible))
            .map(|(index, _)| index as u32)
            .collect();
        order.sort_unstable_by(|&left, &right| self.compare_slots(left, right));
        self.order = order;
    }

    fn matches(&self, entry: &FileEntry) -> bool {
        self.options.shows(entry)
    }

    fn column_key(&self, entry: &FileEntry) -> ColumnKey {
        match self.options.sort_by.as_str() {
            "size" => ColumnKey::Number(entry.size),
            // "YYYY-MM-DD HH:MM" orders as text the way it orders as a time.
            "modified" | "date" => ColumnKey::Bytes(entry.modified.as_bytes().into()),
            "created" => ColumnKey::Bytes(entry.created.as_bytes().into()),
            "accessed" => ColumnKey::Bytes(entry.accessed.as_bytes().into()),
            "attributes" => ColumnKey::Bytes(self.collator.sort_key(&entry.attributes)),
            "extension" => ColumnKey::Bytes(self.collator.sort_key(&entry.extension)),
            "type" => ColumnKey::Bytes(self.collator.sort_key(if entry.is_dir {
                "folder"
            } else {
                &entry.extension
            })),
            _ => ColumnKey::Name,
        }
    }

    /// The view order: folders first when asked, the sort column in its
    /// direction, then the name ascending. The last two steps only make the
    /// order total, so one entry has exactly one place.
    fn compare_slots(&self, left: u32, right: u32) -> Ordering {
        let (Some(left), Some(right)) = (
            self.slots[left as usize].as_ref(),
            self.slots[right as usize].as_ref(),
        ) else {
            return left.cmp(&right);
        };

        if self.options.folders_first && left.entry.is_dir != right.entry.is_dir {
            return if left.entry.is_dir {
                Ordering::Less
            } else {
                Ordering::Greater
            };
        }

        let column = match (&left.column, &right.column) {
            (ColumnKey::Number(a), ColumnKey::Number(b)) => a.cmp(b),
            (ColumnKey::Bytes(a), ColumnKey::Bytes(b)) => a.cmp(b),
            _ => left.name_key.cmp(&right.name_key),
        };
        let column = if self.options.sort_ascending {
            column
        } else {
            column.reverse()
        };
        column
            .then_with(|| left.name_key.cmp(&right.name_key))
            .then_with(|| left.entry.name.cmp(&right.entry.name))
            .then_with(|| left.entry.path.cmp(&right.entry.path))
    }

    /// The entry as a page carries it. In an ordinary folder the path and the
    /// extension repeat the folder path and the name, so they are left out
    /// and the host puts them back.
    fn wire_entry(&self, entry: &FileEntry) -> FileEntry {
        let mut wire = entry.clone();
        wire.git_status = None;
        if self.paths_follow_names {
            wire.path.clear();
            wire.extension.clear();
        }
        wire
    }
}

fn fold_name(name: &str) -> String {
    crate::native_accel::case_fold_for_sort(name)
}

/// Whether two enumerations of one name describe the same row.
fn same_entry(left: &FileEntry, right: &FileEntry) -> bool {
    left.name == right.name
        && left.path == right.path
        && left.is_dir == right.is_dir
        && left.is_symlink == right.is_symlink
        && left.is_hidden == right.is_hidden
        && left.is_system == right.is_system
        && left.size == right.size
        && left.modified == right.modified
        && left.extension == right.extension
        && left.permissions == right.permissions
        && left.symlink_target == right.symlink_target
        && left.created == right.created
        && left.accessed == right.accessed
        && left.attributes == right.attributes
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(name: &str, is_dir: bool, size: u64) -> FileEntry {
        FileEntry {
            name: name.to_string(),
            path: format!(r"C:\work\{name}"),
            is_dir,
            is_symlink: false,
            is_hidden: name.starts_with('.'),
            is_system: false,
            size,
            modified: "2026-11-01 10:00".to_string(),
            extension: if is_dir {
                String::new()
            } else {
                name.rsplit_once('.')
                    .map(|(_, extension)| extension.to_string())
                    .unwrap_or_default()
            },
            permissions: None,
            symlink_target: None,
            git_status: None,
            created: String::new(),
            accessed: String::new(),
            attributes: String::new(),
        }
    }

    fn listing(entries: Vec<FileEntry>) -> DirectoryListing {
        DirectoryListing {
            path: r"C:\work".to_string(),
            parent: Some(r"C:\".to_string()),
            entries,
            is_network: false,
        }
    }

    fn view(entries: Vec<FileEntry>, options: ListingViewOptions) -> ListingView {
        ListingView::new("primary", listing(entries), true, options)
    }

    #[test]
    fn a_view_sorts_by_created_accessed_and_attributes() {
        let mut a = entry("a.txt", false, 1);
        a.created = "2026-01-03 00:00".to_string();
        a.accessed = "2026-01-01 00:00".to_string();
        a.attributes = "RA".to_string();
        let mut b = entry("b.txt", false, 1);
        b.created = "2026-01-01 00:00".to_string();
        b.accessed = "2026-01-03 00:00".to_string();
        b.attributes = "A".to_string();
        let mut c = entry("c.txt", false, 1);
        c.created = "2026-01-02 00:00".to_string();
        c.accessed = "2026-01-02 00:00".to_string();
        c.attributes = "HA".to_string();
        let by = |column: &str| {
            names(&view(
                vec![a.clone(), b.clone(), c.clone()],
                ListingViewOptions {
                    sort_by: column.to_string(),
                    ..Default::default()
                },
            ))
        };

        assert_eq!(vec!["b.txt", "c.txt", "a.txt"], by("created"));
        assert_eq!(vec!["a.txt", "c.txt", "b.txt"], by("accessed"));
        // The letters sort as text: A, HA, RA.
        assert_eq!(vec!["b.txt", "c.txt", "a.txt"], by("attributes"));
    }

    fn names(view: &ListingView) -> Vec<String> {
        view.page(0, u32::MAX)
            .entries
            .into_iter()
            .map(|entry| entry.name)
            .collect()
    }

    fn sample() -> Vec<FileEntry> {
        vec![
            entry("notes.txt", false, 30),
            entry("Archive", true, 0),
            entry("beta.md", false, 10),
            entry(".hidden", false, 5),
            entry("alpha.TXT", false, 20),
            entry("zeta", true, 0),
        ]
    }

    /// Replays a delta on a plain list of names, the way the host applies it.
    fn replay(mut rows: Vec<String>, delta: &ListingViewDelta) -> Vec<String> {
        for change in &delta.changes {
            let index = change.index as usize;
            match change.kind.as_str() {
                "remove" => {
                    rows.remove(index);
                }
                "insert" => rows.insert(index, change.entry.as_ref().unwrap().name.clone()),
                "update" => rows[index] = change.entry.as_ref().unwrap().name.clone(),
                other => panic!("unknown change kind {other}"),
            }
        }
        rows
    }

    #[test]
    fn default_order_is_folders_first_then_names_ignoring_case() {
        let view = view(sample(), ListingViewOptions::default());
        assert_eq!(
            vec![
                "Archive",
                "zeta",
                ".hidden",
                "alpha.TXT",
                "beta.md",
                "notes.txt"
            ],
            names(&view)
        );
        assert_eq!(6, view.total());
        assert_eq!(1, view.revision());
    }

    #[test]
    fn hidden_entries_and_the_filter_are_left_out_of_the_view_not_the_folder() {
        let view = view(
            sample(),
            ListingViewOptions {
                include_hidden: false,
                filter: Some("  TXT ".to_string()),
                ..ListingViewOptions::default()
            },
        );
        assert_eq!(vec!["alpha.TXT", "notes.txt"], names(&view));
        assert_eq!(2, view.total());
        assert_eq!(6, view.raw_listing().entries.len());
    }

    #[test]
    fn sort_columns_keep_folders_first_and_break_ties_by_name_ascending() {
        let mut view = view(sample(), ListingViewOptions::default());
        assert!(view.set_options(ListingViewOptions {
            sort_by: "size".to_string(),
            sort_ascending: false,
            ..ListingViewOptions::default()
        }));
        // The two folders tie on size 0 and stay in name order.
        assert_eq!(
            vec![
                "Archive",
                "zeta",
                "notes.txt",
                "alpha.TXT",
                "beta.md",
                ".hidden"
            ],
            names(&view)
        );

        assert!(view.set_options(ListingViewOptions {
            sort_by: "name".to_string(),
            sort_ascending: false,
            ..ListingViewOptions::default()
        }));
        assert_eq!(
            vec![
                "zeta",
                "Archive",
                "notes.txt",
                "beta.md",
                "alpha.TXT",
                ".hidden"
            ],
            names(&view)
        );

        assert!(view.set_options(ListingViewOptions {
            sort_by: "extension".to_string(),
            ..ListingViewOptions::default()
        }));
        // ".hidden" has the extension "hidden"; "md" < "txt"; the TXT files tie by name.
        assert_eq!(
            vec![
                "Archive",
                "zeta",
                ".hidden",
                "beta.md",
                "alpha.TXT",
                "notes.txt"
            ],
            names(&view)
        );
    }

    #[test]
    fn folders_mix_with_files_when_folders_first_is_off() {
        let view = view(
            sample(),
            ListingViewOptions {
                folders_first: false,
                include_hidden: false,
                ..ListingViewOptions::default()
            },
        );
        assert_eq!(
            vec!["alpha.TXT", "Archive", "beta.md", "notes.txt", "zeta"],
            names(&view)
        );
    }

    #[test]
    fn type_sort_groups_folders_under_the_word_folder() {
        let view = view(
            vec![
                entry("b.zip", false, 1),
                entry("Docs", true, 0),
                entry("a.exe", false, 1),
            ],
            ListingViewOptions {
                sort_by: "type".to_string(),
                folders_first: false,
                ..ListingViewOptions::default()
            },
        );
        assert_eq!(vec!["a.exe", "Docs", "b.zip"], names(&view));
    }

    #[test]
    fn setting_the_same_options_changes_nothing() {
        let mut view = view(sample(), ListingViewOptions::default());
        assert!(!view.set_options(ListingViewOptions {
            sort_by: " NAME ".to_string(),
            filter: Some("   ".to_string()),
            ..ListingViewOptions::default()
        }));
        assert_eq!(1, view.revision());
    }

    #[test]
    fn views_on_one_clock_never_share_a_revision() {
        let clock = RevisionClock::default();
        let mut first = ListingView::with_clock(
            "primary",
            listing(sample()),
            true,
            ListingViewOptions::default(),
            clock.clone(),
        );
        let second = ListingView::with_clock(
            "primary",
            listing(sample()),
            true,
            ListingViewOptions::default(),
            clock,
        );
        assert_eq!((1, 2), (first.revision(), second.revision()));

        let delta = first.apply_entry("new.txt", Some(entry("new.txt", false, 1)));
        assert_eq!((1, 3), (delta.base_revision, delta.revision));
    }

    #[test]
    fn pages_are_windows_of_the_view_and_stop_at_its_end() {
        let view = view(sample(), ListingViewOptions::default());
        let page = view.page(2, 3);
        assert_eq!(2, page.offset);
        assert_eq!(6, page.total);
        assert_eq!(
            vec![".hidden", "alpha.TXT", "beta.md"],
            page.entries
                .iter()
                .map(|entry| entry.name.as_str())
                .collect::<Vec<_>>()
        );
        assert_eq!(1, view.page(5, 10).entries.len());
        let past = view.page(99, 10);
        assert!(past.entries.is_empty());
        assert_eq!(6, past.offset);
    }

    #[test]
    fn ordinary_folders_send_names_only_and_other_views_send_paths() {
        let plain = view(sample(), ListingViewOptions::default());
        let row = &plain.page(3, 1).entries[0];
        assert_eq!("alpha.TXT", row.name);
        assert!(row.path.is_empty() && row.extension.is_empty());

        let recycle = ListingView::new(
            "primary",
            listing(sample()),
            false,
            ListingViewOptions::default(),
        );
        let row = &recycle.page(3, 1).entries[0];
        assert_eq!(r"C:\work\alpha.TXT", row.path);
        assert_eq!("TXT", row.extension);
    }

    /// Two deleted files with one name are two rows of the Recycle Bin.
    #[test]
    fn entries_that_share_a_name_stay_apart_where_paths_do_not_follow_names() {
        let deleted = |token: &str, size: u64| FileEntry {
            path: format!(r"C:\$Recycle.Bin\S-1\$R{token}.txt"),
            ..entry("notes.txt", false, size)
        };
        let mut view = ListingView::new(
            "primary",
            listing(vec![
                deleted("AAA", 1),
                deleted("BBB", 2),
                entry("other.txt", false, 3),
            ]),
            false,
            ListingViewOptions::default(),
        );
        assert_eq!(3, view.total());
        assert_eq!(vec!["notes.txt", "notes.txt", "other.txt"], names(&view));
        let paths: Vec<String> = view
            .page(0, 2)
            .entries
            .into_iter()
            .map(|e| e.path)
            .collect();
        assert_eq!(
            vec![
                r"C:\$Recycle.Bin\S-1\$RAAA.txt",
                r"C:\$Recycle.Bin\S-1\$RBBB.txt"
            ],
            paths
        );

        // One of the two is restored: exactly its row goes.
        let before = names(&view);
        let delta = view.replace_entries(vec![deleted("BBB", 2), entry("other.txt", false, 3)]);
        assert_eq!(
            vec![("remove", 0)],
            delta
                .changes
                .iter()
                .map(|c| (c.kind.as_str(), c.index))
                .collect::<Vec<_>>()
        );
        assert_eq!(names(&view), replay(before, &delta));
        assert_eq!(
            r"C:\$Recycle.Bin\S-1\$RBBB.txt",
            view.page(0, 1).entries[0].path
        );

        // A change by name cannot say which of several entries it means.
        assert!(!view.follows_names());
        let ignored = view.apply_entry("notes.txt", None);
        assert!(ignored.changes.is_empty());
        assert_eq!(2, view.total());
    }

    #[test]
    fn a_new_entry_is_inserted_at_its_sorted_row() {
        let mut view = view(sample(), ListingViewOptions::default());
        let before = names(&view);
        let delta = view.apply_entry("gamma.txt", Some(entry("gamma.txt", false, 1)));
        assert_eq!(
            (1, 2, 7, false),
            (
                delta.base_revision,
                delta.revision,
                delta.total,
                delta.reset
            )
        );
        assert_eq!(1, delta.changes.len());
        assert_eq!(
            ("insert", 5),
            (delta.changes[0].kind.as_str(), delta.changes[0].index)
        );
        assert_eq!(names(&view), replay(before, &delta));
    }

    #[test]
    fn a_removed_entry_leaves_its_row_and_an_unknown_name_changes_nothing() {
        let mut view = view(sample(), ListingViewOptions::default());
        let before = names(&view);
        let delta = view.apply_entry("BETA.MD", None);
        assert_eq!(
            ("remove", 4),
            (delta.changes[0].kind.as_str(), delta.changes[0].index)
        );
        assert_eq!(names(&view), replay(before, &delta));
        assert_eq!(5, view.raw_listing().entries.len());

        let nothing = view.apply_entry("never-there.txt", None);
        assert!(nothing.changes.is_empty());
        assert_eq!(nothing.base_revision, nothing.revision);
    }

    #[test]
    fn a_changed_entry_updates_in_place_or_moves_with_the_sort() {
        let mut view = view(
            sample(),
            ListingViewOptions {
                sort_by: "size".to_string(),
                ..ListingViewOptions::default()
            },
        );
        // Sizes: .hidden 5, beta.md 10, alpha.TXT 20, notes.txt 30.
        let before = names(&view);
        let same_place = view.apply_entry("beta.md", Some(entry("beta.md", false, 12)));
        assert_eq!(1, same_place.changes.len());
        assert_eq!(
            ("update", 3),
            (
                same_place.changes[0].kind.as_str(),
                same_place.changes[0].index
            )
        );
        assert_eq!(12, same_place.changes[0].entry.as_ref().unwrap().size);

        let moved = view.apply_entry("beta.md", Some(entry("beta.md", false, 99)));
        assert_eq!(
            vec![("remove", 3), ("insert", 5)],
            moved
                .changes
                .iter()
                .map(|change| (change.kind.as_str(), change.index))
                .collect::<Vec<_>>()
        );
        assert_eq!(names(&view), replay(replay(before, &same_place), &moved));
    }

    #[test]
    fn entries_the_filter_hides_change_the_folder_without_a_visible_change() {
        let mut view = view(
            sample(),
            ListingViewOptions {
                filter: Some("txt".to_string()),
                ..ListingViewOptions::default()
            },
        );
        let added = view.apply_entry("photo.png", Some(entry("photo.png", false, 1)));
        assert!(added.changes.is_empty());
        assert_eq!(added.base_revision, added.revision);
        assert_eq!(7, view.raw_listing().entries.len());

        // Renamed out of the filter: the row goes although the file remains.
        let before = names(&view);
        let gone = view.apply_entry("notes.txt", None);
        let _ = view.apply_entry("notes.log", Some(entry("notes.log", false, 30)));
        assert_eq!(
            vec![("remove", 1)],
            gone.changes
                .iter()
                .map(|c| (c.kind.as_str(), c.index))
                .collect::<Vec<_>>()
        );
        assert_eq!(names(&view), replay(before, &gone));

        // Clearing the filter shows what arrived while it was on.
        assert!(view.set_options(ListingViewOptions::default()));
        assert!(names(&view).contains(&"photo.png".to_string()));
    }

    #[test]
    fn a_fresh_enumeration_becomes_the_smallest_delta() {
        let mut view = view(sample(), ListingViewOptions::default());
        let before = names(&view);
        let mut fresh = sample();
        fresh.retain(|entry| entry.name != "beta.md");
        fresh.push(entry("delta.txt", false, 7));
        for entry in &mut fresh {
            if entry.name == "notes.txt" {
                entry.size = 31;
            }
        }

        let delta = view.replace_entries(fresh);
        assert!(!delta.reset);
        assert_eq!(
            vec!["remove", "insert", "update"],
            delta
                .changes
                .iter()
                .map(|change| change.kind.as_str())
                .collect::<Vec<_>>()
        );
        assert_eq!(names(&view), replay(before, &delta));
        assert_eq!(
            vec![
                "Archive",
                "zeta",
                ".hidden",
                "alpha.TXT",
                "delta.txt",
                "notes.txt"
            ],
            names(&view)
        );

        let unchanged = view.replace_entries(view.raw_listing().entries);
        assert!(unchanged.changes.is_empty() && !unchanged.reset);
        assert_eq!(unchanged.base_revision, unchanged.revision);
    }

    #[test]
    fn a_large_difference_is_a_reset_not_hundreds_of_edits() {
        let mut view = view(sample(), ListingViewOptions::default());
        let fresh: Vec<FileEntry> = (0..MAX_DELTA_CHANGES + 10)
            .map(|index| entry(&format!("file{index:04}.bin"), false, index as u64))
            .collect();
        let delta = view.replace_entries(fresh);
        assert!(delta.reset && delta.changes.is_empty());
        assert_eq!((MAX_DELTA_CHANGES + 10) as u32, delta.total);
        assert_eq!(delta.total, view.total());
        assert_eq!("file0000.bin", view.page(0, 1).entries[0].name);
    }

    #[test]
    fn many_single_changes_keep_the_view_equal_to_a_rebuilt_one() {
        let options = ListingViewOptions {
            sort_by: "size".to_string(),
            sort_ascending: false,
            include_hidden: false,
            ..ListingViewOptions::default()
        };
        let mut live = view(Vec::new(), options.clone());
        let mut rows: Vec<String> = Vec::new();
        let mut folder: HashMap<String, FileEntry> = HashMap::new();
        // A fixed pseudo-random walk of adds, changes and removes.
        let mut state = 0x2545_F491_4F6C_DD1Du64;
        for step in 0..600 {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            let name = format!(
                "{}item{}.dat",
                if state.is_multiple_of(11) { "." } else { "" },
                state % 40
            );
            let delta = if state.is_multiple_of(5) {
                folder.remove(&name);
                live.apply_entry(&name, None)
            } else {
                let fresh = entry(&name, state.is_multiple_of(7), (state >> 8) % 50);
                folder.insert(name.clone(), fresh.clone());
                live.apply_entry(&name, Some(fresh))
            };
            rows = replay(rows, &delta);
            assert_eq!(names(&live), rows, "step {step}");
        }

        let rebuilt = view(folder.into_values().collect(), options);
        assert_eq!(names(&rebuilt), names(&live));
    }
}
