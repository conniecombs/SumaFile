//! The persistent cache of file facts (lazy column values) in `metadata.db`:
//! a file is read once, and again only when its size or modified time
//! changes. Failed and unsupported outcomes are cached too, so a damaged
//! file is not re-read on every visit.

use crate::entry_facts::FactKind;
use crate::models::EntryFact;
use rusqlite::{params, Connection, OptionalExtension};

/// Paths are compared the way Windows does: without regard to case.
fn key(path: &str) -> String {
    path.trim_end_matches(['\\', '/']).to_lowercase()
}

pub fn get_cached_fact(path: &str, kind: FactKind, size: u64, modified: &str) -> Option<EntryFact> {
    let db = crate::settings_store::open_metadata_db_reader().ok()?;
    get_cached_fact_in(&db, path, kind, size, modified)
}

pub fn get_cached_facts_for_path(
    path: &str,
    kinds: &[FactKind],
    size: u64,
    modified: &str,
) -> Vec<(FactKind, Option<EntryFact>)> {
    let Ok(db) = crate::settings_store::open_metadata_db_reader() else {
        return kinds.iter().map(|kind| (*kind, None)).collect();
    };
    kinds
        .iter()
        .map(|kind| (*kind, get_cached_fact_in(&db, path, *kind, size, modified)))
        .collect()
}

pub fn put_cached_fact(fact: &EntryFact, size: u64, modified: &str) -> Result<(), String> {
    let db = crate::settings_store::open_metadata_db()?;
    put_cached_fact_in(&db, fact, size, modified)
}

pub fn put_cached_facts_batch(items: &[(EntryFact, u64, String)]) -> Result<(), String> {
    if items.is_empty() {
        return Ok(());
    }
    let db = crate::settings_store::open_metadata_db()?;
    let tx = db
        .unchecked_transaction()
        .map_err(|error| error.to_string())?;
    for (fact, size, modified) in items {
        put_cached_fact_in(&tx, fact, *size, modified)?;
    }
    tx.commit().map_err(|error| error.to_string())?;
    if prune_is_due(&FACTS_WRITTEN, items.len() as u64) {
        // The facts are stored either way; a failed prune is tried again later.
        let _ = prune_entry_facts(&db, MAX_ENTRY_FACTS);
    }
    Ok(())
}

/// The most cached facts kept. A fact is a row of about a hundred bytes for
/// one file and one column, and nothing else ever removed one: the table grew
/// with every folder shown with a lazy column for as long as the profile
/// existed, and with it every lookup.
pub(crate) const MAX_ENTRY_FACTS: i64 = 200_000;

/// Facts written between two checks of the table's size.
const PRUNE_INTERVAL: u64 = 4096;

static FACTS_WRITTEN: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

/// True on the first write of a process and each time another
/// [`PRUNE_INTERVAL`] facts have been written.
fn prune_is_due(counter: &std::sync::atomic::AtomicU64, written: u64) -> bool {
    let before = counter.fetch_add(written, std::sync::atomic::Ordering::Relaxed);
    before == 0 || before / PRUNE_INTERVAL != (before + written) / PRUNE_INTERVAL
}

/// When the table holds more than `limit` facts, removes the ones written
/// longest ago until nine tenths of `limit` are left, and returns how many
/// went. A fact that is stored again is written anew, so it counts as recent.
pub(crate) fn prune_entry_facts(connection: &Connection, limit: i64) -> rusqlite::Result<usize> {
    let count: i64 =
        connection.query_row("SELECT COUNT(*) FROM entry_facts", [], |row| row.get(0))?;
    if count <= limit {
        return Ok(0);
    }
    let keep = limit / 10 * 9;
    // The row just older than the newest `keep` rows, and everything before it.
    connection.execute(
        "DELETE FROM entry_facts WHERE rowid <= (
            SELECT rowid FROM entry_facts ORDER BY rowid DESC LIMIT 1 OFFSET ?1
        )",
        [keep],
    )
}

