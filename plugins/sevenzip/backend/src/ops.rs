//! The archive operations, each one 7-Zip command (plus a listing where it
//! needs one), never one process per entry.
//!
//! - `extract_all` and `read_entry`: `l`, then one `x`.
//! - `rename` and `delete`: `l`, then one `rn` or `d`.
//! - `create` and `add`: one `a`.
//!
//! Every command runs with `-sccUTF-8 -scsUTF-8 -spd -ssc -y`; the ones
//! that do the work add `-bsp1 -bso0` so the percentage can be read from
//! stdout. `-ssc` makes 7-Zip match names by exact case, as the listing and
//! [`is_within`] do; core sends every name spelled exactly as listed. On
//! Windows 7-Zip otherwise ignores case, so with `a.txt` and `A.TXT` in one
//! archive, `x`, `d` and `rn` on `a.txt` would also read, delete or rename
//! `A.TXT`, and `a` of `A.TXT` would replace `a.txt`.
//! Names travel in UTF-8 listfiles through `-i@`, because `--` also stops
//! `@listfile` parsing; each line is quoted, because 7-Zip trims listfile
//! lines and a leading space is part of a valid name. Update commands on an existing archive (`a`, `d`,
//! `rn`) write their temporary archive into a dot folder beside the
//! archive (`-w`), which is removed afterwards; 7-Zip replaces the archive
//! only once the new one is complete, so a failed or cancelled update
//! leaves it as it was.
//!
//! Safety: every listed name passes [`validate_entry_path`] before anything
//! is written; `extract_all` and `read_entry` refuse a non-empty
//! destination and fail, removing their output, when they find a link in
//! it; `read_entry` checks declared sizes against [`Limits`] before it runs
//! 7-Zip and the written bytes afterwards; `create` refuses an existing
//! archive; and `delete` refuses to remove every entry.

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use crate::entries::{is_within, parse_list_output, validate_entry_path, SevenZipEntry};
use crate::run::{run_7za_as, RunObserver};

/// Error text when a delete would leave a `.7z` with no entries, which
/// 7-Zip writes as an empty, 32-byte archive.
pub const NO_ENTRIES_MESSAGE: &str = "7-Zip archives cannot be rewritten with no entries";

const COMMON: [&str; 5] = ["-sccUTF-8", "-scsUTF-8", "-spd", "-ssc", "-y"];
const PROGRESS: [&str; 2] = ["-bsp1", "-bso0"];
/// An empty password: an encrypted archive fails at once instead of
/// prompting. Only `l` and `x` take it; on an update command `-p` asks for
/// a new password.
const NO_PASSWORD: &str = "-p";

/// Budget for one `read_entry` selection.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Limits {
    pub max_bytes: u64,
    pub max_entries: u32,
}

/// Runs one 7-Zip executable.
#[derive(Debug, Clone)]
pub struct SevenZip {
    binary: PathBuf,
}

/// A listed entry with its validated, `/`-separated path.
struct Listed {
    entry: SevenZipEntry,
    path: String,
}

/// Watches nothing and is never cancelled.
struct Unobserved;

impl RunObserver for Unobserved {
    fn progress(&mut self, _percent: u8, _item: Option<&str>) {}
    fn is_cancelled(&self) -> bool {
        false
    }
}

impl SevenZip {
    pub fn new(binary: PathBuf) -> Self {
        SevenZip { binary }
    }

    pub fn binary(&self) -> &Path {
        &self.binary
    }

    /// Every entry, with paths as 7-Zip printed them.
    pub fn list(&self, archive: &Path) -> Result<Vec<SevenZipEntry>, String> {
        self.list_observed(archive, &mut Unobserved)
    }

    fn list_observed(
        &self,
        archive: &Path,
        observer: &mut dyn RunObserver,
    ) -> Result<Vec<SevenZipEntry>, String> {
        let mut args = args(&["l", "-slt", "-bd", "-bb0"]);
        args.extend(os(&COMMON));
        args.push(NO_PASSWORD.into());
        args.push("--".into());
        args.push(archive.into());
        let stdout = run_7za_as("7-Zip list", &self.binary, &args, None, observer)?;
        Ok(parse_list_output(&String::from_utf8_lossy(&stdout)))
    }

