use crate::models::Tag;
use crate::settings_store::open_metadata_db;
use rusqlite::{params, OptionalExtension};
use std::collections::HashMap;

pub fn get_all_tags() -> Result<Vec<Tag>, String> {
    let connection = open_metadata_db()?;
    let mut statement = connection
        .prepare_cached("SELECT id, name, color FROM tags ORDER BY name")
        .map_err(|error| error.to_string())?;

    let rows = statement
        .query_map([], read_tag)
        .map_err(|error| error.to_string())?;

    collect_rows(rows)
}

pub fn create_tag(name: String, color: String) -> Result<Tag, String> {
    validate_tag_input(&name, &color)?;
    let connection = open_metadata_db()?;

    connection
        .execute(
            "INSERT INTO tags (name, color) VALUES (?1, ?2)",
            params![name, color],
        )
        .map_err(|error| error.to_string())?;

    Ok(Tag {
        id: connection.last_insert_rowid(),
        name,
        color,
    })
}

pub fn update_tag(id: i64, name: String, color: String) -> Result<Tag, String> {
    validate_tag_input(&name, &color)?;
    validate_tag_id(id)?;
    let connection = open_metadata_db()?;

    let changed = connection
        .execute(
            "UPDATE tags SET name = ?1, color = ?2 WHERE id = ?3",
            params![name, color, id],
        )
        .map_err(|error| error.to_string())?;
    if changed == 0 {
        return Err(format!("Tag not found: {id}"));
    }

    Ok(Tag { id, name, color })
}

pub fn delete_tag(id: i64) -> Result<(), String> {
    validate_tag_id(id)?;
    let connection = open_metadata_db()?;
    connection
        .execute("DELETE FROM tags WHERE id = ?1", params![id])
        .map_err(|error| error.to_string())?;
    Ok(())
}

pub fn get_tags_for_path(path: String) -> Result<Vec<Tag>, String> {
    validate_path_key(&path)?;
    let normalized = normalize_tag_path(&path);
    let connection = open_metadata_db()?;
    #[cfg(windows)]
    const SQL: &str = "SELECT DISTINCT t.id, t.name, t.color
             FROM tags t
             JOIN file_tags ft ON t.id = ft.tag_id
             WHERE ft.file_path = ?1 COLLATE NOCASE
             ORDER BY t.name";
    #[cfg(not(windows))]
    const SQL: &str = "SELECT t.id, t.name, t.color
             FROM tags t
             JOIN file_tags ft ON t.id = ft.tag_id
             WHERE ft.file_path = ?1
             ORDER BY t.name";

    let mut statement = connection
        .prepare_cached(SQL)
        .map_err(|error| error.to_string())?;

    let rows = statement
        .query_map(params![normalized], read_tag)
        .map_err(|error| error.to_string())?;

    collect_rows(rows)
}

pub fn set_tags_for_path(path: String, tag_ids: Vec<i64>) -> Result<(), String> {
    validate_path_key(&path)?;
    for tag_id in &tag_ids {
        validate_tag_id(*tag_id)?;
    }
    let normalized = normalize_tag_path(&path);

    let mut connection = open_metadata_db()?;
    let transaction = connection
        .transaction()
        .map_err(|error| error.to_string())?;
    #[cfg(windows)]
    const DELETE_SQL: &str = "DELETE FROM file_tags WHERE file_path = ?1 COLLATE NOCASE";
    #[cfg(not(windows))]
    const DELETE_SQL: &str = "DELETE FROM file_tags WHERE file_path = ?1";

    transaction
        .execute(DELETE_SQL, params![normalized])
        .map_err(|error| error.to_string())?;

    let mut seen = std::collections::HashSet::with_capacity(tag_ids.len());
    for tag_id in tag_ids {
        if !seen.insert(tag_id) {
            continue;
        }
        let exists: Option<i64> = transaction
            .query_row(
                "SELECT id FROM tags WHERE id = ?1",
                params![tag_id],
                |row| row.get(0),
            )
            .optional()
            .map_err(|error| error.to_string())?;
        if exists.is_none() {
            return Err(format!("Tag not found: {tag_id}"));
        }

        transaction
            .execute(
                "INSERT INTO file_tags (file_path, tag_id) VALUES (?1, ?2)",
                params![normalized, tag_id],
            )
            .map_err(|error| error.to_string())?;
    }

    transaction.commit().map_err(|error| error.to_string())?;
    Ok(())
}

