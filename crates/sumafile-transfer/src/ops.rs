//! Counts the file-system operations a transfer makes, for the tests and the
//! benchmark.
//!
//! Each call site names the kind of operation and the path it is made on. A
//! [`Counter`] counts the operations on paths under its folder, whatever
//! thread makes them, so tests that run at the same time in folders of their
//! own count only their own. Without the `test-support` feature (the app's
//! build) [`record`] is empty and no counter exists.
//!
//! On Windows the kinds cost these calls: a `Stat` is an open, a query and a
//! close; an `Open` is an open and later a close; each other kind is one call.

use std::path::Path;

/// One kind of file-system operation.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum Op {
    /// Facts or existence asked of a path: the file is opened, asked and
    /// closed again.
    Stat,
    /// A handle opened or created; it is closed later.
    Open,
    /// Facts read through an open handle: size, times, file ID, attributes.
    Query,
    Read,
    Write,
    /// Data forced to the disk.
    Flush,
    /// Times, attributes, length or allocation set through a handle.
    Set,
    Rename,
    Delete,
    /// A byte-range lock taken on an open handle.
    Lock,
    /// A folder created.
    CreateDir,
    /// A folder listed.
    List,
}

impl Op {
    pub const ALL: [Op; 12] = [
        Op::Stat,
        Op::Open,
        Op::Query,
        Op::Read,
        Op::Write,
        Op::Flush,
        Op::Set,
        Op::Rename,
        Op::Delete,
        Op::Lock,
        Op::CreateDir,
        Op::List,
    ];

    #[cfg(any(test, feature = "test-support"))]
    fn index(self) -> usize {
        self as usize
    }

    /// The Windows calls one operation of this kind costs.
    pub fn windows_calls(self) -> u64 {
        match self {
            Op::Stat => 3,
            Op::Open => 2,
            _ => 1,
        }
    }
}

/// Counts `op`, made on `path`, for every counter whose folder holds `path`.
#[cfg(any(test, feature = "test-support"))]
pub fn record(op: Op, path: &Path) {
    counting::record(op, path);
}

/// Counts nothing: the app's build has no counters.
#[cfg(not(any(test, feature = "test-support")))]
#[inline(always)]
pub fn record(_op: Op, _path: &Path) {}

#[cfg(any(test, feature = "test-support"))]
pub use counting::{Counter, Counts};

#[cfg(any(test, feature = "test-support"))]
mod counting {
    use super::Op;
    use std::path::{Path, PathBuf};
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::sync::{Arc, RwLock};

    struct Scope {
        root: PathBuf,
        counts: [AtomicU64; Op::ALL.len()],
    }

    static SCOPES: RwLock<Vec<Arc<Scope>>> = RwLock::new(Vec::new());

    pub(super) fn record(op: Op, path: &Path) {
        let Ok(scopes) = SCOPES.read() else {
            return;
        };
        for scope in scopes.iter() {
            if path.starts_with(&scope.root) {
                scope.counts[op.index()].fetch_add(1, Ordering::Relaxed);
            }
        }
    }

    /// Counts the operations on paths under one folder while it lives.
    pub struct Counter {
        scope: Arc<Scope>,
    }

    impl Counter {
        /// Starts counting the operations on `root` and on every path under it.
        pub fn under(root: &Path) -> Self {
            let scope = Arc::new(Scope {
                root: root.to_path_buf(),
                counts: Default::default(),
            });
            SCOPES
                .write()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .push(scope.clone());
            Self { scope }
        }

        /// What has been counted so far.
        pub fn counts(&self) -> Counts {
            Counts(Op::ALL.map(|op| self.scope.counts[op.index()].load(Ordering::Relaxed)))
        }

        /// Starts again from zero.
        pub fn reset(&self) {
            for count in &self.scope.counts {
                count.store(0, Ordering::Relaxed);
            }
        }
    }

    impl Drop for Counter {
        fn drop(&mut self) {
            SCOPES
                .write()
                .unwrap_or_else(|poisoned| poisoned.into_inner())
                .retain(|scope| !Arc::ptr_eq(scope, &self.scope));
        }
    }