    /// The listing with every name validated.
    fn validated(
        &self,
        archive: &Path,
        observer: &mut dyn RunObserver,
    ) -> Result<Vec<Listed>, String> {
        self.list_observed(archive, observer)?
            .into_iter()
            .map(|entry| {
                Ok(Listed {
                    path: validate_entry_path(&entry.path)?,
                    entry,
                })
            })
            .collect()
    }

    /// Extracts every entry into `destination`, an empty or missing folder.
    pub fn extract_all(
        &self,
        archive: &Path,
        destination: &Path,
        observer: &mut dyn RunObserver,
    ) -> Result<(), String> {
        prepare_destination(destination)?;
        let entries = self.validated(archive, observer)?;
        let total = declared_bytes(entries.iter());
        observer.total(total);
        observer.progress(0, None);
        let extracted = self
            .run_extract(archive, destination, None, observer)
            .and_then(|()| check_no_links(destination));
        if let Err(error) = extracted {
            clear_dir(destination);
            return Err(error);
        }
        observer.progress(100, None);
        Ok(())
    }

    /// Extracts `entry` (`/`-separated) and everything under it, by whole
    /// segments, at its own relative path inside `destination`, an empty
    /// or missing folder.
    pub fn read_entry(
        &self,
        archive: &Path,
        entry: &str,
        destination: &Path,
        limits: Limits,
        observer: &mut dyn RunObserver,
    ) -> Result<(), String> {
        let wanted = validate_entry_path(entry)?;
        prepare_destination(destination)?;
        let entries = self.validated(archive, observer)?;
        let selected: Vec<&Listed> = entries
            .iter()
            .filter(|listed| is_within(&listed.path, &wanted))
            .collect();
        if selected.is_empty() {
            return Err(format!("Archive entry not found: {entry}"));
        }
        if selected.len() as u64 > u64::from(limits.max_entries) {
            return Err(format!(
                "Archive extract exceeds entry limit of {} entries",
                limits.max_entries
            ));
        }
        let declared = selected
            .iter()
            .try_fold(0u64, |sum, listed| sum.checked_add(listed.entry.size))
            .ok_or_else(|| "Archive extract size overflow".to_string())?;
        if declared > limits.max_bytes {
            return Err(size_limit(limits.max_bytes));
        }
        observer.total(declared);
        observer.progress(0, None);

        let read = (|| {
            let files: Vec<&Listed> = selected
                .iter()
                .copied()
                .filter(|listed| !listed.entry.is_dir)
                .collect();
            if !files.is_empty() {
                let listfile =
                    Listfile::write(files.iter().map(|listed| listed.entry.path.as_str()))?;
                self.run_extract(archive, destination, Some(listfile.path()), observer)?;
            }
            for listed in selected.iter().filter(|listed| listed.entry.is_dir) {
                let folder = join_entry(destination, &listed.path);
                std::fs::create_dir_all(&folder)
                    .map_err(|e| format!("Failed to create {}: {e}", folder.display()))?;
            }
            check_no_links(destination)?;
            for listed in &files {
                if !join_entry(destination, &listed.path).is_file() {
                    return Err(format!("7-Zip did not extract {}", listed.path));
                }
            }
            if written_bytes(destination)? > limits.max_bytes {
                return Err(size_limit(limits.max_bytes));
            }
            Ok(())
        })();
        if let Err(error) = read {
            clear_dir(destination);
            return Err(error);
        }
        observer.progress(100, None);
        Ok(())
    }

