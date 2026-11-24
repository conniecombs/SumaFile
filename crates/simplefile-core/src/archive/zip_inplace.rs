//! Delete and rename in a ZIP archive without unpacking it.
//!
//! The general way to change an archive is to extract all of it, change the
//! files and build it again. For a ZIP that decompresses and compresses every
//! byte to remove one entry, needs the archive's unpacked size in free space,
//! and resets what the archive recorded about the entries that stay: their
//! compression, comments and permissions.
//!
//! A ZIP entry can be copied as it is stored. Deleting is then a copy of the
//! entries that stay, and renaming a copy under new names. That is done here
//! for an archive whose names are plain: every name a safe relative path, no
//! two the same ignoring case, no file that is also a folder of another
//! entry, nothing encrypted and no links. For any other archive these
//! functions return `None` and the caller extracts and rebuilds as before,
//! with the errors that gives.

use super::create::{add_dir_to_zip, with_source_mtime};
use super::mutate_engine::NewEntry;
use super::path::{
    build_virtual_archive_path, replace_archive, unique_temp_archive_path, zip_entry_relative_path,
};
use std::collections::HashSet;
use std::fs::{self, File};
use std::io::{BufReader, BufWriter};
use std::path::{Component, Path, PathBuf};

type Archive = zip::ZipArchive<BufReader<File>>;
type Writer = zip::ZipWriter<BufWriter<File>>;

struct Entry {
    /// The parts of the entry's path as stored.
    parts: Vec<String>,
    /// The same, lowercased: what two names are compared by.
    key: Vec<String>,
    is_dir: bool,
}

fn parts_of(path: &Path) -> Vec<String> {
    path.components()
        .filter_map(|component| match component {
            Component::Normal(part) => Some(part.to_string_lossy().into_owned()),
            _ => None,
        })
        .collect()
}

fn key_of(parts: &[String]) -> Vec<String> {
    parts.iter().map(|part| part.to_lowercase()).collect()
}

/// True when `key` is `prefix` or lies below it.
fn is_within(key: &[String], prefix: &[String]) -> bool {
    key.len() >= prefix.len() && key[..prefix.len()] == *prefix
}

fn open(archive_path: &Path) -> Result<Archive, String> {
    let file = File::open(archive_path).map_err(|e| e.to_string())?;
    zip::ZipArchive::new(BufReader::new(file)).map_err(|e| e.to_string())
}

/// The entries in stored order, or `None` when the archive is not plain.
fn plain_entries(archive: &mut Archive) -> Result<Option<Vec<Entry>>, String> {
    let mut entries = Vec::with_capacity(archive.len());
    for index in 0..archive.len() {
        let file = archive.by_index_raw(index).map_err(|e| e.to_string())?;
        if file.encrypted() || file.is_symlink() {
            return Ok(None);
        }
        let Ok(name) = file.name() else {
            return Ok(None);
        };
        let Ok(relative) = zip_entry_relative_path(&name) else {
            return Ok(None);
        };
        let parts = parts_of(&relative);
        entries.push(Entry {
            key: key_of(&parts),
            parts,
            is_dir: file.is_dir(),
        });
    }

    let mut seen = HashSet::with_capacity(entries.len());
    let mut files = HashSet::new();
    for entry in &entries {
        if !seen.insert(entry.key.as_slice()) {
            return Ok(None);
        }
        if !entry.is_dir {
            files.insert(entry.key.as_slice());
        }
    }
    // A file that another entry lies below cannot be both on a disk.
    let file_is_a_folder = entries
        .iter()
        .any(|entry| (1..entry.key.len()).any(|depth| files.contains(&entry.key[..depth])));
    Ok((!file_is_a_folder).then_some(entries))
}

/// Writes a new archive beside the old one with `write`, then puts it in the
/// old one's place. A failure leaves the archive as it was.
fn rewrite<F>(archive_path: &Path, mut archive: Archive, write: F) -> Result<(), String>
where
    F: FnOnce(&mut Archive, &mut Writer) -> Result<(), String>,
{
    let new_archive = unique_temp_archive_path(archive_path)?;
    let result = (|| {
        let file = File::create(&new_archive)
            .map_err(|e| format!("Failed to create the new archive: {e}"))?;
        let mut writer = zip::ZipWriter::new(BufWriter::new(file));
        write(&mut archive, &mut writer)?;
        writer
            .set_raw_comment(archive.comment().into())
            .map_err(|e| e.to_string())?;
        let mut finished = writer.finish().map_err(|e| e.to_string())?;
        std::io::Write::flush(&mut finished).map_err(|e| e.to_string())?;
        drop(finished);
        // The old archive must not be open when it is renamed away.
        drop(archive);
        replace_archive(archive_path, &new_archive)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&new_archive);
    }
    result
}

fn copy_as_stored(archive: &mut Archive, writer: &mut Writer, index: usize) -> Result<(), String> {
    let file = archive.by_index_raw(index).map_err(|e| e.to_string())?;
    writer.raw_copy_file(file).map_err(|e| e.to_string())
}