pub fn get_files_with_tag(tag_id: i64) -> Result<Vec<String>, String> {
    validate_tag_id(tag_id)?;
    let connection = open_metadata_db()?;
    let mut statement = connection
        .prepare_cached("SELECT file_path FROM file_tags WHERE tag_id = ?1 ORDER BY file_path")
        .map_err(|error| error.to_string())?;

    let rows = statement
        .query_map(params![tag_id], |row| row.get::<_, String>(0))
        .map_err(|error| error.to_string())?;

    let mut paths = Vec::new();
    for row in rows {
        paths.push(row.map_err(|error| error.to_string())?);
    }
    Ok(paths)
}

/// Every tag of every tagged file. A file can carry several tags; the map
/// used to keep only the last one per path.
pub fn get_all_file_tags() -> Result<HashMap<String, Vec<Tag>>, String> {
    let connection = open_metadata_db()?;
    let mut statement = connection
        .prepare_cached(
            "SELECT ft.file_path, t.id, t.name, t.color
             FROM file_tags ft
             JOIN tags t ON ft.tag_id = t.id
             ORDER BY ft.file_path, t.name",
        )
        .map_err(|error| error.to_string())?;

    let rows = statement
        .query_map([], |row| {
            Ok((
                row.get::<_, String>(0)?,
                Tag {
                    id: row.get(1)?,
                    name: row.get(2)?,
                    color: row.get(3)?,
                },
            ))
        })
        .map_err(|error| error.to_string())?;

    let mut map: HashMap<String, Vec<Tag>> = HashMap::new();
    for row in rows {
        let (path, tag) = row.map_err(|error| error.to_string())?;
        let key = normalize_tag_path(&path);
        let entry = map.entry(key).or_default();
        if !entry.iter().any(|existing| existing.id == tag.id) {
            entry.push(tag);
        }
    }
    Ok(map)
}

#[cfg(windows)]
const SEPARATOR: char = '\\';
#[cfg(not(windows))]
const SEPARATOR: char = '/';

/// What two label paths are compared by: Windows paths without regard to case.
fn path_key(path: &str) -> String {
    #[cfg(windows)]
    {
        path.to_lowercase()
    }
    #[cfg(not(windows))]
    {
        path.to_string()
    }
}

/// `path` itself and then each folder above it, nearest first.
fn self_and_ancestors(path: &str) -> impl Iterator<Item = &str> {
    let mut end = Some(path.len());
    std::iter::from_fn(move || {
        let current = end?;
        end = path[..current].rfind(SEPARATOR).filter(|cut| *cut > 0);
        Some(&path[..current])
    })
}

fn labelled_paths(connection: &rusqlite::Connection) -> Result<Vec<String>, String> {
    let mut statement = connection
        .prepare_cached("SELECT DISTINCT file_path FROM file_tags")
        .map_err(|error| error.to_string())?;
    let rows = statement
        .query_map([], |row| row.get::<_, String>(0))
        .map_err(|error| error.to_string())?;
    rows.collect::<Result<Vec<_>, _>>()
        .map_err(|error| error.to_string())
}

#[cfg(windows)]
const DELETE_PATH_SQL: &str = "DELETE FROM file_tags WHERE file_path = ?1 COLLATE NOCASE";
#[cfg(not(windows))]
const DELETE_PATH_SQL: &str = "DELETE FROM file_tags WHERE file_path = ?1";