    fn run_extract(
        &self,
        archive: &Path,
        destination: &Path,
        include: Option<&Path>,
        observer: &mut dyn RunObserver,
    ) -> Result<(), String> {
        let mut args = args(&["x"]);
        args.push(prefixed("-o", destination));
        args.extend(os(&COMMON));
        args.extend(os(&PROGRESS));
        args.push(NO_PASSWORD.into());
        if let Some(listfile) = include {
            args.push(prefixed("-i@", listfile));
        }
        args.push("--".into());
        args.push(archive.into());
        run_7za_as("7-Zip extraction", &self.binary, &args, None, observer).map(drop)
    }

    /// A new `.7z` at `archive` holding each of `paths` (a file or a tree)
    /// under its own name.
    pub fn create(
        &self,
        archive: &Path,
        paths: &[PathBuf],
        observer: &mut dyn RunObserver,
    ) -> Result<(), String> {
        if paths.is_empty() {
            return Err("No files specified".to_string());
        }
        if std::fs::symlink_metadata(archive).is_ok() {
            return Err(format!("Archive already exists: {}", archive.display()));
        }
        let total = source_bytes(paths.iter().map(PathBuf::as_path))?;
        observer.total(total);
        observer.progress(0, None);
        let listfile = Listfile::write_paths(paths.iter().map(PathBuf::as_path))?;
        let mut args = args(&["a", "-t7z"]);
        args.extend(os(&COMMON));
        args.extend(os(&PROGRESS));
        args.push("-sse".into());
        args.push(prefixed("-i@", listfile.path()));
        args.push("--".into());
        args.push(archive.into());
        if let Err(error) = run_7za_as(
            "7-Zip archive creation",
            &self.binary,
            &args,
            None,
            observer,
        ) {
            // Nothing was there before, so whatever is there now is ours.
            let _ = std::fs::remove_file(archive);
            return Err(error);
        }
        observer.progress(100, None);
        Ok(())
    }

    /// Moves `entry` and its descendants to the full path `new_path`.
    pub fn rename(
        &self,
        archive: &Path,
        entry: &str,
        new_path: &str,
        observer: &mut dyn RunObserver,
    ) -> Result<(), String> {
        let from = validate_entry_path(entry)?;
        let to = validate_entry_path(new_path)?;
        let entries = self.validated(archive, observer)?;
        if !entries.iter().any(|listed| is_within(&listed.path, &from)) {
            return Err(format!("Archive entry not found: {from}"));
        }
        if from == to {
            return Ok(());
        }
        if is_within(&to, &from) {
            return Err(format!("Cannot move {from} into itself"));
        }
        if entries
            .iter()
            .any(|listed| !is_within(&listed.path, &from) && is_within(&listed.path, &to))
        {
            return Err(format!("Archive entry already exists: {to}"));
        }
        observer.total(declared_bytes(entries.iter()));
        observer.progress(0, None);
        let work = WorkDir::beside(archive)?;
        let mut args = args(&["rn"]);
        args.extend(os(&COMMON));
        args.extend(os(&PROGRESS));
        args.push(prefixed("-w", work.path()));
        args.push("--".into());
        args.push(archive.into());
        args.push(windows_path(&from).into());
        args.push(windows_path(&to).into());
        run_7za_as("7-Zip rename", &self.binary, &args, None, observer)?;
        drop(work);
        observer.progress(100, None);
        Ok(())
    }