/// Removes each of `targets` (paths inside the archive) with everything below
/// it, and returns for each target whether it was there. Targets that are not
/// there change nothing. `None`: not a plain archive.
pub(super) fn try_delete(
    archive_path: &Path,
    targets: &[PathBuf],
) -> Result<Option<Vec<bool>>, String> {
    let mut archive = open(archive_path)?;
    let Some(entries) = plain_entries(&mut archive)? else {
        return Ok(None);
    };

    let target_keys: Vec<Vec<String>> = targets
        .iter()
        .map(|target| key_of(&parts_of(target)))
        .collect();
    let found: Vec<bool> = target_keys
        .iter()
        .map(|target| {
            !target.is_empty() && entries.iter().any(|entry| is_within(&entry.key, target))
        })
        .collect();
    let removed = |entry: &Entry| {
        target_keys
            .iter()
            .zip(&found)
            .any(|(target, found)| *found && is_within(&entry.key, target))
    };
    if !found.iter().any(|found| *found) {
        return Ok(Some(found));
    }

    // A folder that existed only because something was in it would vanish with
    // its last entry, where a folder on a disk stays behind empty. It is kept
    // by giving it an entry of its own.
    let mut emptied: Vec<Vec<String>> = Vec::new();
    for entry in entries.iter().filter(|entry| removed(entry)) {
        let Some(target) = target_keys
            .iter()
            .zip(&found)
            .filter(|(target, found)| **found && is_within(&entry.key, target))
            .map(|(target, _)| target)
            .min_by_key(|target| target.len())
        else {
            continue;
        };
        let parent_depth = target.len() - 1;
        if parent_depth == 0 {
            continue;
        }
        let parent_key = &entry.key[..parent_depth];
        let still_there = entries
            .iter()
            .any(|other| !removed(other) && is_within(&other.key, parent_key));
        let parent = entry.parts[..parent_depth].to_vec();
        if !still_there && !emptied.iter().any(|known| key_of(known) == parent_key) {
            emptied.push(parent);
        }
    }

    rewrite(archive_path, archive, |archive, writer| {
        for (index, entry) in entries.iter().enumerate() {
            if !removed(entry) {
                copy_as_stored(archive, writer, index)?;
            }
        }
        for folder in &emptied {
            writer
                .add_directory(folder.join("/"), zip::write::SimpleFileOptions::default())
                .map_err(|e| e.to_string())?;
        }
        Ok(())
    })?;
    Ok(Some(found))
}

/// Gives the entry at `inner` the name `new_name` in the same folder, moving
/// everything below it along, and returns its new path inside the archive.
/// `display` names the entry in the error for one that is not there.
/// `None`: not a plain archive.
pub(super) fn try_rename(
    archive_path: &Path,
    inner: &Path,
    new_name: &str,
    display: &str,
) -> Result<Option<PathBuf>, String> {
    let mut archive = open(archive_path)?;
    let Some(entries) = plain_entries(&mut archive)? else {
        return Ok(None);
    };

    let target = key_of(&parts_of(inner));
    let Some(example) = entries
        .iter()
        .find(|entry| !target.is_empty() && is_within(&entry.key, &target))
    else {
        return Err(format!("Archive entry not found: {display}"));
    };
    let depth = target.len();
    // The path as the archive spells it, which may differ in case from `inner`.
    let old_parts = example.parts[..depth].to_vec();
    let mut new_parts = old_parts.clone();
    new_parts[depth - 1] = new_name.to_string();
    let new_path: PathBuf = new_parts.iter().collect();
    if new_parts == old_parts {
        return Ok(Some(new_path));
    }

    let new_key = key_of(&new_parts);
    // A rename that changes only the case of the name meets nothing but itself.
    let taken = new_key != target && entries.iter().any(|entry| is_within(&entry.key, &new_key));
    if taken {
        return Err(format!(
            "A file or directory with that name already exists: {new_name}"
        ));
    }

    rewrite(archive_path, archive, |archive, writer| {
        for (index, entry) in entries.iter().enumerate() {
            if !is_within(&entry.key, &target) {
                copy_as_stored(archive, writer, index)?;
                continue;
            }
            let mut name = new_parts
                .iter()
                .chain(&entry.parts[depth..])
                .map(String::as_str)
                .collect::<Vec<_>>()
                .join("/");
            if entry.is_dir {
                name.push('/');
            }
            let file = archive.by_index_raw(index).map_err(|e| e.to_string())?;
            writer
                .raw_copy_file_rename(file, name)
                .map_err(|e| e.to_string())?;
        }
        Ok(())
    })?;
    Ok(Some(new_path))
}

/// True when the archive has `key` as an entry or as a folder of one.
fn exists(entries: &[Entry], key: &[String]) -> bool {
    !key.is_empty() && entries.iter().any(|entry| is_within(&entry.key, key))
}

fn joined(parts: &[String]) -> String {
    parts.join("/")
}

fn shown(archive_path: &Path, parts: &[String]) -> String {
    let inner: PathBuf = parts.iter().collect();
    build_virtual_archive_path(archive_path, &inner)
}

/// A folder that existed only because something was in it gets an entry of
/// its own when `gone` leaves it empty, as it stays behind on a disk.
fn folder_left_empty(
    entries: &[Entry],
    kept: impl Fn(&Entry) -> bool,
    gone: &[String],
) -> Option<Vec<String>> {
    let parent_depth = gone.len().checked_sub(1).filter(|depth| *depth > 0)?;
    let parent_key = &gone[..parent_depth];
    let example = entries.iter().find(|entry| is_within(&entry.key, gone))?;
    let still_there = entries
        .iter()
        .any(|entry| kept(entry) && is_within(&entry.key, parent_key));
    (!still_there).then(|| example.parts[..parent_depth].to_vec())
}

/// Adds an empty folder or file named `name` to the folder `parent`, and
/// returns its path inside the archive. `None`: not a plain archive.
pub(super) fn try_create(
    archive_path: &Path,
    parent: &Path,
    name: &str,
    kind: NewEntry,
) -> Result<Option<PathBuf>, String> {
    let mut archive = open(archive_path)?;
    let Some(entries) = plain_entries(&mut archive)? else {
        return Ok(None);
    };

    let mut parts = parts_of(parent);
    let parent_key = key_of(&parts);
    if entries
        .iter()
        .any(|entry| !entry.is_dir && entry.key == parent_key)
    {
        return Err(format!(
            "Destination is not a directory: {}",
            shown(archive_path, &parts)
        ));
    }
    parts.push(name.to_string());
    if exists(&entries, &key_of(&parts)) {
        return Err(match kind {
            NewEntry::Directory => format!("Directory already exists: {name}"),
            NewEntry::File => format!("File already exists: {name}"),
        });
    }

    let new_path: PathBuf = parts.iter().collect();
    rewrite(archive_path, archive, |archive, writer| {
        for index in 0..entries.len() {
            copy_as_stored(archive, writer, index)?;
        }
        let options = zip::write::SimpleFileOptions::default();
        match kind {
            NewEntry::Directory => writer.add_directory(joined(&parts), options),
            NewEntry::File => writer.start_file(
                joined(&parts),
                options.compression_method(zip::CompressionMethod::Stored),
            ),
        }
        .map_err(|e| e.to_string())
    })?;
    Ok(Some(new_path))
}

