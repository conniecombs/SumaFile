//! The listing views a session holds: one sorted, filtered folder per pane.
//!
//! The registry is shared between the session loop (which opens, pages and
//! closes views) and the watcher thread (which applies file changes to them).

use crate::session_cache::SessionCache;
use simplefile_core::listing_view::{ListingView, ListingViewOptions, RevisionClock};
use simplefile_core::models::{DirectoryListing, FileEntry, ListingViewDelta, ListingViewPage};
use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex, MutexGuard};

/// The most rows one page request returns.
pub const MAX_PAGE_ROWS: u32 = 8_192;
/// The first page when the host names no size.
pub const DEFAULT_PAGE_ROWS: u32 = 512;

/// What a file change did to the open views.
#[derive(Debug, Default)]
pub struct ViewChanges {
    /// Deltas to send, one per view whose rows changed.
    pub deltas: Vec<ListingViewDelta>,
    /// Views whose folder changed as a whole and must be enumerated again.
    pub refresh: Vec<String>,
}

#[derive(Default)]
struct Registry {
    views: HashMap<String, ListingView>,
    /// The newest open that is enumerating a folder for each view id.
    opening: HashMap<String, u64>,
    last_ticket: u64,
}

/// Marks one open of a view. A pane that navigates twice in quick succession
/// has two enumerations under way for one view id, and the slower folder may
/// finish last; only the open that was started last may become the view.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct OpenTicket(u64);

#[derive(Clone, Default)]
pub struct ListingViews {
    inner: Arc<Mutex<Registry>>,
    /// One clock for the session: no two views, and no two views of one id,
    /// ever report the same revision.
    clock: RevisionClock,
}

impl ListingViews {
    fn lock(&self) -> MutexGuard<'_, Registry> {
        // A panic while a view was being edited leaves a view that is still
        // a valid (if stale) list; the next refresh corrects it.
        self.inner
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// Starts a navigation of `view_id`. Every open of the id that began
    /// earlier and has not finished is superseded, whether this one goes on
    /// to enumerate a folder or finds that the view already shows it.
    pub fn begin_open(&self, view_id: &str) -> OpenTicket {
        let mut registry = self.lock();
        registry.last_ticket += 1;
        let ticket = registry.last_ticket;
        registry.opening.insert(view_id.to_string(), ticket);
        OpenTicket(ticket)
    }

    /// Stores the view `view_id` over an enumeration of a folder, replacing
    /// the view that had the id, and returns its first page. When the
    /// replaced view showed another, cacheable folder, its entries come back
    /// too: the pane navigated away and the cache may keep them. `None` when
    /// a later open of the id superseded `ticket`: nothing is stored.
    pub fn open(
        &self,
        view_id: &str,
        ticket: OpenTicket,
        listing: DirectoryListing,
        paths_follow_names: bool,
        options: ListingViewOptions,
        page_rows: u32,
    ) -> Option<(ListingViewPage, Option<DirectoryListing>)> {
        let view = ListingView::with_clock(
            view_id,
            listing,
            paths_follow_names,
            options,
            self.clock.clone(),
        );
        let page = view.page(0, clamp_page_rows(page_rows));
        let mut registry = self.lock();
        if registry.opening.get(view_id) != Some(&ticket.0) {
            return None;
        }
        registry.opening.remove(view_id);
        let replaced = registry.views.insert(view_id.to_string(), view);
        let left_behind = replaced
            .filter(|old| old.is_cacheable() && !same_folder(old.path(), &page.path))
            .map(ListingView::into_raw_listing);
        Some((page, left_behind))
    }

    /// Ends a navigation that needed no enumeration (the view already showed
    /// the folder). Opens it superseded stay superseded.
    pub fn finish_open(&self, view_id: &str, ticket: OpenTicket) {
        let mut registry = self.lock();
        if registry.opening.get(view_id) == Some(&ticket.0) {
            registry.opening.remove(view_id);
        }
    }

    /// Re-sorts and re-filters the view `view_id` when it already shows
    /// `path`, and returns its first page. `None` when there is no such view
    /// of that folder. This alone supersedes nothing: a pane that only asks
    /// for another order must not cancel the navigation it started since.
    pub fn reconfigure(
        &self,
        view_id: &str,
        path: &str,
        options: ListingViewOptions,
        page_rows: u32,
    ) -> Option<ListingViewPage> {
        let mut registry = self.lock();
        let view = registry.views.get_mut(view_id)?;
        if !same_folder(view.path(), path) {
            return None;
        }
        view.set_options(options);
        Some(view.page(0, clamp_page_rows(page_rows)))
    }