    /// Removes each entry and its descendants. Refuses to remove every
    /// entry ([`NO_ENTRIES_MESSAGE`]) and fails before changing anything
    /// when an entry is missing.
    pub fn delete(
        &self,
        archive: &Path,
        entries: &[String],
        observer: &mut dyn RunObserver,
    ) -> Result<(), String> {
        let wanted = entries
            .iter()
            .map(|entry| validate_entry_path(entry))
            .collect::<Result<Vec<_>, _>>()?;
        if wanted.is_empty() {
            return Err("No entries specified".to_string());
        }
        let listed = self.validated(archive, observer)?;
        if let Some(missing) = wanted
            .iter()
            .find(|wanted| !listed.iter().any(|l| is_within(&l.path, wanted)))
        {
            return Err(format!("Archive entry not found: {missing}"));
        }
        if listed
            .iter()
            .all(|l| wanted.iter().any(|wanted| is_within(&l.path, wanted)))
        {
            return Err(NO_ENTRIES_MESSAGE.to_string());
        }
        observer.total(declared_bytes(listed.iter()));
        observer.progress(0, None);
        let work = WorkDir::beside(archive)?;
        let listfile = Listfile::write(wanted.iter().map(|path| windows_path(path)))?;
        let mut args = args(&["d"]);
        args.extend(os(&COMMON));
        args.extend(os(&PROGRESS));
        args.push(prefixed("-w", work.path()));
        args.push(prefixed("-i@", listfile.path()));
        args.push("--".into());
        args.push(archive.into());
        run_7za_as("7-Zip delete", &self.binary, &args, None, observer)?;
        drop(work);
        observer.progress(100, None);
        Ok(())
    }

    /// Stores `base_dir/<p>` (a file or a tree) at `<p>` for each
    /// `/`-separated `p`.
    pub fn add(
        &self,
        archive: &Path,
        base_dir: &Path,
        paths: &[String],
        observer: &mut dyn RunObserver,
    ) -> Result<(), String> {
        let relative = paths
            .iter()
            .map(|path| validate_entry_path(path))
            .collect::<Result<Vec<_>, _>>()?;
        if relative.is_empty() {
            return Err("No files specified".to_string());
        }
        let sources: Vec<PathBuf> = relative
            .iter()
            .map(|path| join_entry(base_dir, path))
            .collect();
        let total = source_bytes(sources.iter().map(PathBuf::as_path))?;
        observer.total(total);
        observer.progress(0, None);
        let work = WorkDir::beside(archive)?;
        let listfile = Listfile::write(relative.iter().map(|path| windows_path(path)))?;
        let mut args = args(&["a", "-t7z"]);
        args.extend(os(&COMMON));
        args.extend(os(&PROGRESS));
        args.push("-sse".into());
        args.push(prefixed("-w", work.path()));
        args.push(prefixed("-i@", listfile.path()));
        args.push("--".into());
        args.push(archive.into());
        run_7za_as("7-Zip add", &self.binary, &args, Some(base_dir), observer)?;
        drop(work);
        observer.progress(100, None);
        Ok(())
    }
}

fn args(items: &[&str]) -> Vec<OsString> {
    os(items)
}

fn os(items: &[&str]) -> Vec<OsString> {
    items.iter().map(OsString::from).collect()
}

/// A switch with a path value, e.g. `-o<dir>`.
fn prefixed(switch: &str, path: &Path) -> OsString {
    let mut arg = OsString::from(switch);
    arg.push(path.as_os_str());
    arg
}

/// A `/`-separated entry path as 7-Zip names it on Windows.
fn windows_path(entry: &str) -> String {
    entry.replace('/', "\\")
}

/// `base` joined with a validated `/`-separated entry path.
fn join_entry(base: &Path, entry: &str) -> PathBuf {
    entry
        .split('/')
        .fold(base.to_path_buf(), |path, segment| path.join(segment))
}

fn size_limit(max_bytes: u64) -> String {
    format!("Archive extract exceeds size limit of {max_bytes} bytes")
}

fn declared_bytes<'a>(entries: impl Iterator<Item = &'a Listed>) -> u64 {
    entries.fold(0u64, |sum, listed| sum.saturating_add(listed.entry.size))
}

