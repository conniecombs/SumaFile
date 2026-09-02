use crate::models::Tag;
use crate::settings_store::open_metadata_db;
use rusqlite::{params, OptionalExtension};
use std::collections::HashMap;

pub fn get_all_tags() -> Result<Vec<Tag>, String> {
    let connection = open_metadata_db()?;
    let mut statement = connection
        .prepare("SELECT id, name, color FROM tags ORDER BY name")
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
    validate_tag_id(id)?;
    validate_tag_input(&name, &color)?;
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
    let connection = open_metadata_db()?;
    let mut statement = connection
        .prepare(
            "SELECT t.id, t.name, t.color
             FROM tags t
             JOIN file_tags ft ON t.id = ft.tag_id
             WHERE ft.file_path = ?1
             ORDER BY t.name",
        )
        .map_err(|error| error.to_string())?;

    let rows = statement
        .query_map(params![path], read_tag)
        .map_err(|error| error.to_string())?;

    collect_rows(rows)
}

pub fn set_tags_for_path(path: String, tag_ids: Vec<i64>) -> Result<(), String> {
    validate_path_key(&path)?;
    for tag_id in &tag_ids {
        validate_tag_id(*tag_id)?;
    }

    let mut connection = open_metadata_db()?;
    let transaction = connection
        .transaction()
        .map_err(|error| error.to_string())?;
    transaction
        .execute("DELETE FROM file_tags WHERE file_path = ?1", params![path])
        .map_err(|error| error.to_string())?;

    for tag_id in tag_ids {
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
                params![path, tag_id],
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
        .prepare("SELECT file_path FROM file_tags WHERE tag_id = ?1 ORDER BY file_path")
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

pub fn get_all_file_tags() -> Result<HashMap<String, Tag>, String> {
    let connection = open_metadata_db()?;
    let mut statement = connection
        .prepare(
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

    let mut map = HashMap::new();
    for row in rows {
        let (path, tag) = row.map_err(|error| error.to_string())?;
        map.insert(path, tag);
    }
    Ok(map)
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

        set_tags_for_path("C:\\file.txt".to_string(), vec![created.id]).expect("set tags");
        let tags = get_tags_for_path("C:\\file.txt".to_string()).expect("path tags");
        assert_eq!(tags, vec![updated.clone()]);
        assert_eq!(
            get_files_with_tag(created.id).expect("files"),
            vec!["C:\\file.txt".to_string()]
        );
        assert_eq!(
            get_all_file_tags()
                .expect("file tags")
                .get("C:\\file.txt")
                .cloned(),
            Some(updated)
        );

        delete_tag(created.id).expect("delete");
        assert!(get_tags_for_path("C:\\file.txt".to_string())
            .expect("after delete")
            .is_empty());

        let _ = fs::remove_file(db);
    }
}