/// Where an item named `name` goes in the folder `dest`, by the conflict
/// action the user chose.
enum Placement {
    /// At these parts. `replacing`: what is there now goes first.
    At { parts: Vec<String>, replacing: bool },
    /// The user chose Skip.
    Skipped,
}

/// The rule of a copy or a move into a folder, for a folder inside an
/// archive: free name, or Skip, Replace, Keep both, or the conflict error the
/// app answers by asking.
fn place(
    archive_path: &Path,
    entries: &[Entry],
    dest: &[String],
    name: &str,
    conflict_action: &str,
) -> Result<Placement, String> {
    let dest_key = key_of(dest);
    if entries
        .iter()
        .any(|entry| !entry.is_dir && entry.key == dest_key)
    {
        return Err(format!(
            "Destination is not a directory: {}",
            shown(archive_path, dest)
        ));
    }
    let mut parts = dest.to_vec();
    parts.push(name.to_string());
    if !exists(entries, &key_of(&parts)) {
        return Ok(Placement::At {
            parts,
            replacing: false,
        });
    }

    match conflict_action.to_ascii_lowercase().as_str() {
        "skip" => Ok(Placement::Skipped),
        "replace" => Ok(Placement::At {
            parts,
            replacing: true,
        }),
        "rename" | "keep-both" | "keep_both" => {
            let free = sumafile_transfer::paths::unique_name(name.as_ref(), |candidate| {
                let mut key = dest_key.clone();
                key.push(candidate.to_lowercase());
                exists(entries, &key)
            })?;
            let mut parts = dest.to_vec();
            parts.push(free);
            Ok(Placement::At {
                parts,
                replacing: false,
            })
        }
        _ => Err(format!(
            "CONFLICT: destination already exists: {}",
            shown(archive_path, &parts)
        )),
    }
}

/// Adds the local file or folder `source` to the folder `dest` of the
/// archive, compressing it; the entries already there are copied as stored.
/// Returns the new path inside the archive, or `Some(None)` when the user's
/// conflict action was Skip. `None`: not a plain archive.
pub(super) fn try_add(
    archive_path: &Path,
    dest: &Path,
    source: &Path,
    conflict_action: &str,
) -> Result<Option<Option<PathBuf>>, String> {
    let mut archive = open(archive_path)?;
    let Some(entries) = plain_entries(&mut archive)? else {
        return Ok(None);
    };

    let meta = fs::symlink_metadata(source).map_err(|e| format!("Failed to stat source: {e}"))?;
    if meta.file_type().is_symlink() {
        return Err("Copying symlinks into or out of archives is not supported".to_string());
    }
    let name = source
        .file_name()
        .ok_or_else(|| "Cannot get file name".to_string())?
        .to_string_lossy()
        .into_owned();
    let (parts, replacing) = match place(
        archive_path,
        &entries,
        &parts_of(dest),
        &name,
        conflict_action,
    )? {
        Placement::Skipped => return Ok(Some(None)),
        Placement::At { parts, replacing } => (parts, replacing),
    };
    let new_key = key_of(&parts);
    let new_path: PathBuf = parts.iter().collect();

    rewrite(archive_path, archive, |archive, writer| {
        for (index, entry) in entries.iter().enumerate() {
            if !(replacing && is_within(&entry.key, &new_key)) {
                copy_as_stored(archive, writer, index)?;
            }
        }
        let options = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Deflated);
        let entry_name = joined(&parts);
        if meta.is_dir() {
            writer
                .add_directory(entry_name.as_str(), options)
                .map_err(|e| e.to_string())?;
            add_dir_to_zip(writer, source, &entry_name, options)
        } else {
            writer
                .start_file(entry_name.as_str(), with_source_mtime(options, source))
                .map_err(|e| e.to_string())?;
            let mut contents =
                File::open(source).map_err(|e| format!("Failed to copy file: {e}"))?;
            std::io::copy(&mut contents, writer)
                .map(|_| ())
                .map_err(|e| format!("Failed to copy file: {e}"))
        }
    })?;
    Ok(Some(Some(new_path)))
}