/// An empty folder; a missing one is created.
fn prepare_destination(destination: &Path) -> Result<(), String> {
    match std::fs::read_dir(destination) {
        Ok(mut children) => {
            if children.next().is_some() {
                return Err(format!(
                    "Destination is not empty: {}",
                    destination.display()
                ));
            }
            Ok(())
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => std::fs::create_dir_all(destination)
            .map_err(|e| format!("Failed to create {}: {e}", destination.display())),
        Err(e) => Err(format!(
            "Cannot use destination {}: {e}",
            destination.display()
        )),
    }
}

/// A symlink or a junction. On Windows `is_symlink` is true for every
/// reparse point whose tag is a name surrogate, junctions included. Other
/// reparse points, such as cloud-file placeholders (OneDrive Files
/// On-Demand) and deduplicated or WOF-compressed files, are ordinary files
/// and folders.
fn is_link(meta: &std::fs::Metadata) -> bool {
    meta.file_type().is_symlink()
}

/// Fails on the first link under `root`, naming it by its relative path.
fn check_no_links(root: &Path) -> Result<(), String> {
    fn walk(root: &Path, dir: &Path) -> Result<(), String> {
        let children =
            std::fs::read_dir(dir).map_err(|e| format!("Cannot read {}: {e}", dir.display()))?;
        for child in children {
            let child = child.map_err(|e| format!("Cannot read {}: {e}", dir.display()))?;
            let path = child.path();
            let meta = std::fs::symlink_metadata(&path)
                .map_err(|e| format!("Cannot read {}: {e}", path.display()))?;
            if is_link(&meta) {
                let relative = path.strip_prefix(root).unwrap_or(&path);
                let shown = relative
                    .components()
                    .map(|c| c.as_os_str().to_string_lossy().into_owned())
                    .collect::<Vec<_>>()
                    .join("/");
                return Err(format!("Archive entry is a link: {shown}"));
            }
            if meta.is_dir() {
                walk(root, &path)?;
            }
        }
        Ok(())
    }
    walk(root, root)
}

/// The bytes of every file under `root`; links are not followed.
fn written_bytes(root: &Path) -> Result<u64, String> {
    source_bytes(std::iter::once(root))
}

/// The bytes of every file at or under `paths`; links count nothing and
/// are not followed.
fn source_bytes<'a>(mut paths: impl Iterator<Item = &'a Path>) -> Result<u64, String> {
    fn walk(path: &Path) -> Result<u64, String> {
        let meta = std::fs::symlink_metadata(path)
            .map_err(|e| format!("Cannot read {}: {e}", path.display()))?;
        if is_link(&meta) {
            return Ok(0);
        }
        if !meta.is_dir() {
            return Ok(meta.len());
        }
        let mut sum = 0u64;
        let children =
            std::fs::read_dir(path).map_err(|e| format!("Cannot read {}: {e}", path.display()))?;
        for child in children {
            let child = child.map_err(|e| format!("Cannot read {}: {e}", path.display()))?;
            sum = sum.saturating_add(walk(&child.path())?);
        }
        Ok(sum)
    }
    paths.try_fold(0u64, |sum, path| Ok(sum.saturating_add(walk(path)?)))
}

/// Removes everything inside `root`, which stays. Links are removed, never
/// followed.
fn clear_dir(root: &Path) {
    let Ok(children) = std::fs::read_dir(root) else {
        return;
    };
    for child in children.flatten() {
        let path = child.path();
        let Ok(meta) = std::fs::symlink_metadata(&path) else {
            continue;
        };
        if is_link(&meta) {
            if std::fs::remove_dir(&path).is_err() {
                let _ = std::fs::remove_file(&path);
            }
        } else if meta.is_dir() {
            let _ = std::fs::remove_dir_all(&path);
        } else {
            let _ = std::fs::remove_file(&path);
        }
    }
}

/// A name no other run of this process uses at the same time.
fn unique_suffix() -> String {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|elapsed| elapsed.as_nanos())
        .unwrap_or(0);
    format!(
        "{}-{nanos}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )
}

/// A UTF-8 listfile, one quoted name per line, in the temp folder. Removed
/// on drop.
///
/// 7-Zip trims each listfile line and then strips one pair of surrounding
/// quotes, so a bare ` lead.txt` would select `lead.txt`. Every line is
/// written as `"<name>"`; a quote is not valid in a Windows name and
/// [`validate_entry_path`] rejects it, so the quoting is never ambiguous.
struct Listfile {
    path: PathBuf,
}