/// Labels follow what was renamed or moved. Each pair is a path before and
/// after; the labels of that path and of everything below it are carried to
/// the new place, where they replace any labels already there. Returns how
/// many labelled paths moved.
///
/// Labels are stored by path, so without this a renamed file lost its labels
/// and they stayed behind for whatever took the old name next.
pub fn move_tags(moves: &[(String, String)]) -> Result<usize, String> {
    let moves: HashMap<String, String> = moves
        .iter()
        .filter(|(from, to)| !from.trim().is_empty() && !to.trim().is_empty())
        .map(|(from, to)| (normalize_tag_path(from), normalize_tag_path(to)))
        .filter(|(from, to)| from != to)
        .map(|(from, to)| (path_key(&from), to))
        .collect();
    if moves.is_empty() {
        return Ok(0);
    }

    let mut connection = open_metadata_db()?;
    let carried: Vec<(String, String)> = labelled_paths(&connection)?
        .into_iter()
        .filter_map(|path| {
            let moved_part = self_and_ancestors(&path)
                .find(|ancestor| moves.contains_key(&path_key(ancestor)))?;
            let to = &moves[&path_key(moved_part)];
            let new_path = format!("{to}{}", &path[moved_part.len()..]);
            Some((path, new_path))
        })
        .collect();
    if carried.is_empty() {
        return Ok(0);
    }

    let transaction = connection
        .transaction()
        .map_err(|error| error.to_string())?;
    // Taken off every old path first, so that two files that swap names each
    // get the other's labels.
    let mut labels: Vec<(String, Vec<i64>)> = Vec::with_capacity(carried.len());
    for (old_path, new_path) in &carried {
        let tag_ids = {
            let mut statement = transaction
                .prepare_cached("SELECT tag_id FROM file_tags WHERE file_path = ?1")
                .map_err(|error| error.to_string())?;
            let rows = statement
                .query_map(params![old_path], |row| row.get::<_, i64>(0))
                .map_err(|error| error.to_string())?;
            rows.collect::<Result<Vec<_>, _>>()
                .map_err(|error| error.to_string())?
        };
        transaction
            .execute(
                "DELETE FROM file_tags WHERE file_path = ?1",
                params![old_path],
            )
            .map_err(|error| error.to_string())?;
        labels.push((new_path.clone(), tag_ids));
    }
    for (new_path, tag_ids) in labels {
        transaction
            .execute(DELETE_PATH_SQL, params![new_path])
            .map_err(|error| error.to_string())?;
        for tag_id in tag_ids {
            transaction
                .execute(
                    "INSERT OR IGNORE INTO file_tags (file_path, tag_id) VALUES (?1, ?2)",
                    params![new_path, tag_id],
                )
                .map_err(|error| error.to_string())?;
        }
    }
    transaction.commit().map_err(|error| error.to_string())?;
    Ok(carried.len())
}

/// Removes the labels of `paths` and of everything below them, for files that
/// were deleted for good. Returns how many labelled paths lost their labels.
pub fn remove_tags_under(paths: &[String]) -> Result<usize, String> {
    let removed: std::collections::HashSet<String> = paths
        .iter()
        .filter(|path| !path.trim().is_empty())
        .map(|path| path_key(&normalize_tag_path(path)))
        .collect();
    if removed.is_empty() {
        return Ok(0);
    }

    let mut connection = open_metadata_db()?;
    let gone: Vec<String> = labelled_paths(&connection)?
        .into_iter()
        .filter(|path| {
            self_and_ancestors(path).any(|ancestor| removed.contains(&path_key(ancestor)))
        })
        .collect();
    if gone.is_empty() {
        return Ok(0);
    }
    let transaction = connection
        .transaction()
        .map_err(|error| error.to_string())?;
    for path in &gone {
        transaction
            .execute("DELETE FROM file_tags WHERE file_path = ?1", params![path])
            .map_err(|error| error.to_string())?;
    }
    transaction.commit().map_err(|error| error.to_string())?;
    Ok(gone.len())
}

fn normalize_tag_path(path: &str) -> String {
    let trimmed = path.trim();
    #[cfg(windows)]
    {
        let mut normalized = trimmed.replace('/', "\\");
        while normalized.len() > 3 && normalized.ends_with('\\') {
            normalized.pop();
        }
        if normalized.len() == 2
            && normalized.as_bytes()[0].is_ascii_alphabetic()
            && normalized.as_bytes()[1] == b':'
        {
            normalized.push('\\');
        }
        normalized
    }
    #[cfg(not(windows))]
    {
        if trimmed.len() > 1 {
            trimmed.trim_end_matches('/').to_string()
        } else {
            trimmed.to_string()
        }
    }
}

fn read_tag(row: &rusqlite::Row<'_>) -> rusqlite::Result<Tag> {
    Ok(Tag {
        id: row.get(0)?,
        name: row.get(1)?,
        color: row.get(2)?,
    })
}