    pub fn range(&self, view_id: &str, offset: u32, count: u32) -> Result<ListingViewPage, String> {
        self.lock()
            .views
            .get(view_id)
            .map(|view| view.page(offset, clamp_page_rows(count)))
            .ok_or_else(|| unknown_view(view_id))
    }

    /// The folder the view shows.
    pub fn path_of(&self, view_id: &str) -> Option<String> {
        self.lock()
            .views
            .get(view_id)
            .map(|view| view.path().to_string())
    }

    /// Replaces the view's entries with a fresh enumeration of its folder and
    /// returns what changed. `None` when the view is gone or shows another
    /// folder by now (the host navigated while the folder was enumerated).
    pub fn refresh(
        &self,
        view_id: &str,
        path: &str,
        entries: Vec<FileEntry>,
    ) -> Option<ListingViewDelta> {
        let mut registry = self.lock();
        let view = registry.views.get_mut(view_id)?;
        if !same_folder(view.path(), path) {
            return None;
        }
        Some(view.replace_entries(entries))
    }

    /// Closes the view. The entries it held come back when the cache may
    /// keep them (an ordinary local folder).
    pub fn close(&self, view_id: &str) -> Option<DirectoryListing> {
        self.lock()
            .views
            .remove(view_id)
            .filter(ListingView::is_cacheable)
            .map(ListingView::into_raw_listing)
    }

    /// Closes every view (the session ended).
    pub fn clear(&self) {
        let mut registry = self.lock();
        registry.views.clear();
        registry.opening.clear();
    }

    /// Applies a change of `path` to the views of its folder. `lookup` reads
    /// what the path is now: `Some` entry, or `None` when it no longer exists.
    /// A change of a viewed folder itself (the watcher reports a storm of
    /// changes that way) asks for those views to be refreshed instead.
    pub fn apply_path_change(
        &self,
        path: &str,
        lookup: impl Fn(&str) -> Option<FileEntry>,
    ) -> ViewChanges {
        let mut changes = ViewChanges::default();
        let target = Path::new(path);
        let (Some(folder), Some(name)) = (
            target.parent().map(|parent| parent.to_string_lossy()),
            target.file_name().map(|name| name.to_string_lossy()),
        ) else {
            return changes;
        };

        let mut registry = self.lock();
        // Looked up once, and only when some view shows the folder.
        let mut current: Option<Option<FileEntry>> = None;
        for (view_id, view) in registry.views.iter_mut() {
            if same_folder(view.path(), path) {
                changes.refresh.push(view_id.clone());
            } else if same_folder(view.path(), &folder) {
                if !view.follows_names() {
                    // Names may repeat there: only a fresh enumeration can
                    // say which entry changed.
                    changes.refresh.push(view_id.clone());
                    continue;
                }
                let entry = current.get_or_insert_with(|| lookup(path)).clone();
                let delta = view.apply_entry(&name, entry);
                if !delta.changes.is_empty() || delta.reset {
                    changes.deltas.push(delta);
                }
            }
        }
        changes
    }
}

fn clamp_page_rows(rows: u32) -> u32 {
    if rows == 0 {
        DEFAULT_PAGE_ROWS
    } else {
        rows.min(MAX_PAGE_ROWS)
    }
}

fn same_folder(left: &str, right: &str) -> bool {
    SessionCache::normalize_key(left) == SessionCache::normalize_key(right)
}