    /// Operations by kind.
    #[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
    pub struct Counts([u64; Op::ALL.len()]);

    impl Counts {
        pub fn get(&self, op: Op) -> u64 {
            self.0[op.index()]
        }

        /// All operations, of every kind.
        pub fn total(&self) -> u64 {
            self.0.iter().sum()
        }

        /// What the operations cost in Windows calls; see [`Op::windows_calls`].
        pub fn windows_calls(&self) -> u64 {
            Op::ALL
                .iter()
                .map(|op| self.get(*op) * op.windows_calls())
                .sum()
        }

        /// The counts of `self` and `other` together.
        pub fn add(&self, other: &Counts) -> Counts {
            Counts(Op::ALL.map(|op| self.get(op) + other.get(op)))
        }

        /// What `self` counted beyond `earlier`.
        pub fn since(&self, earlier: &Counts) -> Counts {
            Counts(Op::ALL.map(|op| self.get(op).saturating_sub(earlier.get(op))))
        }

        /// The kinds that were counted, with their counts, for a message.
        pub fn describe(&self) -> String {
            Op::ALL
                .iter()
                .filter(|op| self.get(**op) > 0)
                .map(|op| format!("{op:?} {}", self.get(*op)))
                .collect::<Vec<_>>()
                .join(", ")
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn folder(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!(
            "sumafile_ops_{name}_{}",
            crate::ids::generate_transfer_operation_id()
        ))
    }

    #[test]
    fn a_counter_counts_the_operations_under_its_folder_only() {
        let mine = folder("mine");
        let other = folder("other");
        let counter = Counter::under(&mine);

        record(Op::Stat, &mine.join("a.txt"));
        record(Op::Stat, &mine.join("sub").join("b.txt"));
        record(Op::Rename, &mine);
        record(Op::Stat, &other.join("a.txt"));

        let counts = counter.counts();
        assert_eq!(counts.get(Op::Stat), 2);
        assert_eq!(counts.get(Op::Rename), 1);
        assert_eq!(counts.total(), 3);
        assert_eq!(counts.describe(), "Stat 2, Rename 1");
    }

    #[test]
    fn a_name_that_only_begins_like_the_folder_is_not_under_it() {
        let mine = folder("prefix");
        let counter = Counter::under(&mine);
        let mut longer = mine.clone().into_os_string();
        longer.push("-not-mine");

        record(Op::Write, &PathBuf::from(longer).join("a.txt"));

        assert_eq!(counter.counts().total(), 0);
    }

    #[test]
    fn operations_on_other_threads_are_counted() {
        let mine = folder("threads");
        let counter = Counter::under(&mine);
        std::thread::scope(|scope| {
            for _ in 0..4 {
                scope.spawn(|| {
                    for _ in 0..100 {
                        record(Op::Write, &mine.join("a.bin"));
                    }
                });
            }
        });
        assert_eq!(counter.counts().get(Op::Write), 400);
    }

    #[test]
    fn a_dropped_counter_counts_nothing_more_and_others_go_on() {
        let mine = folder("dropped");
        let first = Counter::under(&mine);
        let second = Counter::under(&mine);
        drop(first);

        record(Op::Flush, &mine.join("a.bin"));

        assert_eq!(second.counts().get(Op::Flush), 1);
    }

    #[test]
    fn counts_convert_to_windows_calls_and_subtract() {
        let mine = folder("calls");
        let counter = Counter::under(&mine);
        record(Op::Stat, &mine.join("a"));
        record(Op::Open, &mine.join("a"));
        record(Op::Write, &mine.join("a"));
        let before = counter.counts();
        record(Op::Rename, &mine.join("a"));

        assert_eq!(before.windows_calls(), 3 + 2 + 1);
        assert_eq!(counter.counts().since(&before).describe(), "Rename 1");
        counter.reset();
        assert_eq!(counter.counts().total(), 0);
    }
}
