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
    let db = crate::settings_store::open_metadata_db().ok()?;
    get_cached_fact_in(&db, path, kind, size, modified)
}

pub fn put_cached_fact(fact: &EntryFact, size: u64, modified: &str) -> Result<(), String> {
    let db = crate::settings_store::open_metadata_db()?;
    put_cached_fact_in(&db, fact, size, modified)
}

/// The cached fact for `path`, when it was stored for a file of this `size` and `modified` time.
pub fn get_cached_fact_in(
    connection: &Connection,
    path: &str,
    kind: FactKind,
    size: u64,
    modified: &str,
) -> Option<EntryFact> {
    let (cached_size, cached_modified, state, text, sort): (i64, String, String, String, Option<f64>) = connection
        .query_row(
            "SELECT size, modified, state, text, sort FROM entry_facts WHERE path = ?1 AND kind = ?2",
            params![key(path), kind.as_wire()],
            |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?, row.get(4)?)),
        )
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
    connection
        .execute(
            "INSERT OR REPLACE INTO entry_facts (path, kind, size, modified, state, text, sort)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            params![
                key(&fact.path),
                fact.kind,
                i64::try_from(size).unwrap_or(i64::MAX),
                modified,
                fact.state,
                fact.text,
                fact.sort
            ],
        )
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
}