fn collect_rows<F>(rows: rusqlite::MappedRows<'_, F>) -> Result<Vec<Tag>, String>
where
    F: FnMut(&rusqlite::Row<'_>) -> rusqlite::Result<Tag>,
{
    let mut tags = Vec::new();
    for row in rows {
        tags.push(row.map_err(|error| error.to_string())?);
    }
    Ok(tags)
}

fn validate_tag_id(id: i64) -> Result<(), String> {
    if id <= 0 {
        return Err("tag id must be positive".to_string());
    }
    Ok(())
}

fn validate_tag_input(name: &str, color: &str) -> Result<(), String> {
    if name.trim().is_empty() {
        return Err("tag name cannot be empty".to_string());
    }
    if color.trim().is_empty() {
        return Err("tag color cannot be empty".to_string());
    }
    Ok(())
}

fn validate_path_key(path: &str) -> Result<(), String> {
    if path.trim().is_empty() {
        return Err("path cannot be empty".to_string());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::OsString;
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    struct EnvVarGuard {
        previous: Option<OsString>,
    }

    impl EnvVarGuard {
        fn set(path: &std::path::Path) -> Self {
            let previous = std::env::var_os("SIMPLEFILE_METADATA_DB");
            std::env::set_var("SIMPLEFILE_METADATA_DB", path);
            Self { previous }
        }
    }

    impl Drop for EnvVarGuard {
        fn drop(&mut self) {
            if let Some(previous) = &self.previous {
                std::env::set_var("SIMPLEFILE_METADATA_DB", previous);
            } else {
                std::env::remove_var("SIMPLEFILE_METADATA_DB");
            }
        }
    }

    fn temp_db() -> std::path::PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("time")
            .as_nanos();
        std::env::temp_dir().join(format!("simplefile-tags-{nanos}.db"))
    }

    #[test]
    fn tags_round_trip_through_metadata_db() {
        let _lock = crate::test_support::env_lock().lock().expect("env lock");
        let db = temp_db();
        let _env = EnvVarGuard::set(&db);

        let created = create_tag("Review".to_string(), "#123456".to_string()).expect("create");
        let updated =
            update_tag(created.id, "Reviewed".to_string(), "#654321".to_string()).expect("update");
        assert_eq!(updated.name, "Reviewed");

        let second = create_tag("Urgent".to_string(), "#ff0000".to_string()).expect("second");
        set_tags_for_path("C:\\file.txt".to_string(), vec![created.id, second.id])
            .expect("set tags");
        let tags = get_tags_for_path("C:\\file.txt".to_string()).expect("path tags");
        assert_eq!(tags, vec![updated.clone(), second.clone()]);
        assert_eq!(
            get_files_with_tag(created.id).expect("files"),
            vec!["C:\\file.txt".to_string()]
        );
        // Both tags survive the all-files map; it used to keep only one.
        let all = get_all_file_tags().expect("file tags");
        assert_eq!(
            all.get("C:\\file.txt").cloned(),
            Some(vec![updated, second.clone()])
        );
        delete_tag(second.id).expect("delete second");

        delete_tag(created.id).expect("delete");
        assert!(get_tags_for_path("C:\\file.txt".to_string())
            .expect("after delete")
            .is_empty());

        let _ = fs::remove_file(db);
    }

    /// A label path for the tests: written with `/`, which the store turns
    /// into the platform's separator where the two differ.
    fn tagged(path: &str, ids: &[i64]) {
        set_tags_for_path(path.to_string(), ids.to_vec()).expect("set tags");
    }

    fn ids_at(path: &str) -> Vec<i64> {
        let mut ids: Vec<i64> = get_tags_for_path(path.to_string())
            .expect("tags for path")
            .into_iter()
            .map(|tag| tag.id)
            .collect();
        ids.sort_unstable();
        ids
    }

    fn pair(from: &str, to: &str) -> (String, String) {
        (from.to_string(), to.to_string())
    }

    #[test]
    fn labels_follow_a_renamed_file_and_everything_below_a_moved_folder() {
        let _lock = crate::test_support::env_lock().lock().expect("env lock");
        let db = temp_db();
        let _env = EnvVarGuard::set(&db);
        let red = create_tag("Red".into(), "#ff0000".into()).unwrap().id;
        let blue = create_tag("Blue".into(), "#0000ff".into()).unwrap().id;
        tagged("C:/work/report.txt", &[red, blue]);
        tagged("C:/work/photos", &[blue]);
        tagged("C:/work/photos/2025/a.jpg", &[red]);
        tagged("C:/work/photos-old/b.jpg", &[red]);
        tagged("C:/other/keep.txt", &[blue]);

        let moved = move_tags(&[
            pair("C:/work/report.txt", "C:/work/final.txt"),
            pair("C:/work/photos/", "D:/archive/pictures"),
        ])
        .expect("move tags");

        assert_eq!(moved, 3);
        assert_eq!(ids_at("C:/work/final.txt"), vec![red, blue]);
        assert!(ids_at("C:/work/report.txt").is_empty());
        assert_eq!(ids_at("D:/archive/pictures"), vec![blue]);
        assert_eq!(ids_at("D:/archive/pictures/2025/a.jpg"), vec![red]);
        assert!(ids_at("C:/work/photos/2025/a.jpg").is_empty());
        // A folder whose name only starts the same is another folder.
        assert_eq!(ids_at("C:/work/photos-old/b.jpg"), vec![red]);
        assert_eq!(ids_at("C:/other/keep.txt"), vec![blue]);
        let _ = fs::remove_file(db);
    }

    #[test]
    fn carried_labels_replace_the_labels_at_the_new_path_and_a_swap_swaps_them() {
        let _lock = crate::test_support::env_lock().lock().expect("env lock");
        let db = temp_db();
        let _env = EnvVarGuard::set(&db);
        let red = create_tag("Red".into(), "#ff0000".into()).unwrap().id;
        let blue = create_tag("Blue".into(), "#0000ff".into()).unwrap().id;
        tagged("C:/a.txt", &[red]);
        tagged("C:/b.txt", &[blue]);
        tagged("C:/c.txt", &[red, blue]);

        // A file moved over a labelled one brings its own labels.
        assert_eq!(move_tags(&[pair("C:/a.txt", "C:/c.txt")]).unwrap(), 1);
        assert_eq!(ids_at("C:/c.txt"), vec![red]);

        // A file without labels leaves the labels at its new path alone.
        assert_eq!(move_tags(&[pair("C:/plain.txt", "C:/b.txt")]).unwrap(), 0);
        assert_eq!(ids_at("C:/b.txt"), vec![blue]);

        // Two files that take each other's names in one batch.
        let swapped = move_tags(&[pair("C:/b.txt", "C:/c.txt"), pair("C:/c.txt", "C:/b.txt")]);
        assert_eq!(swapped.unwrap(), 2);
        assert_eq!(ids_at("C:/c.txt"), vec![blue]);
        assert_eq!(ids_at("C:/b.txt"), vec![red]);

        // Nothing to do is not an error.
        assert_eq!(move_tags(&[]).unwrap(), 0);
        assert_eq!(
            move_tags(&[pair("C:/b.txt", "C:/b.txt"), pair("", "C:/x")]).unwrap(),
            0
        );
        assert_eq!(ids_at("C:/b.txt"), vec![red]);
        let _ = fs::remove_file(db);
    }

    #[test]
    fn labels_of_deleted_files_and_folders_are_removed() {
        let _lock = crate::test_support::env_lock().lock().expect("env lock");
        let db = temp_db();
        let _env = EnvVarGuard::set(&db);
        let red = create_tag("Red".into(), "#ff0000".into()).unwrap().id;
        tagged("C:/work/gone.txt", &[red]);
        tagged("C:/work/folder", &[red]);
        tagged("C:/work/folder/deep/x.txt", &[red]);
        tagged("C:/work/folder2/y.txt", &[red]);

        let removed = remove_tags_under(&["C:/work/gone.txt".into(), "C:/work/folder".into()]);

        assert_eq!(removed.unwrap(), 3);
        assert!(ids_at("C:/work/gone.txt").is_empty());
        assert!(ids_at("C:/work/folder/deep/x.txt").is_empty());
        assert_eq!(ids_at("C:/work/folder2/y.txt"), vec![red]);
        assert_eq!(remove_tags_under(&[]).unwrap(), 0);
        assert_eq!(remove_tags_under(&["C:/nothing/here".into()]).unwrap(), 0);
        let _ = fs::remove_file(db);
    }

    #[cfg(windows)]
    #[test]
    fn a_rename_that_changes_only_the_case_keeps_the_labels() {
        let _lock = crate::test_support::env_lock().lock().expect("env lock");
        let db = temp_db();
        let _env = EnvVarGuard::set(&db);
        let red = create_tag("Red".into(), "#ff0000".into()).unwrap().id;
        tagged("C:/Work/Report.txt", &[red]);

        assert_eq!(
            move_tags(&[pair("c:/work/report.TXT", "C:/Work/REPORT.txt")]).unwrap(),
            1
        );

        assert_eq!(ids_at("C:/Work/REPORT.txt"), vec![red]);
        assert_eq!(
            get_files_with_tag(red).unwrap(),
            vec!["C:\\Work\\REPORT.txt".to_string()]
        );
        let _ = fs::remove_file(db);
    }
}