/// Copies or moves the entry at `source` into the folder `dest` of the same
/// archive, with everything below it. Returns its new path inside the
/// archive, or `Some(None)` when the user's conflict action was Skip.
/// `display` names the source in the error for one that is not there.
/// `None`: not a plain archive.
pub(super) fn try_transfer_within(
    archive_path: &Path,
    source: &Path,
    dest: &Path,
    conflict_action: &str,
    is_move: bool,
    display: &str,
) -> Result<Option<Option<PathBuf>>, String> {
    let mut archive = open(archive_path)?;
    let Some(entries) = plain_entries(&mut archive)? else {
        return Ok(None);
    };

    let source_key = key_of(&parts_of(source));
    let Some(example) = entries
        .iter()
        .find(|entry| !source_key.is_empty() && is_within(&entry.key, &source_key))
    else {
        return Err(format!("Archive entry not found: {display}"));
    };
    let depth = source_key.len();
    let name = example.parts[depth - 1].clone();
    let dest_parts = parts_of(dest);
    if is_within(&key_of(&dest_parts), &source_key) {
        return Err("Cannot copy a directory into itself or one of its subdirectories".to_string());
    }

    let (parts, replacing) =
        match place(archive_path, &entries, &dest_parts, &name, conflict_action)? {
            Placement::Skipped => return Ok(Some(None)),
            Placement::At { parts, replacing } => (parts, replacing),
        };
    let new_key = key_of(&parts);
    let new_path: PathBuf = parts.iter().collect();
    if new_key == source_key {
        // Onto itself: there is nothing to copy or move.
        return Ok(Some(Some(new_path)));
    }
    if replacing && is_within(&source_key, &new_key) {
        return Err("Cannot replace a folder with an item from inside it".to_string());
    }

    let replaced = |entry: &Entry| replacing && is_within(&entry.key, &new_key);
    let transferred = |entry: &Entry| is_within(&entry.key, &source_key);
    let emptied = if is_move {
        folder_left_empty(
            &entries,
            |entry| !replaced(entry) && !transferred(entry),
            &source_key,
        )
        // The item may have moved to another place in that same folder.
        .filter(|folder| !is_within(&new_key, &key_of(folder)))
    } else {
        None
    };

    rewrite(archive_path, archive, |archive, writer| {
        let renamed = |entry: &Entry| {
            let mut name = parts
                .iter()
                .chain(&entry.parts[depth..])
                .map(String::as_str)
                .collect::<Vec<_>>()
                .join("/");
            if entry.is_dir {
                name.push('/');
            }
            name
        };
        for (index, entry) in entries.iter().enumerate() {
            if replaced(entry) {
                continue;
            }
            if !transferred(entry) {
                copy_as_stored(archive, writer, index)?;
            } else if is_move {
                let file = archive.by_index_raw(index).map_err(|e| e.to_string())?;
                writer
                    .raw_copy_file_rename(file, renamed(entry))
                    .map_err(|e| e.to_string())?;
            } else {
                copy_as_stored(archive, writer, index)?;
            }
        }
        if !is_move {
            // The copies go after everything else, in the order of their originals.
            for (index, entry) in entries.iter().enumerate() {
                if transferred(entry) {
                    let file = archive.by_index_raw(index).map_err(|e| e.to_string())?;
                    writer
                        .raw_copy_file_rename(file, renamed(entry))
                        .map_err(|e| e.to_string())?;
                }
            }
        }
        if let Some(folder) = &emptied {
            writer
                .add_directory(joined(folder), zip::write::SimpleFileOptions::default())
                .map_err(|e| e.to_string())?;
        }
        Ok(())
    })?;
    Ok(Some(Some(new_path)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use zip::write::SimpleFileOptions;
    use zip::CompressionMethod;

    /// Entry names with their contents; a name ending in `/` is a folder.
    type Contents = &'static [(&'static str, &'static [u8])];

    fn temp_dir(label: &str) -> PathBuf {
        let nanos = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let dir = std::env::temp_dir().join(format!(
            "sumafile-zip-inplace-{label}-{}-{nanos}",
            std::process::id()
        ));
        fs::create_dir_all(&dir).unwrap();
        dir
    }

    fn stamp() -> zip::DateTime {
        zip::DateTime::from_date_and_time(2021, 3, 4, 5, 6, 8).unwrap()
    }

    /// Names ending in `/` are folders. Files alternate between stored and
    /// deflated, and carry a comment, a fixed time and Unix permissions. The
    /// entries are written as made on Unix: the zip crate otherwise writes
    /// DOS entries on Windows, and a DOS entry has no Unix mode to read back.
    fn write_zip(path: &Path, entries: &[(&str, &[u8])]) {
        let mut zip = zip::ZipWriter::new(File::create(path).unwrap());
        for (index, (name, contents)) in entries.iter().enumerate() {
            if let Some(folder) = name.strip_suffix('/') {
                zip.add_directory(
                    folder,
                    SimpleFileOptions::default().system(zip::System::Unix),
                )
                .unwrap();
                continue;
            }
            let method = if index % 2 == 1 {
                CompressionMethod::Deflated
            } else {
                CompressionMethod::Stored
            };
            let options = zip::write::FullFileOptions::default()
                .compression_method(method)
                .last_modified_time(stamp())
                .system(zip::System::Unix)
                .unix_permissions(0o640)
                .with_file_comment(format!("comment of {name}"));
            zip.start_file(*name, options).unwrap();
            zip.write_all(contents).unwrap();
        }
        zip.set_comment("the archive's own comment").unwrap();
        zip.finish().unwrap();
    }

    /// What an entry is stored as: everything a raw copy must keep.
    #[derive(Debug, PartialEq)]
    struct Stored {
        name: String,
        crc32: u32,
        compressed_size: u64,
        size: u64,
        method: String,
        modified: Option<(u16, u8, u8, u8, u8, u8)>,
        comment: String,
        mode: Option<u32>,
        contents: Vec<u8>,
    }

    fn stored(path: &Path) -> Vec<Stored> {
        let mut archive = open(path).unwrap();
        (0..archive.len())
            .map(|index| {
                let mut file = archive.by_index(index).unwrap();
                let mut contents = Vec::new();
                file.read_to_end(&mut contents).unwrap();
                Stored {
                    name: file.name().unwrap().into_owned(),
                    crc32: file.crc32(),
                    compressed_size: file.compressed_size(),
                    size: file.size(),
                    method: format!("{:?}", file.compression()),
                    modified: file.last_modified().map(|time| {
                        (
                            time.year(),
                            time.month(),
                            time.day(),
                            time.hour(),
                            time.minute(),
                            time.second(),
                        )
                    }),
                    comment: file.comment().to_string(),
                    mode: file.unix_mode(),
                    contents,
                }
            })
            .collect()
    }

    fn names(path: &Path) -> Vec<String> {
        stored(path).into_iter().map(|entry| entry.name).collect()
    }

    fn sample(dir: &Path) -> PathBuf {
        let path = dir.join("sample.zip");
        write_zip(
            &path,
            &[
                ("docs/", b""),
                ("docs/a.txt", &[b'a'; 4000]),
                ("docs/b.txt", b"second"),
                ("docs/sub/c.txt", &[b'c'; 3000]),
                ("loose/only.txt", b"alone"),
                ("top.txt", &[b't'; 5000]),
            ],
        );
        path
    }

    #[test]
    fn a_delete_copies_the_entries_that_stay_exactly_as_they_are_stored() {
        let dir = temp_dir("delete-keeps");
        let archive = sample(&dir);
        let before = stored(&archive);

        let found = try_delete(&archive, &[PathBuf::from("docs").join("a.txt")])
            .unwrap()
            .expect("a plain archive");

        assert_eq!(found, vec![true]);
        let after = stored(&archive);
        let expected: Vec<&Stored> = before
            .iter()
            .filter(|entry| entry.name != "docs/a.txt")
            .collect();
        assert_eq!(after.iter().collect::<Vec<_>>(), expected);
        // The deflated entries really are deflated, so equal sizes mean a copy.
        assert!(after
            .iter()
            .any(|entry| entry.method == "Deflated" && entry.compressed_size < entry.size));
        assert_eq!(after[0].comment, "");
        assert_eq!(after[1].comment, "comment of docs/b.txt");
        assert_eq!(after[1].modified, Some((2021, 3, 4, 5, 6, 8)));
        assert_eq!(after[1].mode.map(|mode| mode & 0o777), Some(0o640));
        assert_eq!(
            open(&archive).unwrap().comment(),
            b"the archive's own comment"
        );
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn deleting_a_folder_removes_everything_below_it() {
        let dir = temp_dir("delete-folder");
        let archive = sample(&dir);

        let found = try_delete(&archive, &[PathBuf::from("docs")])
            .unwrap()
            .unwrap();

        assert_eq!(found, vec![true]);
        assert_eq!(names(&archive), vec!["loose/only.txt", "top.txt"]);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_folder_emptied_by_a_delete_stays_as_a_folder() {
        let dir = temp_dir("delete-empties");
        let archive = sample(&dir);

        // `loose` has no entry of its own: it exists because of the file in it.
        try_delete(&archive, &[PathBuf::from("loose").join("only.txt")])
            .unwrap()
            .unwrap();
        assert!(names(&archive).contains(&"loose/".to_string()));

        // `docs/sub` likewise, below a folder that does have an entry.
        try_delete(&archive, &[PathBuf::from("docs").join("sub").join("c.txt")])
            .unwrap()
            .unwrap();
        let after = names(&archive);
        assert!(after.contains(&"docs/sub/".to_string()), "{after:?}");

        // A folder that keeps another entry gets none.
        try_delete(&archive, &[PathBuf::from("docs").join("a.txt")])
            .unwrap()
            .unwrap();
        let after = names(&archive);
        assert_eq!(
            after.iter().filter(|name| name.as_str() == "docs/").count(),
            1,
            "{after:?}"
        );
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_target_that_is_not_there_is_reported_and_changes_nothing() {
        let dir = temp_dir("delete-missing");
        let archive = sample(&dir);
        let before = fs::read(&archive).unwrap();

        let found = try_delete(
            &archive,
            &[PathBuf::from("nope.txt"), PathBuf::from("docs").join("zz")],
        )
        .unwrap()
        .unwrap();

        assert_eq!(found, vec![false, false]);
        assert_eq!(fs::read(&archive).unwrap(), before);
        let leftovers: Vec<_> = fs::read_dir(&dir).unwrap().collect();
        assert_eq!(leftovers.len(), 1, "no temporary archive is left behind");
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn several_targets_are_removed_in_one_pass_and_the_missing_ones_named() {
        let dir = temp_dir("delete-several");
        let archive = sample(&dir);

        let found = try_delete(
            &archive,
            &[
                PathBuf::from("docs"),
                PathBuf::from("docs").join("a.txt"),
                PathBuf::from("missing.txt"),
                PathBuf::from("TOP.TXT"),
            ],
        )
        .unwrap()
        .unwrap();

        assert_eq!(found, vec![true, true, false, true]);
        assert_eq!(names(&archive), vec!["loose/only.txt"]);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_rename_moves_a_file_and_keeps_its_stored_bytes() {
        let dir = temp_dir("rename-file");
        let archive = sample(&dir);
        let before = stored(&archive);

        let renamed = try_rename(
            &archive,
            &PathBuf::from("docs").join("a.txt"),
            "first.txt",
            "shown",
        )
        .unwrap()
        .unwrap();

        assert_eq!(renamed, PathBuf::from("docs").join("first.txt"));
        let after = stored(&archive);
        assert_eq!(after.len(), before.len());
        assert_eq!(after[1].name, "docs/first.txt");
        assert_eq!(after[1].crc32, before[1].crc32);
        assert_eq!(after[1].compressed_size, before[1].compressed_size);
        assert_eq!(after[1].contents, before[1].contents);
        assert_eq!(after[1].modified, before[1].modified);
        assert_eq!(after[1].comment, before[1].comment);
        for index in [0, 2, 3, 4, 5] {
            assert_eq!(after[index], before[index]);
        }
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_rename_of_a_folder_moves_everything_below_it() {
        let dir = temp_dir("rename-folder");
        let archive = sample(&dir);

        let renamed = try_rename(&archive, Path::new("docs"), "papers", "shown")
            .unwrap()
            .unwrap();
        assert_eq!(renamed, PathBuf::from("papers"));
        assert_eq!(
            names(&archive),
            vec![
                "papers/",
                "papers/a.txt",
                "papers/b.txt",
                "papers/sub/c.txt",
                "loose/only.txt",
                "top.txt"
            ]
        );

        // A folder without an entry of its own.
        let renamed = try_rename(&archive, Path::new("loose"), "tied", "shown")
            .unwrap()
            .unwrap();
        assert_eq!(renamed, PathBuf::from("tied"));
        assert!(names(&archive).contains(&"tied/only.txt".to_string()));
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_rename_onto_a_taken_name_is_refused_and_changes_nothing() {
        let dir = temp_dir("rename-taken");
        let archive = sample(&dir);
        let before = fs::read(&archive).unwrap();

        for taken in ["top.txt", "LOOSE", "Top.TXT"] {
            assert_eq!(
                try_rename(&archive, Path::new("docs"), taken, "shown").unwrap_err(),
                format!("A file or directory with that name already exists: {taken}")
            );
        }
        assert_eq!(
            try_rename(&archive, Path::new("gone.txt"), "x.txt", "the shown path").unwrap_err(),
            "Archive entry not found: the shown path"
        );
        assert_eq!(fs::read(&archive).unwrap(), before);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_rename_to_the_same_name_does_nothing_and_a_change_of_case_is_a_rename() {
        let dir = temp_dir("rename-case");
        let archive = sample(&dir);
        let before = fs::read(&archive).unwrap();

        let same = try_rename(&archive, Path::new("top.txt"), "top.txt", "shown")
            .unwrap()
            .unwrap();
        assert_eq!(same, PathBuf::from("top.txt"));
        assert_eq!(fs::read(&archive).unwrap(), before);

        // Asked for in another case than the archive spells it.
        let cased = try_rename(&archive, Path::new("TOP.TXT"), "Top.txt", "shown")
            .unwrap()
            .unwrap();
        assert_eq!(cased, PathBuf::from("Top.txt"));
        assert!(names(&archive).contains(&"Top.txt".to_string()));
        assert!(!names(&archive).contains(&"top.txt".to_string()));
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn an_archive_whose_names_are_not_plain_is_left_to_the_rebuild() {
        let dir = temp_dir("not-plain");
        let cases: [(&str, Contents); 3] = [
            ("escapes", &[("ok.txt", b"1"), ("../evil.txt", b"2")]),
            ("case", &[("A.txt", b"1"), ("a.txt", b"2")]),
            ("file-and-folder", &[("a", b"1"), ("a/b.txt", b"2")]),
        ];
        for (label, entries) in cases {
            let archive = dir.join(format!("{label}.zip"));
            write_zip(&archive, entries);
            let before = fs::read(&archive).unwrap();

            let first = PathBuf::from(entries[0].0);
            assert_eq!(
                try_delete(&archive, std::slice::from_ref(&first)).unwrap(),
                None,
                "{label}"
            );
            assert_eq!(
                try_rename(&archive, &first, "new.txt", "shown").unwrap(),
                None,
                "{label}"
            );
            assert_eq!(fs::read(&archive).unwrap(), before, "{label}");
        }
        let _ = fs::remove_dir_all(dir);
    }

    fn local_file(dir: &Path, name: &str, contents: &[u8]) -> PathBuf {
        let path = dir.join(name);
        fs::write(&path, contents).unwrap();
        path
    }

    fn contents_of(archive: &Path, name: &str) -> Vec<u8> {
        stored(archive)
            .into_iter()
            .find(|entry| entry.name == name)
            .unwrap_or_else(|| panic!("{name} is in the archive"))
            .contents
    }

    #[test]
    fn creating_a_folder_or_a_file_adds_one_entry_and_keeps_the_rest_as_stored() {
        let dir = temp_dir("create");
        let archive = sample(&dir);
        let before = stored(&archive);

        let folder = try_create(&archive, Path::new("docs"), "new", NewEntry::Directory)
            .unwrap()
            .expect("a plain archive");
        let file = try_create(&archive, Path::new(""), "empty.txt", NewEntry::File)
            .unwrap()
            .unwrap();

        assert_eq!(folder, PathBuf::from("docs").join("new"));
        assert_eq!(file, PathBuf::from("empty.txt"));
        let after = stored(&archive);
        assert_eq!(&after[..before.len()], &before[..]);
        assert_eq!(after[before.len()].name, "docs/new/");
        assert_eq!(after[before.len() + 1].name, "empty.txt");
        assert_eq!(after[before.len() + 1].size, 0);
        assert_eq!(
            open(&archive).unwrap().comment(),
            b"the archive's own comment"
        );
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn creating_over_a_name_that_is_taken_is_refused_and_changes_nothing() {
        let dir = temp_dir("create-taken");
        let archive = sample(&dir);
        let before = fs::read(&archive).unwrap();

        assert_eq!(
            try_create(&archive, Path::new(""), "DOCS", NewEntry::Directory).unwrap_err(),
            "Directory already exists: DOCS"
        );
        assert_eq!(
            try_create(&archive, Path::new("docs"), "a.txt", NewEntry::File).unwrap_err(),
            "File already exists: a.txt"
        );
        // `loose` exists only through the file in it.
        assert_eq!(
            try_create(&archive, Path::new(""), "loose", NewEntry::File).unwrap_err(),
            "File already exists: loose"
        );
        let below_a_file = try_create(&archive, Path::new("top.txt"), "x", NewEntry::Directory);
        assert!(
            below_a_file
                .unwrap_err()
                .starts_with("Destination is not a directory:"),
            "a file is not a folder to create in"
        );
        assert_eq!(fs::read(&archive).unwrap(), before);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn adding_a_local_file_compresses_it_and_copies_the_others_as_stored() {
        let dir = temp_dir("add-file");
        let archive = sample(&dir);
        let before = stored(&archive);
        let local = local_file(&dir, "notes.txt", &[b'n'; 6000]);

        let added = try_add(&archive, Path::new("docs"), &local, "error")
            .unwrap()
            .expect("a plain archive");

        assert_eq!(added, Some(PathBuf::from("docs").join("notes.txt")));
        let after = stored(&archive);
        assert_eq!(&after[..before.len()], &before[..]);
        let new = after.last().unwrap();
        assert_eq!(new.name, "docs/notes.txt");
        assert_eq!(new.contents, vec![b'n'; 6000]);
        assert_eq!(new.method, "Deflated");
        assert!(new.compressed_size < new.size);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn adding_a_local_folder_adds_everything_in_it() {
        let dir = temp_dir("add-folder");
        let archive = sample(&dir);
        let album = dir.join("album");
        fs::create_dir_all(album.join("2025")).unwrap();
        fs::create_dir_all(album.join("empty")).unwrap();
        local_file(&album, "cover.jpg", b"cover");
        local_file(&album.join("2025"), "a.jpg", b"first");

        // Into a folder the archive does not have yet.
        let added = try_add(&archive, Path::new("media"), &album, "error")
            .unwrap()
            .unwrap();

        assert_eq!(added, Some(PathBuf::from("media").join("album")));
        let all = names(&archive);
        for expected in [
            "media/album/",
            "media/album/cover.jpg",
            "media/album/2025/",
            "media/album/2025/a.jpg",
            "media/album/empty/",
        ] {
            assert!(all.contains(&expected.to_string()), "{expected} in {all:?}");
        }
        assert_eq!(contents_of(&archive, "media/album/2025/a.jpg"), b"first");
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_name_that_is_taken_follows_the_conflict_action() {
        let dir = temp_dir("add-conflict");
        let archive = sample(&dir);
        let local = local_file(&dir, "a.txt", b"from the disk");
        let docs = Path::new("docs");
        let before = fs::read(&archive).unwrap();

        // No answer yet: the error the app turns into a question.
        let conflict = try_add(&archive, docs, &local, "error").unwrap_err();
        assert!(
            conflict.starts_with("CONFLICT: destination already exists:"),
            "{conflict}"
        );
        assert!(conflict.ends_with("a.txt"), "{conflict}");
        assert_eq!(try_add(&archive, docs, &local, "skip").unwrap(), Some(None));
        assert_eq!(
            fs::read(&archive).unwrap(),
            before,
            "neither changed the archive"
        );

        let kept = try_add(&archive, docs, &local, "keep-both")
            .unwrap()
            .unwrap();
        assert_eq!(kept, Some(docs.join("a (1).txt")));
        assert_eq!(contents_of(&archive, "docs/a.txt"), vec![b'a'; 4000]);
        assert_eq!(contents_of(&archive, "docs/a (1).txt"), b"from the disk");
        let again = try_add(&archive, docs, &local, "rename").unwrap().unwrap();
        assert_eq!(again, Some(docs.join("a (2).txt")));

        let replaced = try_add(&archive, docs, &local, "REPLACE").unwrap().unwrap();
        assert_eq!(replaced, Some(docs.join("a.txt")));
        assert_eq!(contents_of(&archive, "docs/a.txt"), b"from the disk");
        assert_eq!(
            names(&archive)
                .iter()
                .filter(|name| *name == "docs/a.txt")
                .count(),
            1
        );
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn replacing_a_folder_removes_what_was_in_it() {
        let dir = temp_dir("add-replace-folder");
        let archive = sample(&dir);
        let docs = dir.join("docs");
        fs::create_dir_all(&docs).unwrap();
        local_file(&docs, "only.txt", b"new");

        let replaced = try_add(&archive, Path::new(""), &docs, "replace")
            .unwrap()
            .unwrap();

        assert_eq!(replaced, Some(PathBuf::from("docs")));
        let all = names(&archive);
        assert!(all.contains(&"docs/only.txt".to_string()), "{all:?}");
        assert!(!all
            .iter()
            .any(|name| name == "docs/a.txt" || name.starts_with("docs/sub")));
        assert!(all.contains(&"top.txt".to_string()));
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn adding_into_a_file_or_adding_a_missing_source_is_an_error() {
        let dir = temp_dir("add-errors");
        let archive = sample(&dir);
        let local = local_file(&dir, "x.txt", b"x");
        let before = fs::read(&archive).unwrap();

        let into_a_file = try_add(&archive, Path::new("top.txt"), &local, "error").unwrap_err();
        assert!(
            into_a_file.starts_with("Destination is not a directory:"),
            "{into_a_file}"
        );
        let missing = try_add(&archive, Path::new(""), &dir.join("gone.txt"), "error").unwrap_err();
        assert!(missing.starts_with("Failed to stat source:"), "{missing}");
        assert_eq!(fs::read(&archive).unwrap(), before);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_move_inside_the_archive_renames_the_entries_and_keeps_their_bytes() {
        let dir = temp_dir("move-within");
        let archive = sample(&dir);
        let before = stored(&archive);

        let moved = try_transfer_within(
            &archive,
            &PathBuf::from("docs").join("sub"),
            Path::new("loose"),
            "error",
            true,
            "shown",
        )
        .unwrap()
        .expect("a plain archive");

        assert_eq!(moved, Some(PathBuf::from("loose").join("sub")));
        let after = stored(&archive);
        let moved_file = after
            .iter()
            .find(|entry| entry.name == "loose/sub/c.txt")
            .expect("the file moved with its folder");
        let original = before
            .iter()
            .find(|entry| entry.name == "docs/sub/c.txt")
            .unwrap();
        assert_eq!(moved_file.crc32, original.crc32);
        assert_eq!(moved_file.compressed_size, original.compressed_size);
        assert_eq!(moved_file.contents, original.contents);
        assert!(!after.iter().any(|entry| entry.name.starts_with("docs/sub")));
        assert_eq!(after.len(), before.len());
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn moving_the_last_entry_out_of_a_folder_leaves_the_folder() {
        let dir = temp_dir("move-empties");
        let archive = sample(&dir);

        let moved = try_transfer_within(
            &archive,
            &PathBuf::from("loose").join("only.txt"),
            Path::new("docs"),
            "error",
            true,
            "shown",
        )
        .unwrap()
        .unwrap();

        assert_eq!(moved, Some(PathBuf::from("docs").join("only.txt")));
        let all = names(&archive);
        assert!(all.contains(&"loose/".to_string()), "{all:?}");
        assert!(all.contains(&"docs/only.txt".to_string()));
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn a_copy_inside_the_archive_keeps_the_original_and_adds_the_copy() {
        let dir = temp_dir("copy-within");
        let archive = sample(&dir);
        let before = stored(&archive);

        let copied = try_transfer_within(
            &archive,
            Path::new("docs"),
            Path::new("loose"),
            "error",
            false,
            "shown",
        )
        .unwrap()
        .unwrap();

        assert_eq!(copied, Some(PathBuf::from("loose").join("docs")));
        let after = stored(&archive);
        assert_eq!(&after[..before.len()], &before[..]);
        let copies: Vec<&str> = after[before.len()..]
            .iter()
            .map(|entry| entry.name.as_str())
            .collect();
        assert_eq!(
            copies,
            vec![
                "loose/docs/",
                "loose/docs/a.txt",
                "loose/docs/b.txt",
                "loose/docs/sub/c.txt"
            ]
        );
        assert_eq!(contents_of(&archive, "loose/docs/a.txt"), vec![b'a'; 4000]);

        // A second copy beside the first, kept under another name.
        let kept = try_transfer_within(
            &archive,
            &PathBuf::from("docs").join("a.txt"),
            Path::new("docs"),
            "keep-both",
            false,
            "shown",
        )
        .unwrap()
        .unwrap();
        assert_eq!(kept, Some(PathBuf::from("docs").join("a (1).txt")));
        assert_eq!(contents_of(&archive, "docs/a (1).txt"), vec![b'a'; 4000]);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn transfers_inside_the_archive_that_cannot_be_done_change_nothing() {
        let dir = temp_dir("within-errors");
        let archive = sample(&dir);
        let before = fs::read(&archive).unwrap();
        let within = |source: &str, dest: &str, action: &str, is_move: bool| {
            try_transfer_within(
                &archive,
                Path::new(source),
                Path::new(dest),
                action,
                is_move,
                "the shown path",
            )
        };

        assert_eq!(
            within("gone", "docs", "error", true).unwrap_err(),
            "Archive entry not found: the shown path"
        );
        assert_eq!(
            within("docs", "docs/sub", "error", false).unwrap_err(),
            "Cannot copy a directory into itself or one of its subdirectories"
        );
        // A file onto the folder it is in.
        assert_eq!(
            within("docs/sub/c.txt", "docs", "replace", true).unwrap(),
            Some(Some(PathBuf::from("docs").join("c.txt"))),
            "no file named c.txt is in docs, so this is a plain move"
        );
        fs::write(&archive, &before).unwrap();
        assert_eq!(
            within("docs/sub", "", "replace", true).unwrap(),
            Some(Some(PathBuf::from("sub")))
        );
        fs::write(&archive, &before).unwrap();

        // Where it already is: nothing to do, whatever the action.
        assert_eq!(
            within("docs/a.txt", "docs", "replace", true).unwrap(),
            Some(Some(PathBuf::from("docs").join("a.txt")))
        );
        assert_eq!(
            within("docs/a.txt", "docs", "skip", true).unwrap(),
            Some(None)
        );
        let conflict = within("docs/a.txt", "docs", "error", false).unwrap_err();
        assert!(conflict.starts_with("CONFLICT:"), "{conflict}");
        assert_eq!(fs::read(&archive).unwrap(), before);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn replacing_a_folder_with_an_item_from_inside_it_is_refused() {
        let dir = temp_dir("within-replace-parent");
        let archive = dir.join("nested.zip");
        write_zip(
            &archive,
            &[("box/box/inner.txt", b"inner"), ("box/other.txt", b"other")],
        );
        let before = fs::read(&archive).unwrap();

        // `box/box` moved to the root would replace `box`, which holds it.
        let refused = try_transfer_within(
            &archive,
            &PathBuf::from("box").join("box"),
            Path::new(""),
            "replace",
            true,
            "shown",
        );

        assert_eq!(
            refused.unwrap_err(),
            "Cannot replace a folder with an item from inside it"
        );
        assert_eq!(fs::read(&archive).unwrap(), before);
        let _ = fs::remove_dir_all(dir);
    }

    #[test]
    fn the_new_operations_leave_an_archive_that_is_not_plain_to_the_rebuild() {
        let dir = temp_dir("not-plain-new");
        let archive = dir.join("case.zip");
        write_zip(&archive, &[("A.txt", b"1"), ("a.txt", b"2")]);
        let local = local_file(&dir, "x.txt", b"x");
        let before = fs::read(&archive).unwrap();

        assert_eq!(
            try_create(&archive, Path::new(""), "new", NewEntry::Directory).unwrap(),
            None
        );
        assert_eq!(
            try_add(&archive, Path::new(""), &local, "error").unwrap(),
            None
        );
        assert_eq!(
            try_transfer_within(
                &archive,
                Path::new("A.txt"),
                Path::new("d"),
                "error",
                true,
                "s"
            )
            .unwrap(),
            None
        );
        assert_eq!(fs::read(&archive).unwrap(), before);
        let _ = fs::remove_dir_all(dir);
    }
}