/// The cached fact for `path`, when it was stored for a file of this `size` and `modified` time.
pub fn get_cached_fact_in(
    connection: &Connection,
    path: &str,
    kind: FactKind,
    size: u64,
    modified: &str,
) -> Option<EntryFact> {
    let mut stmt = connection
        .prepare_cached(
            "SELECT size, modified, state, text, sort FROM entry_facts WHERE path = ?1 AND kind = ?2",
        )
        .ok()?;
    let (cached_size, cached_modified, state, text, sort): (
        i64,
        String,
        String,
        String,
        Option<f64>,
    ) = stmt
        .query_row(params![key(path), kind.as_wire()], |row| {
            Ok((
                row.get(0)?,
                row.get(1)?,
                row.get(2)?,
                row.get(3)?,
                row.get(4)?,
            ))
        })
        .optional()
        .ok()
        .flatten()?;
    if cached_size != i64::try_from(size).ok()? || cached_modified != modified {
        return None;
    }
    Some(EntryFact {
        path: path.to_string(),
        kind: kind.as_wire().to_string(),
        state,
        text,
        sort,
    })
}

pub fn put_cached_fact_in(
    connection: &Connection,
    fact: &EntryFact,
    size: u64,
    modified: &str,
) -> Result<(), String> {
    let mut stmt = connection
        .prepare_cached(
            "INSERT OR REPLACE INTO entry_facts (path, kind, size, modified, state, text, sort)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
        )
        .map_err(|error| error.to_string())?;
    stmt.execute(params![
        key(&fact.path),
        fact.kind,
        i64::try_from(size).unwrap_or(i64::MAX),
        modified,
        fact.state,
        fact.text,
        fact.sort
    ])
    .map(|_| ())
    .map_err(|error| error.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::entry_facts::{STATE_FAILED, STATE_READY};
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_db(name: &str) -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!(
            "sumafile-facts-{name}-{}-{nanos}.db",
            std::process::id()
        ))
    }

    fn fact(path: &str, state: &str, text: &str, sort: Option<f64>) -> EntryFact {
        EntryFact {
            path: path.to_string(),
            kind: FactKind::Dimensions.as_wire().to_string(),
            state: state.to_string(),
            text: text.to_string(),
            sort,
        }
    }

    #[test]
    fn a_cached_fact_is_returned_only_for_the_same_size_and_modified_time() {
        let db = crate::settings_store::open_metadata_db_at(&temp_db("same")).unwrap();
        let stored = fact(
            r"C:\Photos\IMG_0001.JPG",
            STATE_READY,
            "6000 × 4000",
            Some(24_000_000.0),
        );
        put_cached_fact_in(&db, &stored, 4_200_000, "2026-11-01 10:00").unwrap();

        // Found for the same file, whatever the letter case of the path.
        assert_eq!(
            Some(fact(
                r"c:\photos\img_0001.jpg",
                STATE_READY,
                "6000 × 4000",
                Some(24_000_000.0)
            )),
            get_cached_fact_in(
                &db,
                r"c:\photos\img_0001.jpg",
                FactKind::Dimensions,
                4_200_000,
                "2026-11-01 10:00"
            )
        );
        // Not for a rewritten one.
        assert_eq!(
            None,
            get_cached_fact_in(
                &db,
                r"C:\Photos\IMG_0001.JPG",
                FactKind::Dimensions,
                4_200_001,
                "2026-11-01 10:00"
            )
        );
        assert_eq!(
            None,
            get_cached_fact_in(
                &db,
                r"C:\Photos\IMG_0001.JPG",
                FactKind::Dimensions,
                4_200_000,
                "2026-11-02 09:00"
            )
        );
        // Nor for another kind of fact.
        assert_eq!(
            None,
            get_cached_fact_in(
                &db,
                r"C:\Photos\IMG_0001.JPG",
                FactKind::Duration,
                4_200_000,
                "2026-11-01 10:00"
            )
        );
    }

    #[test]
    fn a_failed_fact_is_cached_until_the_file_changes_and_a_new_value_replaces_the_old() {
        let db = crate::settings_store::open_metadata_db_at(&temp_db("failed")).unwrap();
        put_cached_fact_in(&db, &fact(r"C:\x.png", STATE_FAILED, "", None), 10, "t1").unwrap();
        assert_eq!(
            STATE_FAILED,
            get_cached_fact_in(&db, r"C:\x.png", FactKind::Dimensions, 10, "t1")
                .unwrap()
                .state
        );
        assert_eq!(
            None,
            get_cached_fact_in(&db, r"C:\x.png", FactKind::Dimensions, 11, "t2")
        );

        put_cached_fact_in(
            &db,
            &fact(r"C:\x.png", STATE_READY, "8 × 8", Some(64.0)),
            11,
            "t2",
        )
        .unwrap();
        assert_eq!(
            "8 × 8",
            get_cached_fact_in(&db, r"C:\x.png", FactKind::Dimensions, 11, "t2")
                .unwrap()
                .text
        );
        assert_eq!(
            None,
            get_cached_fact_in(&db, r"C:\x.png", FactKind::Dimensions, 10, "t1")
        );
    }

    fn fact_count(db: &Connection) -> i64 {
        db.query_row("SELECT COUNT(*) FROM entry_facts", [], |row| row.get(0))
            .unwrap()
    }

    #[test]
    fn pruning_keeps_the_facts_written_last() {
        let db = crate::settings_store::open_metadata_db_at(&temp_db("prune")).unwrap();
        let limit = 100;
        for index in 0..limit + 50 {
            let path = format!(r"C:\Photos\IMG_{index:04}.JPG");
            put_cached_fact_in(&db, &fact(&path, STATE_READY, "1 × 1", Some(1.0)), 1, "t").unwrap();
        }
        // The oldest fact is stored again, which makes it the newest.
        let first = fact(r"C:\Photos\IMG_0000.JPG", STATE_READY, "2 × 2", Some(4.0));
        put_cached_fact_in(&db, &first, 2, "t2").unwrap();
        assert_eq!(fact_count(&db), limit + 50);

        let removed = prune_entry_facts(&db, limit).unwrap();

        assert_eq!(removed as i64, 60);
        assert_eq!(fact_count(&db), 90);
        let cached = |index: i64| {
            let path = format!(r"C:\Photos\IMG_{index:04}.JPG");
            get_cached_fact_in(&db, &path, FactKind::Dimensions, 1, "t").is_some()
        };
        assert!(!cached(1), "the oldest facts went");
        assert!(!cached(60));
        assert!(cached(61), "the newest ninety stay");
        assert!(cached(149));
        assert!(
            get_cached_fact_in(&db, &first.path, FactKind::Dimensions, 2, "t2").is_some(),
            "a fact stored again is recent"
        );
    }

    #[test]
    fn pruning_leaves_a_table_within_its_limit_alone() {
        let db = crate::settings_store::open_metadata_db_at(&temp_db("prune-small")).unwrap();
        for index in 0..100 {
            let path = format!(r"C:\Photos\IMG_{index:04}.JPG");
            put_cached_fact_in(&db, &fact(&path, STATE_READY, "1 × 1", Some(1.0)), 1, "t").unwrap();
        }
        assert_eq!(prune_entry_facts(&db, 100).unwrap(), 0);
        assert_eq!(fact_count(&db), 100);
    }

    #[test]
    fn a_prune_is_due_on_the_first_write_and_once_per_interval() {
        let written = std::sync::atomic::AtomicU64::new(0);
        assert!(prune_is_due(&written, 10), "the first write of a process");
        assert!(!prune_is_due(&written, 10));
        assert!(!prune_is_due(&written, PRUNE_INTERVAL - 21));
        assert!(
            prune_is_due(&written, 1),
            "the write that reaches the interval"
        );
        assert!(!prune_is_due(&written, PRUNE_INTERVAL - 1));
        assert!(
            prune_is_due(&written, 5 * PRUNE_INTERVAL),
            "a large batch counts once"
        );
    }
}