fn unknown_view(view_id: &str) -> String {
    format!("Listing view is not open: {view_id}")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn entry(name: &str, size: u64) -> FileEntry {
        FileEntry {
            name: name.to_string(),
            path: format!(r"C:\work\{name}"),
            is_dir: false,
            is_symlink: false,
            is_hidden: false,
            is_system: false,
            size,
            modified: "2026-11-01 10:00".to_string(),
            extension: "txt".to_string(),
            permissions: None,
            symlink_target: None,
            git_status: None,
        }
    }

    fn listing(path: &str, names: &[&str]) -> DirectoryListing {
        DirectoryListing {
            path: path.to_string(),
            parent: None,
            entries: names.iter().map(|name| entry(name, 1)).collect(),
            is_network: false,
        }
    }

    /// An open with nothing else under way for the view.
    fn open_now(
        views: &ListingViews,
        view_id: &str,
        listing: DirectoryListing,
        paths_follow_names: bool,
        options: ListingViewOptions,
        page_rows: u32,
    ) -> (ListingViewPage, Option<DirectoryListing>) {
        let ticket = views.begin_open(view_id);
        views
            .open(
                view_id,
                ticket,
                listing,
                paths_follow_names,
                options,
                page_rows,
            )
            .expect("no later open superseded this one")
    }

    /// A pane on folder A starts opening slow folder B, then goes back to A
    /// before B finishes. A needs no enumeration, and B must still lose.
    #[test]
    fn going_back_to_the_folder_shown_supersedes_the_open_it_interrupted() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\a", &["a.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        let slow = views.begin_open("primary");

        let back = views.begin_open("primary");
        let page = views
            .reconfigure("primary", r"C:\a", ListingViewOptions::default(), 0)
            .expect("the view still shows the folder");
        views.finish_open("primary", back);
        assert_eq!(r"C:\a", page.path);

        assert!(views
            .open(
                "primary",
                slow,
                listing(r"C:\b", &["b.txt"]),
                true,
                ListingViewOptions::default(),
                0
            )
            .is_none());
        assert_eq!(Some(r"C:\a".to_string()), views.path_of("primary"));
    }

    /// Asking the view for another order is not a navigation: it leaves an
    /// open that is under way alone.
    #[test]
    fn a_reconfiguration_alone_supersedes_nothing() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\a", &["a.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        let navigation = views.begin_open("primary");

        assert!(views
            .reconfigure("primary", r"C:\a", ListingViewOptions::default(), 0)
            .is_some());

        let (page, _) = views
            .open(
                "primary",
                navigation,
                listing(r"C:\b", &["b.txt"]),
                true,
                ListingViewOptions::default(),
                0,
            )
            .expect("the navigation was not superseded");
        assert_eq!(r"C:\b", page.path);
    }

    #[test]
    fn a_change_in_a_folder_whose_names_may_repeat_asks_for_a_refresh() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\bin", &["notes.txt"]),
            false,
            ListingViewOptions::default(),
            0,
        );

        let changes =
            views.apply_path_change(r"C:\bin\notes.txt", |_| panic!("not looked up by name"));

        assert_eq!(vec!["primary".to_string()], changes.refresh);
        assert!(changes.deltas.is_empty());
    }

    #[test]
    fn the_open_started_last_becomes_the_view_whichever_finishes_last() {
        let views = ListingViews::default();
        // The pane navigates to a slow folder, then at once to a quick one.
        let slow = views.begin_open("primary");
        let quick = views.begin_open("primary");
        let other_pane = views.begin_open("secondary");

        let (page, _) = views
            .open(
                "primary",
                quick,
                listing(r"C:\quick", &["q.txt"]),
                true,
                ListingViewOptions::default(),
                0,
            )
            .expect("the newest open");
        assert_eq!(r"C:\quick", page.path);
        // The slow folder finishes afterwards and must not replace it.
        assert!(views
            .open(
                "primary",
                slow,
                listing(r"C:\slow", &["s.txt"]),
                true,
                ListingViewOptions::default(),
                0
            )
            .is_none());
        assert_eq!(Some(r"C:\quick".to_string()), views.path_of("primary"));
        // Opens of another view are not affected.
        assert!(views
            .open(
                "secondary",
                other_pane,
                listing(r"C:\slow", &["s.txt"]),
                true,
                ListingViewOptions::default(),
                0
            )
            .is_some());
    }

    fn names(page: &ListingViewPage) -> Vec<&str> {
        page.entries
            .iter()
            .map(|entry| entry.name.as_str())
            .collect()
    }

    #[test]
    fn open_returns_the_first_page_and_range_the_rest() {
        let views = ListingViews::default();
        let (first, left_behind) = open_now(
            &views,
            "primary",
            listing(r"C:\work", &["c.txt", "a.txt", "b.txt"]),
            true,
            ListingViewOptions::default(),
            2,
        );
        assert!(left_behind.is_none());
        assert_eq!((3, 0), (first.total, first.offset));
        assert_eq!(vec!["a.txt", "b.txt"], names(&first));

        let rest = views.range("primary", 2, 50).expect("the view is open");
        assert_eq!(vec!["c.txt"], names(&rest));
        assert_eq!(first.revision, rest.revision);
        assert!(views.range("secondary", 0, 1).is_err());
    }

    #[test]
    fn reconfigure_resorts_the_same_folder_and_refuses_another() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\work", &["a.txt", "b.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        let descending = ListingViewOptions {
            sort_ascending: false,
            ..ListingViewOptions::default()
        };

        let page = views
            .reconfigure("primary", r"c:\WORK\", descending.clone(), 0)
            .expect("the same folder, spelled differently");
        assert_eq!(vec!["b.txt", "a.txt"], names(&page));
        assert!(views
            .reconfigure("primary", r"C:\other", descending.clone(), 0)
            .is_none());
        assert!(views
            .reconfigure("secondary", r"C:\work", descending, 0)
            .is_none());
    }

    #[test]
    fn a_file_change_reaches_every_view_of_its_folder_and_no_other() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\work", &["a.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        open_now(
            &views,
            "secondary",
            listing(r"C:\WORK", &["a.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        open_now(
            &views,
            "tab",
            listing(r"C:\elsewhere", &["a.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );

        let added = views.apply_path_change(r"C:\work\b.txt", |_| Some(entry("b.txt", 9)));
        let mut touched: Vec<&str> = added
            .deltas
            .iter()
            .map(|delta| delta.view_id.as_str())
            .collect();
        touched.sort();
        assert_eq!(vec!["primary", "secondary"], touched);
        assert!(added.refresh.is_empty());
        assert_eq!(2, views.range("primary", 0, 10).unwrap().total);
        assert_eq!(1, views.range("tab", 0, 10).unwrap().total);

        let removed = views.apply_path_change(r"C:\work\a.txt", |_| None);
        assert_eq!(2, removed.deltas.len());
        assert_eq!(
            vec!["b.txt"],
            names(&views.range("secondary", 0, 10).unwrap())
        );

        let unseen = views.apply_path_change(r"C:\work\ghost.txt", |_| None);
        assert!(unseen.deltas.is_empty() && unseen.refresh.is_empty());
    }

    #[test]
    fn a_change_of_the_folder_itself_asks_for_a_refresh() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\work", &["a.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );

        let storm = views.apply_path_change(r"C:\work", |_| panic!("the folder is not looked up"));
        assert_eq!(vec!["primary".to_string()], storm.refresh);
        assert!(storm.deltas.is_empty());

        let delta = views
            .refresh("primary", r"C:\work", vec![entry("z.txt", 1)])
            .expect("the view still shows the folder");
        assert_eq!(1, delta.total);
        assert!(views
            .refresh("primary", r"C:\moved-on", Vec::new())
            .is_none());
    }

    #[test]
    fn navigating_a_view_hands_back_the_folder_it_left_but_a_reload_does_not() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\work", &["a.txt", "b.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );

        let (_, reloaded) = open_now(
            &views,
            "primary",
            listing(r"c:\WORK", &["a.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        assert!(reloaded.is_none());

        let (page, left) = open_now(
            &views,
            "primary",
            listing(r"C:\next", &["n.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        assert_eq!(r"C:\next", page.path);
        // The view it left is the reloaded one, with the reload's spelling of the folder and its one entry.
        let left = left.expect("the folder the pane left");
        assert_eq!((r"c:\WORK", 1), (left.path.as_str(), left.entries.len()));

        // An archive folder or the Recycle Bin is never handed to the cache.
        open_now(
            &views,
            "secondary",
            listing(r"C:\a.zip", &["x.txt"]),
            false,
            ListingViewOptions::default(),
            0,
        );
        let (_, uncached) = open_now(
            &views,
            "secondary",
            listing(r"C:\next", &["n.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        assert!(uncached.is_none());
        assert!(views.close("secondary").is_some());
    }

    #[test]
    fn close_hands_back_the_entries_and_forgets_the_view() {
        let views = ListingViews::default();
        open_now(
            &views,
            "primary",
            listing(r"C:\work", &["a.txt", "b.txt"]),
            true,
            ListingViewOptions::default(),
            0,
        );
        assert_eq!(Some(r"C:\work".to_string()), views.path_of("primary"));

        let raw = views.close("primary").expect("the view was open");
        assert_eq!(2, raw.entries.len());
        assert!(views.close("primary").is_none());
        assert!(views.path_of("primary").is_none());
    }
}