/// One listfile line for `name`, or an error when no line can carry it.
fn listfile_line(name: &str) -> Result<String, String> {
    if name.is_empty() || name.contains(['"', '\r', '\n', '\0']) {
        return Err(format!("Name cannot be passed to 7-Zip: {name:?}"));
    }
    Ok(format!("\"{name}\"\n"))
}

impl Listfile {
    fn write<S: AsRef<str>>(names: impl Iterator<Item = S>) -> Result<Listfile, String> {
        let mut content = String::new();
        for name in names {
            content.push_str(&listfile_line(name.as_ref())?);
        }
        let path = std::env::temp_dir().join(format!("sumafile-7z-list-{}.txt", unique_suffix()));
        std::fs::write(&path, content)
            .map_err(|e| format!("Failed to write 7-Zip listfile {}: {e}", path.display()))?;
        Ok(Listfile { path })
    }

    fn write_paths<'a>(paths: impl Iterator<Item = &'a Path>) -> Result<Listfile, String> {
        let names = paths
            .map(|path| {
                path.to_str()
                    .map(str::to_string)
                    .ok_or_else(|| format!("Path is not valid Unicode: {}", path.to_string_lossy()))
            })
            .collect::<Result<Vec<_>, _>>()?;
        Listfile::write(names.iter())
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for Listfile {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.path);
    }
}

/// The dot folder beside an archive that an update command writes its
/// temporary archive into. Removed on drop.
struct WorkDir {
    path: PathBuf,
}

impl WorkDir {
    fn beside(archive: &Path) -> Result<WorkDir, String> {
        let parent = archive
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
            .unwrap_or_else(|| Path::new("."));
        let name = archive
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        let path = parent.join(format!(".{name}.sumafile-{}", unique_suffix()));
        std::fs::create_dir(&path)
            .map_err(|e| format!("Failed to create 7-Zip work folder {}: {e}", path.display()))?;
        Ok(WorkDir { path })
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for WorkDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn listfile_lines_are_quoted_so_7zip_keeps_leading_spaces() {
        assert_eq!(listfile_line(" lead.txt").unwrap(), "\" lead.txt\"\n");
        assert_eq!(
            listfile_line(" docs\\inner.txt").unwrap(),
            "\" docs\\inner.txt\"\n"
        );
        let listfile = Listfile::write([" a", "b"].into_iter()).unwrap();
        assert_eq!(
            std::fs::read_to_string(listfile.path()).unwrap(),
            "\" a\"\n\"b\"\n"
        );
    }

    #[test]
    fn listfile_refuses_names_a_quoted_line_cannot_carry() {
        for name in ["", "a\"b", "a\nb", "a\rb", "a\0b"] {
            assert!(listfile_line(name).is_err(), "{name:?} was accepted");
            assert!(Listfile::write([name].into_iter()).is_err());
        }
    }

    #[cfg(windows)]
    fn make_junction(link: &Path, target: &Path) {
        std::fs::create_dir_all(target).unwrap();
        let status = std::process::Command::new("cmd")
            .arg("/c")
            .arg("mklink")
            .arg("/J")
            .arg(link)
            .arg(target)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .unwrap();
        assert!(status.success(), "mklink /J {} failed", link.display());
        assert!(is_link(&std::fs::symlink_metadata(link).unwrap()));
    }

    /// Gives `path` a reparse point whose tag is not a name surrogate, the
    /// kind a cloud-file placeholder (OneDrive Files On-Demand) or a
    /// deduplicated or WOF-compressed file carries. The tag is a
    /// third-party one that no filter serves, so afterwards only metadata
    /// that does not follow reparse points can be read.
    #[cfg(windows)]
    fn make_reparse_point_that_is_not_a_link(path: &Path) {
        use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
        use std::os::windows::io::AsRawHandle;

        const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;
        const FILE_FLAG_BACKUP_SEMANTICS: u32 = 0x0200_0000;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        const FSCTL_SET_REPARSE_POINT: u32 = 0x0009_00A4;
        // Neither the Microsoft bit (0x8000_0000) nor the name-surrogate
        // bit (0x2000_0000).
        const TAG: u32 = 0x0000_5346;

        #[link(name = "kernel32")]
        extern "system" {
            fn DeviceIoControl(
                device: *mut std::ffi::c_void,
                control_code: u32,
                in_buffer: *const std::ffi::c_void,
                in_buffer_size: u32,
                out_buffer: *mut std::ffi::c_void,
                out_buffer_size: u32,
                bytes_returned: *mut u32,
                overlapped: *mut std::ffi::c_void,
            ) -> i32;
        }

        let file = std::fs::OpenOptions::new()
            .write(true)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS)
            .open(path)
            .unwrap();
        // REPARSE_GUID_DATA_BUFFER: tag, data length, reserved, GUID, data.
        let data = *b"test";
        let mut buffer = Vec::new();
        buffer.extend_from_slice(&TAG.to_le_bytes());
        buffer.extend_from_slice(&(data.len() as u16).to_le_bytes());
        buffer.extend_from_slice(&[0; 2]);
        buffer.extend_from_slice(&[0x53; 16]);
        buffer.extend_from_slice(&data);
        let mut returned = 0u32;
        // SAFETY: the handle stays open for the call, the input pointer and
        // length describe `buffer`, and there is no output buffer and no
        // overlapped I/O.
        let ok = unsafe {
            DeviceIoControl(
                file.as_raw_handle(),
                FSCTL_SET_REPARSE_POINT,
                buffer.as_ptr().cast(),
                buffer.len() as u32,
                std::ptr::null_mut(),
                0,
                &mut returned,
                std::ptr::null_mut(),
            )
        };
        assert_ne!(
            ok,
            0,
            "FSCTL_SET_REPARSE_POINT on {}: {}",
            path.display(),
            std::io::Error::last_os_error()
        );
        drop(file);
        let meta = std::fs::symlink_metadata(path).unwrap();
        assert_ne!(meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT, 0);
        assert!(!meta.file_type().is_symlink());
    }

    /// A sync client can turn extracted files into cloud-file placeholders
    /// before the walk; they are reparse points, but not links.
    #[cfg(windows)]
    #[test]
    fn check_no_links_refuses_junctions_but_not_other_reparse_points() {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("out");
        std::fs::create_dir_all(root.join("docs")).unwrap();
        std::fs::write(root.join("docs").join("a.txt"), "alpha").unwrap();
        let placeholder = root.join("docs").join("placeholder.txt");
        std::fs::write(&placeholder, "hydrated").unwrap();
        make_reparse_point_that_is_not_a_link(&placeholder);

        assert_eq!(check_no_links(&root), Ok(()));
        assert_eq!(written_bytes(&root), Ok(13));

        make_junction(
            &root.join("docs").join("linked"),
            &dir.path().join("target"),
        );
        assert_eq!(
            check_no_links(&root),
            Err("Archive entry is a link: docs/linked".to_string())
        );
    }

    #[cfg(windows)]
    #[test]
    fn clear_dir_removes_a_junction_without_touching_its_target() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("target");
        std::fs::create_dir_all(&target).unwrap();
        std::fs::write(target.join("keep.txt"), "keep").unwrap();
        let root = dir.path().join("out");
        std::fs::create_dir_all(root.join("docs")).unwrap();
        std::fs::write(root.join("docs").join("a.txt"), "alpha").unwrap();
        make_junction(&root.join("linked"), &target);
        make_junction(&root.join("docs").join("nested"), &target);
        let placeholder = root.join("placeholder.txt");
        std::fs::write(&placeholder, "hydrated").unwrap();
        make_reparse_point_that_is_not_a_link(&placeholder);

        clear_dir(&root);

        assert!(root.is_dir());
        assert_eq!(std::fs::read_dir(&root).unwrap().count(), 0);
        assert_eq!(std::fs::read(target.join("keep.txt")).unwrap(), b"keep");
    }
}
