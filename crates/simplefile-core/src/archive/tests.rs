use super::extract::{
    extract_archive_entry_to_directory, extract_archive_to_directory, extract_tar, extract_zip,
    ArchiveStaging, ExtractLimits,
};
use super::mutate::{
    copy_archive_entry_to_local_with_limits, create_archive_file, delete_archive_entry,
    materialize_archive_entry_to_temp_with_limits, move_entry_resolved,
};
use super::path::{
    archive_entry_relative_path, build_virtual_archive_path,
    ensure_extract_path_within_destination, path_is_within_prefix, split_archive_path,
    unique_engine_temp_path, zip_entry_relative_path, ArchiveKind,
};
use super::*;
use json_engine::{
    make_junction, read_json_archive, register_json_engine, register_json_engine_with,
    write_json_archive, JsonEngine,
};
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

/// The engine registry is process-wide, and `.7z` is the one extension
/// several tests share: some register it, others assert that no engine
/// serves it. Every such test takes this lock first.
static SEVEN_Z_REGISTRY_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());

fn lock_seven_z_registry() -> std::sync::MutexGuard<'static, ()> {
    SEVEN_Z_REGISTRY_LOCK
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
}

mod json_engine;

fn unique_temp_dir(name: &str) -> PathBuf {
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system clock should be after epoch")
        .as_nanos();
    let dir = std::env::temp_dir().join(format!("simplefile-archive-test-{}-{}", name, nonce));
    fs::create_dir_all(&dir).expect("create temp test directory");
    dir
}

fn write_test_zip(zip_path: &Path, entries: &[(&str, &[u8])]) {
    let file = fs::File::create(zip_path).expect("create test zip");
    let mut zip = zip::ZipWriter::new(file);
    let options =
        zip::write::SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored);

    for (name, contents) in entries {
        zip.start_file(name, options).expect("start zip entry");
        zip.write_all(contents).expect("write zip entry");
    }

    zip.finish().expect("finish test zip");
}

#[test]
fn zip_unicode_names_round_trip_through_listing_and_extraction() {
    let root = unique_temp_dir("zip-unicode-name");
    let archive = root.join("unicode.zip");
    write_test_zip(&archive, &[("café/資料.txt", b"unicode content")]);
    let listing = list_archive(archive.to_string_lossy().into_owned()).expect("list Unicode ZIP");
    assert!(listing.unsafe_entries.is_empty());
    assert_eq!(listing.entries[0].path, "café/資料.txt");
    let destination = root.join("out");
    fs::create_dir(&destination).expect("create output");
    extract_zip(&archive.to_string_lossy(), &destination).expect("extract Unicode ZIP");
    assert_eq!(
        fs::read(destination.join("café/資料.txt")).unwrap(),
        b"unicode content"
    );
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn zip_legacy_cp437_names_round_trip_through_listing_and_extraction() {
    let root = unique_temp_dir("zip-cp437-name");
    let archive = root.join("legacy.zip");
    write_test_zip(&archive, &[("cafx.txt", b"payload")]);
    let mut bytes = fs::read(&archive).unwrap();
    // Legacy ZIP names are not necessarily UTF-8: 0x82 is e-acute in CP437.
    for offset in 0..bytes.len() - 7 {
        if &bytes[offset..offset + 8] == b"cafx.txt" {
            bytes[offset + 3] = 0x82;
        }
    }
    fs::write(&archive, bytes).unwrap();
    let listing = list_archive(archive.to_string_lossy().into_owned()).expect("list legacy ZIP");
    assert!(listing.unsafe_entries.is_empty());
    assert_eq!(listing.entries[0].path, "café.txt");
    let destination = root.join("out");
    fs::create_dir(&destination).unwrap();
    extract_zip(&archive.to_string_lossy(), &destination).expect("extract legacy ZIP");
    assert_eq!(fs::read(destination.join("café.txt")).unwrap(), b"payload");
    fs::remove_dir_all(root).unwrap();
}

fn write_test_tar(tar_path: &Path, entries: &[(&str, &[u8])]) {
    let file = fs::File::create(tar_path).expect("create test tar");
    let mut archive = tar::Builder::new(file);

    for &(name, contents) in entries {
        let mut header = tar::Header::new_gnu();
        header.set_path(name).expect("set tar entry path");
        header.set_size(contents.len() as u64);
        header.set_mode(0o644);
        header.set_cksum();
        let mut reader = contents;
        archive
            .append(&header, &mut reader)
            .expect("append tar entry");
    }

    archive.finish().expect("finish test tar");
}

fn write_test_tar_with_symlink(tar_path: &Path) {
    let file = fs::File::create(tar_path).expect("create test tar");
    let mut archive = tar::Builder::new(file);

    let mut data_header = tar::Header::new_gnu();
    data_header.set_path("data.txt").expect("set data path");
    data_header.set_size(4);
    data_header.set_mode(0o644);
    data_header.set_cksum();
    let mut data: &[u8] = b"data";
    archive
        .append(&data_header, &mut data)
        .expect("append regular entry");

    let mut link_header = tar::Header::new_gnu();
    link_header.set_path("link.txt").expect("set link path");
    link_header.set_entry_type(tar::EntryType::Symlink);
    link_header
        .set_link_name("data.txt")
        .expect("set symlink target");
    link_header.set_size(0);
    link_header.set_mode(0o777);
    link_header.set_cksum();
    archive
        .append(&link_header, std::io::empty())
        .expect("append symlink entry");

    archive.finish().expect("finish test tar");
}

#[test]
fn archive_capabilities_do_not_offer_rar_creation() {
    let capabilities = get_archive_capabilities();
    let rar = capabilities
        .formats
        .iter()
        .find(|format| format.format == "rar")
        .expect("rar capability");

    assert!(rar.can_list);
    assert!(rar.can_extract);
    assert!(!rar.can_create);
    assert!(!rar.can_modify);
}

#[test]
fn create_archive_rejects_rar_without_external_install_guidance() {
    let err = create_archive(Vec::new(), "archive.rar".to_string(), "rar".to_string())
        .expect_err("rar creation should not be supported");

    assert!(err.contains("RAR creation is not supported"));
    assert!(!err.contains("Install"));
    assert!(!err.contains("WinRAR"));
    assert!(!err.contains("7z"), "{err}");
    assert_eq!(
        err,
        "RAR creation is not supported by SumaFile. Use ZIP, TAR or TAR.GZ, or an archive format a plugin adds."
    );
}

#[test]
fn create_archive_rejects_existing_output_without_truncating() {
    let root = unique_temp_dir("create-existing-output");
    let source = root.join("source.txt");
    fs::write(&source, b"source").expect("write source");
    let archive_path = root.join("existing.zip");
    fs::write(&archive_path, b"original archive bytes").expect("write existing archive");

    let err = create_archive(
        vec![source.to_string_lossy().to_string()],
        archive_path.to_string_lossy().to_string(),
        "zip".to_string(),
    )
    .expect_err("existing output should be rejected");

    assert!(err.contains("Archive already exists"));
    assert_eq!(
        fs::read(&archive_path).expect("read existing archive"),
        b"original archive bytes"
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn create_archive_rejects_output_inside_selected_directory() {
    let root = unique_temp_dir("create-recursive-output");
    let source_dir = root.join("source");
    fs::create_dir_all(&source_dir).expect("create source dir");
    fs::write(source_dir.join("item.txt"), b"source").expect("write source");
    let archive_path = source_dir.join("nested.zip");

    let err = create_archive(
        vec![source_dir.to_string_lossy().to_string()],
        archive_path.to_string_lossy().to_string(),
        "zip".to_string(),
    )
    .expect_err("output inside selected source should be rejected");

    assert!(err.contains("Archive output cannot be inside a selected source"));
    assert!(!archive_path.exists());

    let _ = fs::remove_dir_all(root);
}

#[test]
fn rar_mutations_are_rejected_before_rebuild() {
    let root = unique_temp_dir("rar-mutation-reject");
    let archive_path = root.join("sample.rar");
    fs::write(&archive_path, b"not a real rar").expect("write placeholder rar");

    let archive_root = archive_path.to_string_lossy().to_string();
    let err = create_archive_file(archive_root.clone(), "new.txt".to_string())
        .expect_err("rar file creation should fail before extraction");
    assert!(err.contains("does not rewrite RAR"));

    let entry_path = format!("{archive_root}\\entry.txt");
    let delete_err =
        delete_archive_entry(&entry_path).expect_err("rar delete should fail before extraction");
    assert!(delete_err.contains("does not rewrite RAR"));

    let move_err = move_entry_resolved(
        entry_path,
        root.to_string_lossy().to_string(),
        "replace".to_string(),
    )
    .expect_err("rar move should fail before copying out");
    assert!(move_err.contains("does not rewrite RAR"));

    let _ = fs::remove_dir_all(root);
}

#[test]
fn tar_mutation_rejects_link_entries_before_rebuild() {
    let root = unique_temp_dir("tar-mutation-link-reject");
    let archive_path = root.join("sample.tar");
    write_test_tar_with_symlink(&archive_path);
    let before = fs::read(&archive_path).expect("read original tar");

    let err = create_archive_file(
        archive_path.to_string_lossy().to_string(),
        "new.txt".to_string(),
    )
    .expect_err("tar mutation should reject link-preservation risk");

    assert!(err.contains("cannot be modified losslessly"));
    assert!(err.contains("link.txt"));
    assert_eq!(
        fs::read(&archive_path).expect("read tar after rejected mutation"),
        before
    );

    let _ = fs::remove_dir_all(root);
}

/// Mark every entry of a stored zip as encrypted by setting bit 0 of the
/// general-purpose flags in the local and central headers. The reader then
/// reports the entries as password protected, exactly like a real ZipCrypto
/// archive, without needing an encryption feature to write one.
fn mark_zip_entries_encrypted(zip_path: &Path) {
    let mut bytes = fs::read(zip_path).unwrap();
    let mut index = 0;
    while index + 4 <= bytes.len() {
        match &bytes[index..index + 4] {
            b"PK" => {
                bytes[index + 6] |= 1;
                index += 4;
            }
            b"PK" => {
                bytes[index + 8] |= 1;
                index += 4;
            }
            _ => index += 1,
        }
    }
    fs::write(zip_path, bytes).unwrap();
}

/// A zip with password-protected entries is still listable (names and sizes
/// live in the central directory), and extraction refuses it up front with a
/// clear message instead of failing half way through.
#[test]
fn encrypted_zip_entries_list_but_do_not_extract() {
    let root = unique_temp_dir("encrypted-zip");
    let zip_path = root.join("locked.zip");
    write_test_zip(
        &zip_path,
        &[("secret.txt", b"hidden"), ("docs/readme.txt", b"hi")],
    );
    mark_zip_entries_encrypted(&zip_path);

    let info = list_archive(zip_path.to_string_lossy().to_string()).expect("list encrypted zip");
    let mut names: Vec<&str> = info
        .entries
        .iter()
        .map(|entry| entry.path.as_str())
        .collect();
    names.sort_unstable();
    assert_eq!(names, vec!["docs/readme.txt", "secret.txt"]);

    let dest = root.join("out");
    fs::create_dir_all(&dest).unwrap();
    let error = extract_zip(zip_path.to_str().unwrap(), &dest).expect_err("no password available");
    assert!(
        error.to_ascii_lowercase().contains("password")
            || error.to_ascii_lowercase().contains("encrypted"),
        "{error}"
    );
    assert!(
        fs::read_dir(&dest).unwrap().next().is_none(),
        "nothing may be written before the refusal"
    );

    let _ = fs::remove_dir_all(root);
}

/// Creating a zip and extracting it again must keep each file's modification
/// time; stamping everything "now" made archive edits rewrite history.
#[test]
fn zip_round_trip_preserves_file_modification_times() {
    use chrono::Datelike;
    let root = unique_temp_dir("zip-mtime");
    let source = root.join("old.txt");
    fs::write(&source, b"vintage").unwrap();
    let stamp = filetime::FileTime::from_unix_time(1_276_603_200, 0); // 2010-06-15 12:00 UTC
    filetime::set_file_mtime(&source, stamp).unwrap();

    let zip_path = root.join("vintage.zip");
    create_archive(
        vec![source.to_string_lossy().to_string()],
        zip_path.to_string_lossy().to_string(),
        "zip".to_string(),
    )
    .expect("create zip");
    let out = root.join("out");
    fs::create_dir_all(&out).unwrap();
    extract_zip(zip_path.to_str().unwrap(), &out).expect("extract zip");

    let extracted = fs::metadata(out.join("old.txt"))
        .unwrap()
        .modified()
        .unwrap();
    let year = chrono::DateTime::<chrono::Local>::from(extracted).year();
    assert_eq!(year, 2010, "extracted file lost its timestamp");
    let _ = fs::remove_dir_all(root);
}

#[test]
fn extract_zip_allows_nested_folder_that_does_not_exist_yet() {
    let root = unique_temp_dir("nested");
    let dest = root.join("out");
    fs::create_dir_all(&dest).expect("create destination");
    let zip_path = root.join("nested.zip");
    let entry_name =
        "218. 2025 - Latex and the City - Awlivv/01-218. 2025 - Latex and the City - Awlivv.jpeg";
    write_test_zip(&zip_path, &[(entry_name, b"image-data")]);

    extract_zip(zip_path.to_str().unwrap(), &dest).expect("nested zip should extract");

    assert_eq!(
        fs::read(
            dest.join("218. 2025 - Latex and the City - Awlivv")
                .join("01-218. 2025 - Latex and the City - Awlivv.jpeg")
        )
        .expect("read extracted file"),
        b"image-data"
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn extract_zip_rejects_parent_traversal() {
    let root = unique_temp_dir("traversal");
    let dest = root.join("out");
    fs::create_dir_all(&dest).expect("create destination");
    let zip_path = root.join("evil.zip");
    write_test_zip(&zip_path, &[("../evil.txt", b"bad")]);

    let err = extract_zip(zip_path.to_str().unwrap(), &dest)
        .expect_err("traversal path should be rejected");

    assert!(err.contains("Zip entry has unsafe path"));
    assert!(!root.join("evil.txt").exists());

    let _ = fs::remove_dir_all(root);
}

#[test]
fn zip_entry_paths_reject_windows_drive_relative_names() {
    assert!(zip_entry_relative_path("C:evil.txt").is_err());
    assert!(zip_entry_relative_path("C:/evil.txt").is_err());
    assert!(zip_entry_relative_path("folder/foo:bar.txt").is_err());
    assert!(zip_entry_relative_path("folder/CON.txt").is_err());
    assert!(zip_entry_relative_path("folder/bad?.txt").is_err());
    assert!(zip_entry_relative_path("folder\0evil.txt").is_err());
}

#[test]
fn extract_path_must_remain_within_destination() {
    let dest = PathBuf::from(r"C:\Users\demo\extract-root");
    ensure_extract_path_within_destination(&dest, &dest.join("safe.txt"))
        .expect("nested safe path");
    ensure_extract_path_within_destination(&dest, &dest.join("folder").join("nested.bin"))
        .expect("deep nested safe path");
    ensure_extract_path_within_destination(&dest, &dest).expect("destination itself");

    let sibling_escape = PathBuf::from(r"C:\Users\demo\extract-root-evil\file.txt");
    assert!(
        ensure_extract_path_within_destination(&dest, &sibling_escape).is_err(),
        "prefix sibling must not count as inside dest"
    );

    let parent_escape = PathBuf::from(r"C:\Users\demo\outside.txt");
    assert!(
        ensure_extract_path_within_destination(&dest, &parent_escape).is_err(),
        "parent path must be rejected"
    );
}

#[test]
fn tar_and_rar_entry_paths_reject_windows_special_names() {
    for archive_type in ["Tar", "RAR"] {
        assert!(archive_entry_relative_path(Path::new("C:evil.txt"), archive_type).is_err());
        assert!(
            archive_entry_relative_path(Path::new("folder/foo:bar.txt"), archive_type).is_err()
        );
        assert!(archive_entry_relative_path(Path::new("folder/CON.txt"), archive_type).is_err());
        assert!(archive_entry_relative_path(Path::new("folder/bad?.txt"), archive_type).is_err());
        assert!(archive_entry_relative_path(Path::new("/absolute.txt"), archive_type).is_err());
        assert!(archive_entry_relative_path(Path::new("../escape.txt"), archive_type).is_err());
        assert!(archive_entry_relative_path(Path::new("."), archive_type).is_err());
    }

    assert_eq!(
        archive_entry_relative_path(Path::new("folder/safe.txt"), "Tar").unwrap(),
        PathBuf::from("folder").join("safe.txt")
    );
}

#[test]
fn extract_tar_rejects_ads_like_entry_names() {
    let root = unique_temp_dir("tar-ads");
    let dest = root.join("out");
    fs::create_dir_all(&dest).expect("create destination");
    let tar_path = root.join("evil.tar");
    write_test_tar(&tar_path, &[("foo:bar.txt", b"bad")]);

    let err = extract_tar(tar_path.to_str().unwrap(), &dest, None)
        .expect_err("ADS-like tar path should be rejected");

    assert!(err.contains("Tar entry has unsafe path"));
    assert!(!dest.join("foo:bar.txt").exists());

    let _ = fs::remove_dir_all(root);
}

#[test]
fn virtual_archive_listing_skips_unsafe_entries() {
    let root = unique_temp_dir("virtual-listing-unsafe");
    let zip_path = root.join("sample.zip");
    write_test_zip(
        &zip_path,
        &[("safe.txt", b"safe"), ("folder/foo:bar.txt", b"bad")],
    );

    let root_listing = list_archive_directory(zip_path.to_str().unwrap())
        .expect("list archive root")
        .expect("archive root listing");

    assert_eq!(root_listing.entries.len(), 1);
    assert_eq!(root_listing.entries[0].name, "safe.txt");

    let archive_info =
        list_archive(zip_path.to_str().unwrap().to_string()).expect("list archive info");
    assert_eq!(archive_info.entries.len(), 1);
    assert_eq!(archive_info.entries[0].path, "safe.txt");
    assert_eq!(archive_info.unsafe_entries, vec!["folder/foo:bar.txt"]);

    let _ = fs::remove_dir_all(root);
}

#[test]
fn extract_zip_renames_colliding_top_level_folder() {
    let root = unique_temp_dir("collision");
    let dest = root.join("out");
    fs::create_dir_all(dest.join("SimpleFile")).expect("create existing top folder");
    fs::write(dest.join("SimpleFile").join("existing.txt"), b"original")
        .expect("write existing file");

    let zip_path = root.join("SimpleFile.zip");
    write_test_zip(
        &zip_path,
        &[
            ("SimpleFile/existing.txt", b"from-archive"),
            ("SimpleFile/nested/new.txt", b"nested"),
        ],
    );

    extract_zip(zip_path.to_str().unwrap(), &dest)
        .expect("colliding top-level folder should keep both");

    assert_eq!(
        fs::read(dest.join("SimpleFile").join("existing.txt")).expect("read original"),
        b"original"
    );
    assert_eq!(
        fs::read(dest.join("SimpleFile (1)").join("existing.txt"))
            .expect("read renamed extraction"),
        b"from-archive"
    );
    assert_eq!(
        fs::read(dest.join("SimpleFile (1)").join("nested").join("new.txt"))
            .expect("read nested renamed extraction"),
        b"nested"
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn list_archive_directory_projects_zip_as_folders() {
    let root = unique_temp_dir("virtual-listing");
    let zip_path = root.join("sample.zip");
    write_test_zip(
        &zip_path,
        &[
            ("folder/a.txt", b"a"),
            ("folder/nested/b.txt", b"b"),
            ("root.txt", b"root"),
        ],
    );

    let root_listing = list_archive_directory(zip_path.to_str().unwrap())
        .expect("list archive root")
        .expect("archive root listing");
    assert_eq!(root_listing.entries.len(), 2);
    assert!(root_listing
        .entries
        .iter()
        .any(|entry| entry.name == "folder" && entry.is_dir));
    assert!(root_listing
        .entries
        .iter()
        .any(|entry| entry.name == "root.txt" && !entry.is_dir));

    let nested_path = build_virtual_archive_path(&zip_path, Path::new("folder"));
    let nested_listing = list_archive_directory(&nested_path)
        .expect("list nested archive folder")
        .expect("nested listing");
    assert!(nested_listing
        .entries
        .iter()
        .any(|entry| entry.name == "a.txt" && !entry.is_dir));
    assert!(nested_listing
        .entries
        .iter()
        .any(|entry| entry.name == "nested" && entry.is_dir));

    let _ = fs::remove_dir_all(root);
}

#[test]
fn copy_local_file_into_zip_archive_root() {
    let root = unique_temp_dir("copy-into-zip");
    let zip_path = root.join("sample.zip");
    let source = root.join("source.txt");
    fs::write(&source, b"from-local").expect("write source");
    write_test_zip(&zip_path, &[("existing.txt", b"existing")]);

    let result = copy_entry_resolved(
        source.to_string_lossy().to_string(),
        zip_path.to_string_lossy().to_string(),
        "error".to_string(),
    )
    .expect("copy into zip");

    assert_eq!(
        result,
        build_virtual_archive_path(&zip_path, Path::new("source.txt"))
    );

    let out = root.join("out");
    fs::create_dir_all(&out).expect("create out dir");
    extract_archive_to_directory(&zip_path, &out).expect("extract updated zip");
    assert_eq!(
        fs::read(out.join("source.txt")).expect("read copied entry"),
        b"from-local"
    );
    assert_eq!(
        fs::read(out.join("existing.txt")).expect("read existing entry"),
        b"existing"
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn copy_zip_archive_entry_out_to_local_folder() {
    let root = unique_temp_dir("copy-out-of-zip");
    let zip_path = root.join("sample.zip");
    write_test_zip(&zip_path, &[("folder/source.txt", b"from-archive")]);
    let out = root.join("out");
    fs::create_dir_all(&out).expect("create out dir");

    let source =
        build_virtual_archive_path(&zip_path, Path::new("folder").join("source.txt").as_path());
    let result = copy_entry_resolved(
        source,
        out.to_string_lossy().to_string(),
        "error".to_string(),
    )
    .expect("copy out of zip");

    assert_eq!(PathBuf::from(result), out.join("source.txt"));
    assert_eq!(
        fs::read(out.join("source.txt")).expect("read copied file"),
        b"from-archive"
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn move_local_file_into_zip_skip_keeps_source() {
    let root = unique_temp_dir("move-skip-keeps-source");
    let zip_path = root.join("sample.zip");
    let source = root.join("source.txt");
    fs::write(&source, b"from-local").expect("write source");
    write_test_zip(&zip_path, &[("source.txt", b"existing")]);

    let result = move_entry_resolved(
        source.to_string_lossy().to_string(),
        zip_path.to_string_lossy().to_string(),
        "skip".to_string(),
    )
    .expect("skip move into zip should succeed as skipped");

    assert!(result.starts_with("SKIPPED:"));
    assert_eq!(
        fs::read(&source).expect("source should remain after skipped move"),
        b"from-local"
    );

    let out = root.join("out");
    fs::create_dir_all(&out).expect("create out dir");
    extract_archive_to_directory(&zip_path, &out).expect("extract unchanged zip");
    assert_eq!(
        fs::read(out.join("source.txt")).expect("read existing archive entry"),
        b"existing"
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn path_is_within_prefix_matches_exact_and_nested() {
    let prefix = PathBuf::from("folder").join("item.txt");
    assert!(path_is_within_prefix(&prefix, &prefix));
    assert!(path_is_within_prefix(
        &PathBuf::from("folder").join("nested").join("a.txt"),
        Path::new("folder")
    ));
    assert!(!path_is_within_prefix(
        Path::new("folder2").join("a.txt").as_path(),
        Path::new("folder")
    ));
    assert!(!path_is_within_prefix(
        Path::new("fold"),
        Path::new("folder")
    ));
}

#[test]
fn materialize_archive_entry_cleans_temp_on_drop() {
    let root = unique_temp_dir("materialize-cleanup");
    let zip_path = root.join("sample.zip");
    write_test_zip(
        &zip_path,
        &[("keep.txt", b"keep"), ("folder/other.txt", b"other")],
    );

    let virtual_path = build_virtual_archive_path(&zip_path, Path::new("keep.txt"));
    let materialized = materialize_archive_entry_to_temp(&virtual_path).expect("materialize");
    let cleanup_root = materialized
        .cleanup_root()
        .expect("archive materialize should own a work root")
        .to_path_buf();
    assert!(
        cleanup_root.exists(),
        "work root should exist while guard is alive"
    );
    assert!(
        cleanup_root
            .file_name()
            .and_then(|n| n.to_str())
            .is_some_and(|n| n.starts_with("archive-open-")),
        "unexpected work dir name: {}",
        cleanup_root.display()
    );
    assert_eq!(
        fs::read(materialized.path()).expect("read materialized"),
        b"keep"
    );
    // Sibling entries must not be extracted for open/materialize.
    assert!(!cleanup_root.join("folder").join("other.txt").exists());

    drop(materialized);
    assert!(
        !cleanup_root.exists(),
        "archive-open work root must be removed on Drop"
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn materialize_handoff_keeps_temp_until_caller_cleans() {
    let root = unique_temp_dir("materialize-handoff");
    let zip_path = root.join("sample.zip");
    write_test_zip(&zip_path, &[("keep.txt", b"keep")]);
    let virtual_path = build_virtual_archive_path(&zip_path, Path::new("keep.txt"));
    let materialized = materialize_archive_entry_to_temp(&virtual_path).expect("materialize");
    let (launch_path, cleanup_root) = materialized.into_open_with_handoff();
    let cleanup_root = cleanup_root.expect("handoff should return work root");
    assert!(
        launch_path.exists(),
        "launch path must remain after handoff"
    );
    assert!(cleanup_root.exists(), "work root must remain after handoff");
    assert_eq!(fs::read(&launch_path).expect("read"), b"keep");
    let _ = fs::remove_dir_all(&cleanup_root);
    assert!(!cleanup_root.exists());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn materialize_archive_entry_rejects_oversized_extract() {
    let root = unique_temp_dir("materialize-oversize");
    let zip_path = root.join("sample.zip");
    write_test_zip(&zip_path, &[("big.txt", b"0123456789abcdef")]);
    let virtual_path = build_virtual_archive_path(&zip_path, Path::new("big.txt"));

    let err = materialize_archive_entry_to_temp_with_limits(
        &virtual_path,
        ExtractLimits {
            max_uncompressed_bytes: 8,
            max_entries: 4_096,
        },
    )
    .expect_err("oversized extract should fail");
    assert!(err.contains("size limit"), "unexpected error: {err}");

    // Failure path deletes the work root before returning (see materialize match arm).
    // Full residue coverage for the success path is in
    // materialize_archive_entry_cleans_temp_on_drop.

    let _ = fs::remove_dir_all(root);
}

#[test]
fn extract_archive_entry_rejects_too_many_entries() {
    let root = unique_temp_dir("materialize-entry-cap");
    let zip_path = root.join("sample.zip");
    write_test_zip(
        &zip_path,
        &[
            ("dir/a.txt", b"a"),
            ("dir/b.txt", b"b"),
            ("dir/c.txt", b"c"),
        ],
    );
    let dest = root.join("out");
    fs::create_dir_all(&dest).expect("dest");

    let err = extract_archive_entry_to_directory(
        &zip_path,
        &dest,
        Path::new("dir"),
        ExtractLimits {
            max_uncompressed_bytes: 512 * 1024 * 1024,
            max_entries: 2,
        },
    )
    .expect_err("entry cap should fail");
    assert!(err.contains("entry limit"), "unexpected error: {err}");

    let _ = fs::remove_dir_all(root);
}

#[test]
fn copy_archive_entry_to_local_selective_extract_and_cleanup() {
    let root = unique_temp_dir("copy-out-selective");
    let zip_path = root.join("sample.zip");
    // Sibling is larger than the limit; selective extract of keep.txt must still succeed.
    write_test_zip(
        &zip_path,
        &[
            ("keep.txt", b"keep"),
            ("folder/other.txt", b"0123456789abcdef"),
        ],
    );
    let out = root.join("out");
    fs::create_dir_all(&out).expect("create out dir");

    let parsed = split_archive_path(&build_virtual_archive_path(
        &zip_path,
        Path::new("keep.txt"),
    ))
    .expect("split")
    .expect("archive path");

    let result = copy_archive_entry_to_local_with_limits(
        &parsed,
        &out.to_string_lossy(),
        "error",
        ExtractLimits {
            max_uncompressed_bytes: 8,
            max_entries: 4_096,
        },
    )
    .expect("selective transfer extract should only budget the selected entry");

    assert_eq!(PathBuf::from(&result), out.join("keep.txt"));
    assert_eq!(fs::read(out.join("keep.txt")).expect("read"), b"keep");

    let _ = fs::remove_dir_all(root);
}

#[test]
fn copy_archive_entry_to_local_rejects_oversized_and_cleans_temp() {
    let root = unique_temp_dir("copy-out-oversize");
    let zip_path = root.join("sample.zip");
    write_test_zip(&zip_path, &[("big.txt", b"0123456789abcdef")]);
    let out = root.join("out");
    fs::create_dir_all(&out).expect("create out dir");

    let parsed = split_archive_path(&build_virtual_archive_path(&zip_path, Path::new("big.txt")))
        .expect("split")
        .expect("archive path");

    let err = copy_archive_entry_to_local_with_limits(
        &parsed,
        &out.to_string_lossy(),
        "error",
        ExtractLimits {
            max_uncompressed_bytes: 8,
            max_entries: 4_096,
        },
    )
    .expect_err("oversized transfer extract should fail");
    assert!(err.contains("size limit"), "unexpected error: {err}");
    assert!(
        !out.join("big.txt").exists(),
        "oversize reject must not write dest"
    );

    // WorkRootGuard drops on Err (same RAII as open/preview MaterializedSource).

    let _ = fs::remove_dir_all(root);
}
#[test]
fn copy_archive_entry_into_archive_uses_selective_materialize() {
    let root = unique_temp_dir("copy-into-selective");
    let src_zip = root.join("src.zip");
    let dst_zip = root.join("dst.zip");
    write_test_zip(
        &src_zip,
        &[("keep.txt", b"keep"), ("folder/other.txt", b"other")],
    );
    write_test_zip(&dst_zip, &[("existing.txt", b"existing")]);

    let source = build_virtual_archive_path(&src_zip, Path::new("keep.txt"));
    let result = copy_entry_resolved(
        source,
        dst_zip.to_string_lossy().to_string(),
        "error".to_string(),
    )
    .expect("copy archive entry into archive");

    assert_eq!(
        result,
        build_virtual_archive_path(&dst_zip, Path::new("keep.txt"))
    );

    let out = root.join("out");
    fs::create_dir_all(&out).expect("out");
    extract_archive_to_directory(&dst_zip, &out).expect("extract dst");
    assert_eq!(fs::read(out.join("keep.txt")).expect("keep"), b"keep");
    assert_eq!(
        fs::read(out.join("existing.txt")).expect("existing"),
        b"existing"
    );

    let _ = fs::remove_dir_all(root);
}

/// Records what the archive layer forwarded to a registered engine.
#[derive(Default)]
struct MockEngine {
    entries: Vec<EngineEntry>,
    calls: std::sync::Mutex<Vec<String>>,
}

impl ArchiveEngine for MockEngine {
    fn name(&self) -> &str {
        "mock-engine"
    }

    fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String> {
        self.calls
            .lock()
            .unwrap()
            .push(format!("list {}", archive.display()));
        Ok(self.entries.clone())
    }

    fn extract(
        &self,
        archive: &Path,
        destination: &Path,
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.calls.lock().unwrap().push(format!(
            "extract {} -> {}",
            archive.display(),
            destination.display()
        ));
        Ok(())
    }

    fn read_entry(
        &self,
        archive: &Path,
        entry: &str,
        destination: &Path,
        limits: EngineLimits,
    ) -> Result<(), String> {
        self.calls.lock().unwrap().push(format!(
            "read_entry {} {entry} -> {} ({})",
            archive.display(),
            destination.display(),
            limits.max_entries
        ));
        Ok(())
    }

    fn create(
        &self,
        archive: &Path,
        paths: &[PathBuf],
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.calls.lock().unwrap().push(format!(
            "create {} from {}",
            archive.display(),
            paths.len()
        ));
        fs::write(archive, b"mock").map_err(|e| e.to_string())
    }

    fn rename(
        &self,
        _archive: &Path,
        entry: &str,
        new_path: &str,
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.calls
            .lock()
            .unwrap()
            .push(format!("rename {entry} {new_path}"));
        Ok(())
    }

    fn delete(
        &self,
        _archive: &Path,
        entries: &[String],
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.calls
            .lock()
            .unwrap()
            .push(format!("delete {}", entries.join(",")));
        Ok(())
    }

    fn add(
        &self,
        _archive: &Path,
        _base_dir: &Path,
        paths: &[String],
        _ctx: &EngineContext<'_>,
    ) -> Result<(), String> {
        self.calls
            .lock()
            .unwrap()
            .push(format!("add {}", paths.join(",")));
        Ok(())
    }
}

/// The engine registry is process-wide, so every test registers under its
/// own owner and extension and removes them again on the way out.
struct EngineRegistration(&'static str);

impl Drop for EngineRegistration {
    fn drop(&mut self) {
        unregister_engine(self.0);
    }
}

fn engine_format(extension: &str) -> EngineFormat {
    EngineFormat {
        extension: extension.to_string(),
        can_list: true,
        can_extract: true,
        can_create: true,
        can_modify: false,
    }
}

fn register_mock(
    owner: &'static str,
    extensions: &[&str],
    engine: std::sync::Arc<MockEngine>,
) -> EngineRegistration {
    register_engine(
        owner,
        extensions.iter().map(|ext| engine_format(ext)).collect(),
        engine,
    );
    EngineRegistration(owner)
}

#[test]
fn engine_registry_resolves_by_extension_case_insensitively() {
    let _short = register_mock(
        "com.test.resolve",
        &[".EngResolve"],
        std::sync::Arc::new(MockEngine::default()),
    );
    let _long = register_mock(
        "com.test.resolve.long",
        &[".tar.engresolve", ".ZIP", ".tar.gz"],
        std::sync::Arc::new(MockEngine::default()),
    );

    let (_, format) =
        engine_for_path(Path::new(r"C:\data\Backup.ENGRESOLVE")).expect("engine for upper case");
    assert_eq!(format.extension, ".engresolve");
    let (_, format) =
        engine_for_path(Path::new(r"C:\data\backup.Tar.EngResolve")).expect("longest extension");
    assert_eq!(format.extension, ".tar.engresolve");
    assert!(engine_for_path(Path::new(r"C:\data\backup.engresolvex")).is_none());
    // Built-in formats never fall through to an engine.
    assert!(engine_for_path(Path::new(r"C:\data\backup.zip")).is_none());

    let owners: Vec<_> = registered_formats()
        .into_iter()
        .filter(|(owner, _, _)| owner.starts_with("com.test.resolve"))
        .map(|(owner, format, name)| (owner, format.extension, name))
        .collect();
    assert!(
        owners
            .iter()
            .all(|(_, extension, _)| extension != ".zip" && extension != ".tar.gz"),
        "built-in extensions are never registered: {owners:?}"
    );
    assert!(owners.contains(&(
        "com.test.resolve".to_string(),
        ".engresolve".to_string(),
        "mock-engine".to_string()
    )));
}

#[test]
fn engine_list_archive_uses_registered_engine() {
    let engine = std::sync::Arc::new(MockEngine {
        entries: vec![
            EngineEntry {
                path: "docs/readme.txt".to_string(),
                is_dir: false,
                size: 12,
                compressed_size: 7,
            },
            EngineEntry {
                path: "../evil".to_string(),
                is_dir: false,
                size: 1,
                compressed_size: 1,
            },
        ],
        ..MockEngine::default()
    });
    let _registration = register_mock("com.test.list", &[".englist"], engine.clone());
    let root = unique_temp_dir("engine-list");
    let archive = root.join("sample.englist");
    fs::write(&archive, b"fake").expect("write fake archive");

    let info = list_archive(archive.to_string_lossy().into_owned()).expect("list via engine");

    assert_eq!(info.format, "englist");
    assert_eq!(info.entries.len(), 1);
    assert_eq!(info.entries[0].path, "docs/readme.txt");
    assert_eq!(info.entries[0].name, "readme.txt");
    assert_eq!(info.entries[0].size, 12);
    assert_eq!(info.entries[0].compressed_size, 7);
    assert_eq!(info.unsafe_entries, vec!["../evil".to_string()]);
    assert_eq!(info.total_size, 12);
    assert_eq!(engine.calls.lock().unwrap().len(), 1);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_extract_and_create_forward_to_engine() {
    let engine = std::sync::Arc::new(MockEngine::default());
    let _registration = register_mock("com.test.forward", &[".engfwd"], engine.clone());
    let root = unique_temp_dir("engine-forward");
    let archive = root.join("sample.engfwd");
    fs::write(&archive, b"fake").expect("write fake archive");
    let destination = root.join("out");
    let source = root.join("source.txt");
    fs::write(&source, b"content").expect("write source");

    extract_archive(
        archive.to_string_lossy().into_owned(),
        destination.to_string_lossy().into_owned(),
    )
    .expect("extract via engine");
    assert!(destination.is_dir(), "destination is created first");

    let created = root.join("created.engfwd");
    create_archive(
        vec![source.to_string_lossy().into_owned()],
        created.to_string_lossy().into_owned(),
        "engfwd".to_string(),
    )
    .expect("create via engine");

    let calls = engine.calls.lock().unwrap().clone();
    assert_eq!(calls.len(), 3, "{calls:?}");
    assert_eq!(calls[0], format!("list {}", archive.display()));
    // The engine extracts into a private staging folder inside the
    // destination, which is gone again once the extract returns.
    let staging_prefix = format!(
        "extract {} -> {}{}.sumafile-extract-",
        archive.display(),
        destination.canonicalize().unwrap().display(),
        std::path::MAIN_SEPARATOR
    );
    assert!(calls[1].starts_with(&staging_prefix), "{calls:?}");
    assert_eq!(fs::read_dir(&destination).unwrap().count(), 0);
    // The engine creates a temp file beside the target, which is then
    // renamed into place.
    let temp_prefix = format!(
        "create {}{}.created.simplefile-new-",
        root.display(),
        std::path::MAIN_SEPARATOR
    );
    assert!(calls[2].starts_with(&temp_prefix), "{calls:?}");
    assert!(calls[2].ends_with(".engfwd from 1"), "{calls:?}");
    assert_eq!(fs::read(&created).unwrap(), b"mock");
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_capabilities_include_registered_formats() {
    let _registration = register_mock(
        "com.test",
        &[".engcaps"],
        std::sync::Arc::new(MockEngine::default()),
    );

    let capabilities = get_archive_capabilities();

    let format = capabilities
        .formats
        .iter()
        .find(|format| format.extension == ".engcaps")
        .expect("registered format is listed");
    assert_eq!(format.format, "engcaps");
    assert_eq!(format.engine, "plugin:com.test");
    assert!(format.can_list && format.can_extract && format.can_create);
    assert!(!format.can_modify);
    assert!(format.note.is_none());
    assert!(capabilities
        .formats
        .iter()
        .any(|format| format.extension == ".zip" && format.engine == "built-in"));
}

#[test]
fn engine_unregister_removes_formats() {
    let registration = register_mock(
        "com.test.unregister",
        &[".engunreg"],
        std::sync::Arc::new(MockEngine::default()),
    );
    assert!(engine_for_path(Path::new("a.engunreg")).is_some());

    drop(registration);

    assert!(engine_for_path(Path::new("a.engunreg")).is_none());
    assert!(registered_formats()
        .iter()
        .all(|(owner, _, _)| owner != "com.test.unregister"));
    assert!(!get_archive_capabilities()
        .formats
        .iter()
        .any(|format| format.extension == ".engunreg"));
}

#[test]
fn engine_is_archive_path_true_for_registered_extension() {
    let root = unique_temp_dir("engine-is-archive");
    let archive = root.join("sample.engisarc");
    fs::write(&archive, b"fake").expect("write fake archive");
    let archive_string = archive.to_string_lossy().into_owned();
    assert!(!is_archive_path(&archive_string));

    let _registration = register_mock(
        "com.test.is-archive",
        &[".engisarc"],
        std::sync::Arc::new(MockEngine::default()),
    );

    assert!(is_archive_path(&archive_string));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_replace_with_no_registrable_formats_keeps_existing() {
    let _registration = register_mock(
        "com.test.replace",
        &[".engreplace"],
        std::sync::Arc::new(MockEngine::default()),
    );
    let thief: std::sync::Arc<dyn ArchiveEngine> = std::sync::Arc::new(MockEngine::default());

    assert!(!replace_engines(
        "com.test.replace",
        vec![(
            vec![engine_format(".zip"), engine_format("")],
            thief.clone()
        )],
    ));
    assert!(engine_for_path(Path::new("a.engreplace")).is_some());
    assert!(!is_registrable_extension(".TGZ"));
    assert!(is_registrable_extension("engreplace2"));

    assert!(replace_engines(
        "com.test.replace",
        vec![(vec![engine_format(".engreplace2")], thief)],
    ));
    assert!(engine_for_path(Path::new("a.engreplace")).is_none());
    assert!(engine_for_path(Path::new("a.engreplace2")).is_some());
}

fn entry_names(dir: &Path) -> Vec<String> {
    let mut names: Vec<String> = fs::read_dir(dir)
        .map(|entries| {
            entries
                .filter_map(Result::ok)
                .map(|entry| entry.file_name().to_string_lossy().into_owned())
                .collect()
        })
        .unwrap_or_default();
    names.sort();
    names
}

fn no_staging_left(dir: &Path) -> bool {
    entry_names(dir)
        .iter()
        .all(|name| !name.starts_with(".sumafile-extract-"))
}

fn cancelled_context(flag: &std::sync::atomic::AtomicBool) -> EngineContext<'_> {
    EngineContext {
        operation_id: Some("op-cancelled"),
        cancel: flag,
        progress: &|_| {},
    }
}

#[test]
fn engine_split_archive_path_parses_paths_inside_engine_archives() {
    let (_engine, _registration) = register_json_engine("com.test.engsplit", ".engsplit", false);
    let root = unique_temp_dir("engine-split");
    let archive = root.join("x.engsplit");
    write_json_archive(&archive, &[("docs/a.txt", false, "text")]);
    let inside = archive.join("docs").join("a.txt");
    let inside = inside.to_string_lossy();

    let parsed = split_archive_path(&inside)
        .expect("split")
        .expect("inside an engine archive");

    assert_eq!(parsed.archive_path, archive);
    assert_eq!(parsed.inner_path, PathBuf::from("docs\\a.txt"));
    assert!(
        matches!(&parsed.kind, ArchiveKind::Engine { format, .. } if format.extension == ".engsplit"),
        "{:?}",
        parsed.kind
    );
    assert_eq!(parsed.kind.entry_type(), "json-engine");
    assert_eq!(parsed.kind.label(), "engsplit");
    assert!(is_archive_virtual_path(&inside));
    assert!(is_archive_path(&inside));
    assert!(is_archive_path(&archive.to_string_lossy()));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_list_archive_directory_lists_root_and_nested_folders() {
    let (_engine, _registration) =
        register_json_engine("com.test.englistdir", ".englistdir", false);
    let root = unique_temp_dir("engine-list-dir");
    let archive = root.join("x.englistdir");
    write_json_archive(
        &archive,
        &[("docs/a.txt", false, "text"), ("top.txt", false, "top")],
    );
    let archive_string = archive.to_string_lossy().into_owned();

    let listing = list_archive_directory(&archive_string)
        .expect("list root")
        .expect("engine archive root");
    let names: Vec<_> = listing
        .entries
        .iter()
        .map(|entry| (entry.name.as_str(), entry.is_dir))
        .collect();
    assert_eq!(names, vec![("docs", true), ("top.txt", false)]);
    assert_eq!(listing.entries[1].size, 3);

    let docs = archive.join("docs").to_string_lossy().into_owned();
    let nested = list_archive_directory(&docs)
        .expect("list docs")
        .expect("engine archive folder");
    assert_eq!(nested.entries.len(), 1);
    assert_eq!(nested.entries[0].name, "a.txt");
    assert_eq!(
        nested.entries[0].path,
        archive.join("docs").join("a.txt").to_string_lossy()
    );
    assert_eq!(nested.parent.as_deref(), Some(archive_string.as_str()));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_full_extract_moves_only_listed_entries() {
    let (_engine, _registration) = register_json_engine_with(
        "com.test.engstray",
        ".engstray",
        false,
        JsonEngine {
            stray_file: true,
            ..JsonEngine::default()
        },
    );
    let root = unique_temp_dir("engine-stray");
    let archive = root.join("x.engstray");
    write_json_archive(&archive, &[("docs/a.txt", false, "text")]);
    let destination = root.join("out");

    extract_archive(
        archive.to_string_lossy().into_owned(),
        destination.to_string_lossy().into_owned(),
    )
    .expect("extract via engine");

    assert_eq!(
        fs::read_to_string(destination.join("docs").join("a.txt")).unwrap(),
        "text"
    );
    assert!(!destination.join("stray.txt").exists());
    assert_eq!(entry_names(&destination), vec!["docs".to_string()]);
    assert!(no_staging_left(&destination));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_full_extract_refuses_unsafe_entry_names_before_writing() {
    let (engine, _registration) = register_json_engine("com.test.engunsafe", ".engunsafe", false);
    let root = unique_temp_dir("engine-unsafe");
    let archive = root.join("x.engunsafe");
    write_json_archive(
        &archive,
        &[
            ("docs/a.txt", false, "text"),
            ("../evil.txt", false, "evil"),
        ],
    );
    let destination = root.join("out");

    let error = extract_archive(
        archive.to_string_lossy().into_owned(),
        destination.to_string_lossy().into_owned(),
    )
    .expect_err("unsafe names are refused");

    assert!(error.contains("unsafe path"), "{error}");
    assert!(entry_names(&destination).is_empty());
    assert!(!root.join("evil.txt").exists());
    assert!(
        !engine.calls().iter().any(|call| call == "extract"),
        "{:?}",
        engine.calls()
    );
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_full_extract_renames_an_existing_top_level_folder() {
    let (_engine, _registration) = register_json_engine("com.test.engremap", ".engremap", false);
    let root = unique_temp_dir("engine-remap");
    let archive = root.join("x.engremap");
    write_json_archive(
        &archive,
        &[("docs", true, ""), ("docs/a.txt", false, "text")],
    );
    let destination = root.join("out");
    fs::create_dir_all(destination.join("docs")).unwrap();
    fs::write(destination.join("docs").join("mine.txt"), b"mine").unwrap();

    extract_archive(
        archive.to_string_lossy().into_owned(),
        destination.to_string_lossy().into_owned(),
    )
    .expect("extract via engine");

    assert_eq!(
        fs::read_to_string(destination.join("docs (1)").join("a.txt")).unwrap(),
        "text"
    );
    assert_eq!(
        entry_names(&destination.join("docs")),
        vec!["mine.txt".to_string()]
    );
    assert!(no_staging_left(&destination));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_full_extract_refuses_links_left_in_staging() {
    let (_engine, _registration) = register_json_engine_with(
        "com.test.englink",
        ".englink",
        false,
        JsonEngine {
            junction_dir: Some("docs"),
            ..JsonEngine::default()
        },
    );
    let root = unique_temp_dir("engine-link");
    let archive = root.join("x.englink");
    write_json_archive(&archive, &[("docs/a.txt", false, "text")]);
    let destination = root.join("out");

    let error = extract_archive(
        archive.to_string_lossy().into_owned(),
        destination.to_string_lossy().into_owned(),
    )
    .expect_err("a link left in staging is refused");

    assert_eq!(error, "Archive entry is a link: docs");
    assert!(
        entry_names(&destination).is_empty(),
        "{:?}",
        entry_names(&destination)
    );
    // The engine wrote docs/a.txt through the junction. Removing the
    // staging removed the junction, not what it points to.
    assert_eq!(
        fs::read_to_string(root.join("json-engine-junction-target").join("a.txt")).unwrap(),
        "text"
    );
    let _ = fs::remove_dir_all(root);
}

/// Gives `path` a reparse point whose tag is not a name surrogate, the kind
/// a cloud-file placeholder (OneDrive Files On-Demand) or a deduplicated or
/// WOF-compressed file carries. The tag is a third-party one that no filter
/// serves, so afterwards only metadata that does not follow reparse points
/// can be read. A folder must be empty.
#[cfg(windows)]
fn make_reparse_point_that_is_not_a_link(path: &Path) {
    use std::os::windows::fs::{MetadataExt, OpenOptionsExt};
    use std::os::windows::io::AsRawHandle;

    const FILE_FLAG_OPEN_REPARSE_POINT: u32 = 0x0020_0000;
    const FILE_FLAG_BACKUP_SEMANTICS: u32 = 0x0200_0000;
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
    const FSCTL_SET_REPARSE_POINT: u32 = 0x0009_00A4;
    // Neither the Microsoft bit (0x8000_0000) nor the name-surrogate bit
    // (0x2000_0000).
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

    let file = fs::OpenOptions::new()
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
    let meta = fs::symlink_metadata(path).unwrap();
    assert_ne!(meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT, 0);
    assert!(!meta.file_type().is_symlink());
}

/// Only symlinks and junctions are links. A sync client can turn a staged
/// folder into a cloud-file placeholder, which is a reparse point too,
/// before core checks it.
#[cfg(windows)]
#[test]
fn staging_check_refuses_junctions_but_not_other_reparse_points() {
    let root = unique_temp_dir("staging-reparse");
    let staging = ArchiveStaging::create(&root).unwrap();
    let placeholder = staging.path().join("docs");
    fs::create_dir(&placeholder).unwrap();
    make_reparse_point_that_is_not_a_link(&placeholder);

    assert_eq!(staging.check_entry(Path::new("docs"), true), Ok(()));

    make_junction(&staging.path().join("linked"), &root.join("target")).unwrap();
    assert_eq!(
        staging.check_entry(Path::new("linked"), true),
        Err("Archive entry is a link: linked".to_string())
    );
    assert_eq!(
        staging.check_entry(&Path::new("linked").join("a.txt"), false),
        Err("Archive entry is a link: linked".to_string())
    );
    let staged = staging.path().to_path_buf();
    drop(staging);
    assert!(fs::symlink_metadata(&staged).is_err(), "staging was kept");
    let _ = fs::remove_dir_all(root);
}

#[test]
fn staging_drop_removes_a_junction_without_touching_its_target() {
    let root = unique_temp_dir("staging-drop");
    let target = root.join("target");
    fs::create_dir_all(target.join("inner")).unwrap();
    fs::write(target.join("keep.txt"), b"keep").unwrap();
    fs::write(target.join("inner").join("deep.txt"), b"deep").unwrap();
    let destination = root.join("out");
    fs::create_dir_all(&destination).unwrap();
    let staging = ArchiveStaging::create(&destination).unwrap();
    fs::create_dir_all(staging.path().join("docs")).unwrap();
    fs::write(staging.path().join("docs").join("a.txt"), b"a").unwrap();
    make_junction(&staging.path().join("linked"), &target).unwrap();
    make_junction(&staging.path().join("docs").join("nested"), &target).unwrap();

    drop(staging);

    assert!(
        entry_names(&destination).is_empty(),
        "{:?}",
        entry_names(&destination)
    );
    assert_eq!(fs::read(target.join("keep.txt")).unwrap(), b"keep");
    assert_eq!(
        fs::read(target.join("inner").join("deep.txt")).unwrap(),
        b"deep"
    );
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_read_entry_selects_by_segment_prefix() {
    let (engine, _registration) = register_json_engine("com.test.engread", ".engread", false);
    let root = unique_temp_dir("engine-read");
    let archive = root.join("x.engread");
    write_json_archive(
        &archive,
        &[
            ("docs/a.txt", false, "text"),
            ("docs2/b.txt", false, "other"),
        ],
    );

    let materialized = materialize_archive_entry_to_temp(&archive.join("docs").to_string_lossy())
        .expect("materialize docs");

    assert!(
        engine
            .calls()
            .contains(&"read_entry docs 536870912 4096".to_string()),
        "{:?}",
        engine.calls()
    );
    assert!(materialized.path().is_dir());
    assert_eq!(entry_names(materialized.path()), vec!["a.txt".to_string()]);
    assert_eq!(
        fs::read_to_string(materialized.path().join("a.txt")).unwrap(),
        "text"
    );
    let work_root = materialized
        .cleanup_root()
        .expect("work root")
        .to_path_buf();
    assert_eq!(entry_names(&work_root), vec!["docs".to_string()]);
    drop(materialized);
    assert!(!work_root.exists());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_read_entry_rejects_a_selection_over_limits_before_calling_the_engine() {
    let (engine, _registration) = register_json_engine("com.test.engbudget", ".engbudget", false);
    let root = unique_temp_dir("engine-budget");
    let archive = root.join("x.engbudget");
    write_json_archive(
        &archive,
        &[("docs/a.txt", false, "text"), ("docs/b.txt", false, "more")],
    );

    let error = materialize_archive_entry_to_temp_with_limits(
        &archive.join("docs").to_string_lossy(),
        ExtractLimits {
            max_uncompressed_bytes: 1024,
            max_entries: 1,
        },
    )
    .expect_err("two entries exceed a one-entry budget");

    assert_eq!(error, "Archive extract exceeds entry limit of 1 entries");
    assert!(
        !engine
            .calls()
            .iter()
            .any(|call| call.starts_with("read_entry")),
        "{:?}",
        engine.calls()
    );
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_read_entry_removes_output_over_the_byte_limit() {
    let (_engine, _registration) = register_json_engine_with(
        "com.test.engoversize",
        ".engoversize",
        false,
        JsonEngine {
            oversize_bytes: Some(64),
            ..JsonEngine::default()
        },
    );
    let root = unique_temp_dir("engine-oversize");
    let archive = root.join("x.engoversize");
    write_json_archive(&archive, &[("docs/a.txt", false, "text")]);
    let destination = root.join("out");
    fs::create_dir_all(&destination).unwrap();

    let error = extract_archive_entry_to_directory(
        &archive,
        &destination,
        Path::new("docs"),
        ExtractLimits {
            max_uncompressed_bytes: 10,
            max_entries: 4096,
        },
    )
    .expect_err("written bytes over the budget are refused");

    assert_eq!(error, "Archive extract exceeds size limit of 10 bytes");
    assert!(!destination.join("docs").join("a.txt").exists());
    assert!(no_staging_left(&destination));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_copy_entry_out_to_a_local_folder() {
    let (_engine, _registration) =
        register_json_engine("com.test.engcopyout", ".engcopyout", false);
    let root = unique_temp_dir("engine-copy-out");
    let archive = root.join("x.engcopyout");
    write_json_archive(&archive, &[("docs/a.txt", false, "text")]);
    let local = root.join("local");
    fs::create_dir_all(&local).unwrap();

    let copied = copy_entry_resolved(
        archive
            .join("docs")
            .join("a.txt")
            .to_string_lossy()
            .into_owned(),
        local.to_string_lossy().into_owned(),
        "error".to_string(),
    )
    .expect("copy out of an engine archive");

    assert_eq!(copied, local.join("a.txt").to_string_lossy());
    assert_eq!(fs::read_to_string(local.join("a.txt")).unwrap(), "text");
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_extract_with_a_cancelled_context_stops() {
    let (_engine, _registration) = register_json_engine("com.test.engcancel", ".engcancel", false);
    let root = unique_temp_dir("engine-cancel");
    let archive = root.join("x.engcancel");
    write_json_archive(&archive, &[("docs/a.txt", false, "text")]);
    let destination = root.join("out");
    let cancel = std::sync::atomic::AtomicBool::new(true);

    let error = extract_archive_with(
        archive.to_string_lossy().into_owned(),
        destination.to_string_lossy().into_owned(),
        &cancelled_context(&cancel),
    )
    .expect_err("a cancelled extract stops");

    assert_eq!(error, CANCELLED_MESSAGE);
    assert!(entry_names(&destination).is_empty());
    assert!(no_staging_left(&destination));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn built_in_extract_with_a_cancelled_context_does_nothing() {
    let root = unique_temp_dir("builtin-cancel");
    let archive = root.join("x.zip");
    write_test_zip(&archive, &[("docs/a.txt", b"text")]);
    let destination = root.join("out");
    let cancel = std::sync::atomic::AtomicBool::new(true);

    let error = extract_archive_with(
        archive.to_string_lossy().into_owned(),
        destination.to_string_lossy().into_owned(),
        &cancelled_context(&cancel),
    )
    .expect_err("a cancelled extract does nothing");

    assert_eq!(error, CANCELLED_MESSAGE);
    assert!(entry_names(&destination).is_empty());
    let _ = fs::remove_dir_all(root);
}

/// The calls that change an archive, in order; `list` and `read_entry`
/// are left out.
fn mutation_calls(engine: &JsonEngine) -> Vec<String> {
    engine
        .calls()
        .into_iter()
        .filter(|call| call != "list" && !call.starts_with("read_entry"))
        .collect()
}

fn json_paths(archive: &Path) -> Vec<String> {
    read_json_archive(archive)
        .into_iter()
        .map(|(path, _, _)| path)
        .collect()
}

fn json_data(archive: &Path, path: &str) -> Option<String> {
    read_json_archive(archive)
        .into_iter()
        .find(|(entry, _, _)| entry == path)
        .map(|(_, _, data)| data)
}

fn text(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

#[test]
fn engine_create_directory_and_file_add_staged_entries() {
    let (engine, _registration) = register_json_engine("com.test.engcreate", ".engcreate", true);
    let root = unique_temp_dir("engine-create-entry");
    let archive = root.join("x.engcreate");
    write_json_archive(
        &archive,
        &[("docs", true, ""), ("docs/a.txt", false, "text")],
    );
    let docs = text(&archive.join("docs"));

    let dir = create_archive_directory(docs.clone(), "new".into()).expect("new folder");
    let file = create_archive_file(docs.clone(), "n.txt".into()).expect("new file");

    assert_eq!(dir, text(&archive.join("docs").join("new")));
    assert_eq!(file, text(&archive.join("docs").join("n.txt")));
    assert_eq!(
        mutation_calls(&engine),
        vec!["add docs/new".to_string(), "add docs/n.txt".to_string()]
    );
    let stored = read_json_archive(&archive);
    assert!(
        stored.contains(&("docs/new".to_string(), true, String::new())),
        "{stored:?}"
    );
    assert!(
        stored.contains(&("docs/n.txt".to_string(), false, String::new())),
        "{stored:?}"
    );

    assert_eq!(
        create_archive_directory(docs.clone(), "new".into()).unwrap_err(),
        "Directory already exists: new"
    );
    assert_eq!(
        create_archive_file(docs, "n.txt".into()).unwrap_err(),
        "File already exists: n.txt"
    );
    assert_eq!(mutation_calls(&engine).len(), 2, "{:?}", engine.calls());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_rename_entry_moves_descendants_and_refuses_taken_names() {
    let (engine, _registration) = register_json_engine("com.test.engrename", ".engrename", true);
    let root = unique_temp_dir("engine-rename");
    let archive = root.join("x.engrename");
    write_json_archive(
        &archive,
        &[
            ("docs", true, ""),
            ("docs/a.txt", false, "text"),
            ("top.txt", false, "top"),
        ],
    );

    let renamed = rename_archive_entry(text(&archive.join("docs")), "papers".into())
        .expect("rename a folder");

    assert_eq!(renamed, text(&archive.join("papers")));
    assert_eq!(
        mutation_calls(&engine),
        vec!["rename docs papers".to_string()]
    );
    let paths = json_paths(&archive);
    assert!(paths.contains(&"papers/a.txt".to_string()), "{paths:?}");
    assert!(
        !paths.iter().any(|path| path.starts_with("docs")),
        "{paths:?}"
    );

    assert_eq!(
        rename_archive_entry(text(&archive.join("papers")), "top.txt".into()).unwrap_err(),
        "A file or directory with that name already exists: top.txt"
    );
    let same = rename_archive_entry(text(&archive.join("top.txt")), "top.txt".into())
        .expect("same name is a no-op");
    assert_eq!(same, text(&archive.join("top.txt")));
    assert_eq!(mutation_calls(&engine).len(), 1, "{:?}", engine.calls());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_delete_entry_calls_the_engine_once() {
    let (engine, _registration) = register_json_engine("com.test.engdelone", ".engdelone", true);
    let root = unique_temp_dir("engine-delete-one");
    let archive = root.join("x.engdelone");
    write_json_archive(
        &archive,
        &[
            ("docs", true, ""),
            ("docs/a.txt", false, "text"),
            ("top.txt", false, "top"),
        ],
    );

    delete_archive_entry(&text(&archive.join("docs"))).expect("delete a folder");

    assert_eq!(mutation_calls(&engine), vec!["delete docs".to_string()]);
    assert_eq!(json_paths(&archive), vec!["top.txt".to_string()]);
    let missing = archive.join("nope");
    assert_eq!(
        delete_archive_entry(&text(&missing)).unwrap_err(),
        format!("Archive entry not found: {}", missing.display())
    );
    assert_eq!(mutation_calls(&engine).len(), 1, "{:?}", engine.calls());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_multi_delete_issues_one_engine_call_per_archive() {
    let (engine, _registration) = register_json_engine("com.test.engdel", ".engdel", true);
    let root = unique_temp_dir("engine-multi-delete");
    let archive = root.join("x.engdel");
    write_json_archive(
        &archive,
        &[
            ("a.txt", false, "a"),
            ("b.txt", false, "b"),
            ("docs", true, ""),
            ("docs/c.txt", false, "c"),
            ("keep.txt", false, "keep"),
        ],
    );
    let paths = vec![
        text(&archive.join("a.txt")),
        text(&archive.join("b.txt")),
        text(&archive.join("docs")),
    ];

    let trashed = crate::file_ops::move_to_trash(&paths).expect("delete three entries");

    assert!(trashed.is_empty(), "{trashed:?}");
    assert_eq!(
        mutation_calls(&engine),
        vec!["delete a.txt,b.txt,docs".to_string()]
    );
    assert_eq!(json_paths(&archive), vec!["keep.txt".to_string()]);

    // A path missing from the fresh listing fails its whole group before
    // the engine is called.
    let missing = text(&archive.join("nope"));
    let errors = delete_archive_entries(&[text(&archive.join("keep.txt")), missing.clone()]);
    assert_eq!(errors, vec![format!("Archive entry not found: {missing}")]);
    assert_eq!(mutation_calls(&engine).len(), 1, "{:?}", engine.calls());
    assert_eq!(json_paths(&archive), vec!["keep.txt".to_string()]);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_copy_into_archive_resolves_conflicts() {
    let (engine, _registration) = register_json_engine("com.test.engcopyin", ".engcopyin", true);
    let root = unique_temp_dir("engine-copy-in");
    let archive = root.join("x.engcopyin");
    write_json_archive(
        &archive,
        &[("docs", true, ""), ("docs/a.txt", false, "old")],
    );
    let local = root.join("local");
    fs::create_dir_all(&local).unwrap();
    let source = local.join("a.txt");
    fs::write(&source, b"new").unwrap();
    let source = text(&source);
    let docs = text(&archive.join("docs"));

    let skipped = copy_entry_resolved(source.clone(), docs.clone(), "skip".into()).expect("skip");
    assert_eq!(skipped, format!("SKIPPED:{source}"));
    assert!(mutation_calls(&engine).is_empty(), "{:?}", engine.calls());

    let replaced =
        copy_entry_resolved(source.clone(), docs.clone(), "replace".into()).expect("replace");
    assert_eq!(replaced, text(&archive.join("docs").join("a.txt")));
    assert_eq!(
        mutation_calls(&engine),
        vec![
            "delete docs/a.txt".to_string(),
            "add docs/a.txt".to_string()
        ]
    );
    assert_eq!(json_data(&archive, "docs/a.txt").as_deref(), Some("new"));

    let renamed =
        copy_entry_resolved(source.clone(), docs.clone(), "rename".into()).expect("rename");
    assert_eq!(renamed, text(&archive.join("docs").join("a (1).txt")));
    assert_eq!(
        json_data(&archive, "docs/a (1).txt").as_deref(),
        Some("new")
    );

    let error = copy_entry_resolved(source, docs, "error".into()).unwrap_err();
    assert!(
        error.starts_with("CONFLICT: destination already exists: "),
        "{error}"
    );
    assert_eq!(
        error,
        format!(
            "CONFLICT: destination already exists: {}",
            archive.join("docs").join("a.txt").display()
        )
    );
    assert_eq!(mutation_calls(&engine).len(), 3, "{:?}", engine.calls());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_move_within_an_archive_renames_with_a_full_path() {
    let (engine, _registration) = register_json_engine("com.test.engmove", ".engmove", true);
    let root = unique_temp_dir("engine-move-within");
    let archive = root.join("x.engmove");
    write_json_archive(
        &archive,
        &[("docs", true, ""), ("docs/a.txt", false, "text")],
    );

    let moved = move_entry_resolved(
        text(&archive.join("docs").join("a.txt")),
        text(&archive.join("papers")),
        "error".into(),
    )
    .expect("move within the archive");

    assert_eq!(moved, text(&archive.join("papers").join("a.txt")));
    assert_eq!(
        mutation_calls(&engine),
        vec!["rename docs/a.txt papers/a.txt".to_string()]
    );
    assert_eq!(json_data(&archive, "papers/a.txt").as_deref(), Some("text"));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_move_into_an_archive_removes_the_local_source() {
    let (engine, _registration) = register_json_engine("com.test.engmovein", ".engmovein", true);
    let root = unique_temp_dir("engine-move-in");
    let archive = root.join("x.engmovein");
    write_json_archive(&archive, &[("docs", true, "")]);
    let local = root.join("n.txt");
    fs::write(&local, b"moved").unwrap();

    let moved = move_entry_resolved(text(&local), text(&archive.join("docs")), "error".into())
        .expect("move into the archive");

    assert_eq!(moved, text(&archive.join("docs").join("n.txt")));
    assert!(!local.exists());
    assert_eq!(mutation_calls(&engine), vec!["add docs/n.txt".to_string()]);
    assert_eq!(json_data(&archive, "docs/n.txt").as_deref(), Some("moved"));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_copy_between_archives_reads_then_adds() {
    let (engine, _registration) = register_json_engine("com.test.engjson", ".engjson", true);
    let root = unique_temp_dir("engine-between");
    let source_archive = root.join("x.engjson");
    let target_archive = root.join("y.engjson");
    write_json_archive(&source_archive, &[("docs/a.txt", false, "text")]);
    write_json_archive(&target_archive, &[("other.txt", false, "other")]);

    let copied = copy_entry_resolved(
        text(&source_archive.join("docs").join("a.txt")),
        text(&target_archive),
        "error".into(),
    )
    .expect("copy between archives");

    assert_eq!(copied, text(&target_archive.join("a.txt")));
    let calls = engine.calls();
    let read = calls
        .iter()
        .position(|call| call == "read_entry docs/a.txt 536870912 4096")
        .unwrap_or_else(|| panic!("no read_entry: {calls:?}"));
    let add = calls
        .iter()
        .position(|call| call == "add a.txt")
        .unwrap_or_else(|| panic!("no add: {calls:?}"));
    assert!(read < add, "{calls:?}");
    assert_eq!(json_data(&target_archive, "a.txt").as_deref(), Some("text"));
    assert_eq!(json_paths(&source_archive), vec!["docs/a.txt".to_string()]);

    // Moving out of one archive into another adds, then deletes the source.
    let moved = move_entry_resolved(
        text(&source_archive.join("docs").join("a.txt")),
        text(&target_archive.join("moved")),
        "error".into(),
    )
    .expect("move between archives");
    assert_eq!(moved, text(&target_archive.join("moved").join("a.txt")));
    let calls = mutation_calls(&engine);
    assert_eq!(
        calls[calls.len() - 2..].to_vec(),
        vec![
            "add moved/a.txt".to_string(),
            "delete docs/a.txt".to_string()
        ]
    );
    assert!(json_paths(&source_archive).is_empty());
    assert_eq!(
        json_data(&target_archive, "moved/a.txt").as_deref(),
        Some("text")
    );
    let _ = fs::remove_dir_all(root);
}

/// Archives made on Linux can hold names that differ only by case. Engine
/// mutations act on exact names, so these are written by the test helper as
/// `A.txt`/`a.txt` and `Docs`/`docs`, with the upper-case spelling listed
/// first (the case that used to lose its listing slot).
fn write_case_colliding_archive(archive: &Path) {
    write_json_archive(
        archive,
        &[
            ("A.txt", false, "upper"),
            ("a.txt", false, "lower"),
            ("Docs", true, ""),
            ("Docs/x.txt", false, "upper-x"),
            ("docs", true, ""),
            ("docs/y.txt", false, "lower-y"),
        ],
    );
}

#[test]
fn engine_case_colliding_names_delete_only_the_selected_entry() {
    let (engine, _registration) = register_json_engine("com.test.engcasedel", ".engcasedel", true);
    let root = unique_temp_dir("engine-case-delete");
    let archive = root.join("x.engcasedel");

    write_case_colliding_archive(&archive);
    delete_archive_entry(&text(&archive.join("A.txt"))).expect("delete A.txt");
    assert_eq!(mutation_calls(&engine), vec!["delete A.txt".to_string()]);
    assert_eq!(json_data(&archive, "A.txt"), None);
    assert_eq!(json_data(&archive, "a.txt").as_deref(), Some("lower"));

    write_case_colliding_archive(&archive);
    delete_archive_entry(&text(&archive.join("a.txt"))).expect("delete a.txt");
    assert_eq!(mutation_calls(&engine)[1], "delete a.txt");
    assert_eq!(json_data(&archive, "A.txt").as_deref(), Some("upper"));
    assert_eq!(json_data(&archive, "a.txt"), None);

    // Selecting both spellings deletes both, and an entry of `docs` is not
    // folded into the `Docs` folder.
    write_case_colliding_archive(&archive);
    let errors = delete_archive_entries(&[
        text(&archive.join("A.txt")),
        text(&archive.join("a.txt")),
        text(&archive.join("Docs")),
        text(&archive.join("docs").join("y.txt")),
    ]);
    assert!(errors.is_empty(), "{errors:?}");
    assert_eq!(
        mutation_calls(&engine)[2],
        "delete A.txt,a.txt,Docs,docs/y.txt"
    );
    assert_eq!(json_paths(&archive), vec!["docs".to_string()]);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_case_colliding_names_rename_and_move_only_the_selected_entry() {
    let (engine, _registration) = register_json_engine("com.test.engcaseren", ".engcaseren", true);
    let root = unique_temp_dir("engine-case-rename");
    let archive = root.join("x.engcaseren");

    write_case_colliding_archive(&archive);
    let renamed =
        rename_archive_entry(text(&archive.join("A.txt")), "b.txt".into()).expect("rename A.txt");
    assert_eq!(renamed, text(&archive.join("b.txt")));
    assert_eq!(
        mutation_calls(&engine),
        vec!["rename A.txt b.txt".to_string()]
    );
    assert_eq!(json_data(&archive, "b.txt").as_deref(), Some("upper"));
    assert_eq!(json_data(&archive, "a.txt").as_deref(), Some("lower"));

    // A change of case onto the other spelling's name is taken.
    write_case_colliding_archive(&archive);
    assert_eq!(
        rename_archive_entry(text(&archive.join("A.txt")), "a.txt".into()).unwrap_err(),
        "A file or directory with that name already exists: a.txt"
    );
    assert_eq!(mutation_calls(&engine).len(), 1, "{:?}", engine.calls());

    let moved = move_entry_resolved(
        text(&archive.join("a.txt")),
        text(&archive.join("docs")),
        "error".into(),
    )
    .expect("move a.txt into docs");
    assert_eq!(moved, text(&archive.join("docs").join("a.txt")));
    assert_eq!(mutation_calls(&engine)[1], "rename a.txt docs/a.txt");
    assert_eq!(json_data(&archive, "A.txt").as_deref(), Some("upper"));
    assert_eq!(json_data(&archive, "docs/a.txt").as_deref(), Some("lower"));
    assert_eq!(json_data(&archive, "Docs/a.txt"), None);

    // `replace` removes the exact spelling the copy lands on.
    write_case_colliding_archive(&archive);
    let local = root.join("local");
    fs::create_dir_all(&local).unwrap();
    let source = local.join("a.txt");
    fs::write(&source, b"new").unwrap();
    let replaced = copy_entry_resolved(text(&source), text(&archive), "replace".into())
        .expect("replace a.txt");
    assert_eq!(replaced, text(&archive.join("a.txt")));
    assert_eq!(
        mutation_calls(&engine)[2..],
        ["delete a.txt".to_string(), "add a.txt".to_string()]
    );
    assert_eq!(json_data(&archive, "A.txt").as_deref(), Some("upper"));
    assert_eq!(json_data(&archive, "a.txt").as_deref(), Some("new"));
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_destination_folder_takes_the_listed_spelling() {
    let (engine, _registration) = register_json_engine("com.test.engcasedst", ".engcasedst", true);
    let root = unique_temp_dir("engine-case-destination");
    let archive = root.join("x.engcasedst");
    write_json_archive(
        &archive,
        &[("Docs/x.txt", false, "x"), ("top.txt", false, "top")],
    );

    let created =
        create_archive_file(text(&archive.join("DOCS")), "n.txt".into()).expect("create in DOCS");
    assert_eq!(created, text(&archive.join("Docs").join("n.txt")));
    let moved = move_entry_resolved(
        text(&archive.join("top.txt")),
        text(&archive.join("docs").join("New")),
        "error".into(),
    )
    .expect("move into docs/New");
    assert_eq!(
        moved,
        text(&archive.join("Docs").join("New").join("top.txt"))
    );
    assert_eq!(
        mutation_calls(&engine),
        vec![
            "add Docs/n.txt".to_string(),
            "rename top.txt Docs/New/top.txt".to_string()
        ]
    );

    // A file anywhere along the destination is not a folder.
    let under_file = archive.join("docs").join("X.TXT").join("sub");
    assert_eq!(
        create_archive_file(text(&under_file), "n.txt".into()).unwrap_err(),
        format!("Destination is not a directory: {}", under_file.display())
    );
    assert_eq!(mutation_calls(&engine).len(), 2, "{:?}", engine.calls());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_name_that_folds_onto_several_entries_is_ambiguous() {
    let (engine, _registration) = register_json_engine("com.test.engcaseamb", ".engcaseamb", true);
    let root = unique_temp_dir("engine-case-ambiguous");
    let archive = root.join("x.engcaseamb");
    write_case_colliding_archive(&archive);
    let before = fs::read(&archive).unwrap();
    let local = root.join("local");
    fs::create_dir_all(&local).unwrap();
    let source = local.join("A.TXT");
    fs::write(&source, b"new").unwrap();
    let ambiguous = |path: &Path| format!("Ambiguous archive entry name: {}", path.display());

    assert_eq!(
        delete_archive_entry(&text(&archive.join("A.TXT"))).unwrap_err(),
        ambiguous(&archive.join("A.TXT"))
    );
    assert_eq!(
        delete_archive_entries(&[text(&archive.join("A.TXT"))]),
        vec![ambiguous(&archive.join("A.TXT"))]
    );
    assert_eq!(
        rename_archive_entry(text(&archive.join("A.TXT")), "b.txt".into()).unwrap_err(),
        ambiguous(&archive.join("A.TXT"))
    );
    assert_eq!(
        move_entry_resolved(
            text(&archive.join("A.TXT")),
            text(&archive.join("Docs")),
            "error".into()
        )
        .unwrap_err(),
        ambiguous(&archive.join("A.TXT"))
    );
    assert_eq!(
        copy_entry_resolved(text(&source), text(&archive), "replace".into()).unwrap_err(),
        ambiguous(&archive.join("A.TXT"))
    );
    assert_eq!(
        create_archive_file(text(&archive.join("DOCS")), "n.txt".into()).unwrap_err(),
        ambiguous(&archive.join("DOCS"))
    );
    assert!(mutation_calls(&engine).is_empty(), "{:?}", engine.calls());
    assert_eq!(fs::read(&archive).unwrap(), before);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_mutation_requires_can_modify() {
    let (engine, _registration) = register_json_engine("com.test.engro", ".engro", false);
    let root = unique_temp_dir("engine-read-only");
    let archive = root.join("x.engro");
    write_json_archive(
        &archive,
        &[("docs", true, ""), ("docs/a.txt", false, "text")],
    );
    let before = fs::read(&archive).unwrap();
    let local = root.join("local");
    fs::create_dir_all(&local).unwrap();
    let local_file = local.join("n.txt");
    fs::write(&local_file, b"n").unwrap();
    let entry = text(&archive.join("docs").join("a.txt"));
    let expected = "json-engine cannot modify .engro archives";

    let results = vec![
        create_archive_directory(text(&archive), "new".into()).map(|_| ()),
        create_archive_file(text(&archive), "n.txt".into()).map(|_| ()),
        rename_archive_entry(entry.clone(), "b.txt".into()).map(|_| ()),
        delete_archive_entry(&entry),
        copy_entry_resolved(text(&local_file), text(&archive), "error".into()).map(|_| ()),
        move_entry_resolved(entry, text(&local), "error".into()).map(|_| ()),
    ];

    for result in results {
        assert_eq!(result.unwrap_err(), expected);
    }
    assert!(engine.calls().is_empty(), "{:?}", engine.calls());
    assert_eq!(fs::read(&archive).unwrap(), before);
    assert!(local_file.exists());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_create_archive_writes_a_temp_file_then_renames() {
    let (engine, _registration) = register_json_engine("com.test.engnew", ".engnew", true);
    let root = unique_temp_dir("engine-create-archive");
    let source = root.join("src");
    fs::create_dir_all(&source).unwrap();
    fs::write(source.join("a.txt"), b"text").unwrap();
    let out = root.join("out");
    fs::create_dir_all(&out).unwrap();
    let target = out.join("new.engnew");

    create_archive(vec![text(&source)], text(&target), "engnew".into()).expect("create");

    assert!(target.is_file());
    assert_eq!(entry_names(&out), vec!["new.engnew".to_string()]);
    assert_eq!(engine.calls(), vec!["create 1".to_string()]);
    assert_eq!(json_data(&target, "src/a.txt").as_deref(), Some("text"));

    *engine.fail_with.lock().unwrap() = Some("create failed".to_string());
    let failed = out.join("bad.engnew");
    let error = create_archive(vec![text(&source)], text(&failed), "engnew".into())
        .expect_err("the engine fails");

    assert!(error.contains("create failed"), "{error}");
    assert!(!failed.exists());
    assert_eq!(entry_names(&out), vec!["new.engnew".to_string()]);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_mutation_failure_is_reported_unchanged() {
    let (engine, _registration) = register_json_engine("com.test.engfail", ".engfail", true);
    let root = unique_temp_dir("engine-failure");
    let archive = root.join("x.engfail");
    write_json_archive(&archive, &[("docs/a.txt", false, "text")]);
    let before = fs::read(&archive).unwrap();
    *engine.fail_with.lock().unwrap() = Some("boom".to_string());

    let error = delete_archive_entry(&text(&archive.join("docs").join("a.txt")))
        .expect_err("the engine fails");

    assert!(error.contains("boom"), "{error}");
    assert_eq!(
        mutation_calls(&engine),
        vec!["delete docs/a.txt".to_string()]
    );
    assert_eq!(fs::read(&archive).unwrap(), before);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_batch_rename_inside_an_archive_is_refused_before_any_engine_call() {
    let (engine, _registration) = register_json_engine("com.test.engbr", ".engbr", true);
    let root = unique_temp_dir("engine-batch-rename");
    let archive = root.join("x.engbr");
    write_json_archive(&archive, &[("a.txt", false, "a"), ("b.txt", false, "b")]);
    let before = fs::read(&archive).unwrap();
    let first = text(&archive.join("a.txt"));

    let error = crate::file_ops::batch_rename(vec![
        crate::file_ops::RenameRequest {
            path: first.clone(),
            new_name: "c.txt".to_string(),
        },
        crate::file_ops::RenameRequest {
            path: text(&archive.join("b.txt")),
            new_name: "d.txt".to_string(),
        },
    ])
    .expect_err("batch rename does not reach inside archives");

    assert_eq!(error, format!("Path does not exist: {first}"));
    assert!(engine.calls().is_empty(), "{:?}", engine.calls());
    assert_eq!(fs::read(&archive).unwrap(), before);
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_temp_path_sits_beside_the_archive() {
    let root = unique_temp_dir("engine-temp-path");
    let archive = root.join("Backup.Tar.Hello");

    let temp = unique_engine_temp_path(&archive, ".tar.hello").expect("temp path");

    assert_eq!(temp.parent(), Some(root.as_path()));
    let name = temp.file_name().unwrap().to_string_lossy().into_owned();
    assert!(name.starts_with(".Backup.simplefile-new-"), "{name}");
    assert!(name.ends_with(".tar.hello"), "{name}");
    assert!(!temp.exists());
    let _ = fs::remove_dir_all(root);
}

#[test]
fn engine_context_reports_progress_and_untracked_drops_it() {
    let seen = std::sync::Mutex::new(Vec::new());
    let record = |progress: EngineProgress| seen.lock().unwrap().push(progress);
    let cancel = std::sync::atomic::AtomicBool::new(false);
    let ctx = EngineContext {
        operation_id: Some("op-1"),
        cancel: &cancel,
        progress: &record,
    };

    ctx.report(3, 10, Some("docs/a.txt"));
    ctx.report(12, 10, None);
    assert!(!ctx.is_cancelled());
    cancel.store(true, std::sync::atomic::Ordering::SeqCst);
    assert!(ctx.is_cancelled());

    assert_eq!(
        seen.lock().unwrap().clone(),
        vec![
            EngineProgress {
                current: 3,
                total: 10,
                current_item: Some("docs/a.txt".to_string()),
            },
            EngineProgress {
                current: 10,
                total: 10,
                current_item: None,
            },
        ]
    );
    let untracked = EngineContext::untracked();
    assert!(untracked.operation_id.is_none());
    assert!(!untracked.is_cancelled());
    untracked.report(1, 2, None);
}

// --- `.7z` without its plugin -------------------------------------------

const SEVEN_ZIP_NOTE: &str = "7-Zip support requires the SumaFile 7-Zip plugin.";
const SEVEN_ZIP_OWNER: &str = "com.sumafile.sevenzip";

/// A folder holding `x.7z`, a file no engine can read (its content does
/// not matter while nothing serves `.7z`).
fn unserved_seven_zip(name: &str) -> (PathBuf, PathBuf) {
    let root = unique_temp_dir(name);
    let archive = root.join("x.7z");
    fs::write(&archive, b"not read without an engine").expect("write x.7z");
    (root, archive)
}

fn inside_archive(archive: &Path, inner: &str) -> String {
    format!("{}\\{inner}", archive.display())
}

#[test]
fn capabilities_report_7z_unavailable_without_its_plugin() {
    let _serial = lock_seven_z_registry();

    let capabilities = get_archive_capabilities();

    let rows: Vec<&crate::models::ArchiveFormatCapability> = capabilities
        .formats
        .iter()
        .filter(|format| format.extension == ".7z")
        .collect();
    assert_eq!(rows.len(), 1, "{:?}", capabilities.formats);
    let row = rows[0];
    assert_eq!(row.format, "7z");
    assert_eq!(row.extension, ".7z");
    assert!(!row.can_list);
    assert!(!row.can_extract);
    assert!(!row.can_create);
    assert!(!row.can_modify);
    assert_eq!(row.engine, "unavailable");
    assert_eq!(row.note.as_deref(), Some(SEVEN_ZIP_NOTE));
    let built_in: Vec<&str> = capabilities
        .formats
        .iter()
        .take(4)
        .map(|format| format.format.as_str())
        .collect();
    assert_eq!(built_in, ["zip", "tar", "tar.gz", "rar"]);
    assert_eq!(
        capabilities
            .formats
            .last()
            .map(|format| format.extension.as_str()),
        Some(".7z"),
        "the placeholder row comes after the built-in and engine rows"
    );
}

#[test]
fn capabilities_list_7z_from_its_engine_once_registered() {
    let _serial = lock_seven_z_registry();
    let (_engine, _registration) = register_json_engine(SEVEN_ZIP_OWNER, ".7z", true);

    let capabilities = get_archive_capabilities();

    let rows: Vec<&crate::models::ArchiveFormatCapability> = capabilities
        .formats
        .iter()
        .filter(|format| format.extension == ".7z")
        .collect();
    assert_eq!(rows.len(), 1, "{:?}", capabilities.formats);
    let row = rows[0];
    assert_eq!(row.format, "7z");
    assert_eq!(row.engine, "plugin:com.sumafile.sevenzip");
    assert!(row.can_list && row.can_extract && row.can_create && row.can_modify);
    assert!(row.note.is_none());
    assert!(!capabilities
        .formats
        .iter()
        .any(|format| format.engine == "unavailable"));
}

#[test]
fn seven_zip_operations_without_an_engine_fail_with_the_plugin_note() {
    let _serial = lock_seven_z_registry();
    let (root, archive) = unserved_seven_zip("seven-zip-note");
    let archive_string = archive.to_string_lossy().into_owned();

    assert_eq!(
        list_archive(archive_string.clone()).expect_err("list without an engine"),
        SEVEN_ZIP_NOTE
    );
    let destination = root.join("out");
    assert_eq!(
        extract_archive(
            archive_string.clone(),
            destination.to_string_lossy().into_owned()
        )
        .expect_err("extract without an engine"),
        SEVEN_ZIP_NOTE
    );
    assert!(
        !destination.exists(),
        "a refused extract creates no destination"
    );
    let source = root.join("source.txt");
    fs::write(&source, b"source").expect("write source");
    let created = root.join("new.7z");
    assert_eq!(
        create_archive(
            vec![source.to_string_lossy().into_owned()],
            created.to_string_lossy().into_owned(),
            "7z".to_string()
        )
        .expect_err("create without an engine"),
        SEVEN_ZIP_NOTE
    );
    assert!(!created.exists());
    assert_eq!(
        list_archive_directory(&archive_string).expect_err("browse the archive file"),
        SEVEN_ZIP_NOTE
    );
    assert_eq!(
        list_archive_directory(&inside_archive(&archive, "inner"))
            .expect_err("browse inside the archive"),
        SEVEN_ZIP_NOTE
    );

    let _ = fs::remove_dir_all(root);
}

#[test]
fn seven_zip_listing_without_an_engine_reports_the_note_through_dir_list() {
    let _serial = lock_seven_z_registry();
    let (root, archive) = unserved_seven_zip("seven-zip-dir-list");

    for path in [
        archive.to_string_lossy().into_owned(),
        inside_archive(&archive, "docs"),
    ] {
        for options in [Some(crate::dir_list::ListDirectoryOptions::default()), None] {
            let described = format!("{path} with options {}", options.is_some());
            let error =
                crate::dir_list::list_directory_capturing_raw(path.clone(), options, |_| Ok(()))
                    .expect_err(&described);
            assert_eq!(error, SEVEN_ZIP_NOTE, "{described}");
        }
    }

    let _ = fs::remove_dir_all(root);
}

#[test]
fn seven_zip_file_without_an_engine_is_an_ordinary_file() {
    let _serial = lock_seven_z_registry();
    let (root, archive) = unserved_seven_zip("seven-zip-ordinary");
    let archive_string = archive.to_string_lossy().into_owned();

    assert!(split_archive_path(&archive_string)
        .expect("the file itself splits")
        .is_none());
    assert!(!is_archive_path(&archive_string));
    assert!(!is_archive_virtual_path(&archive_string));
    let folder = root.join("dest");
    fs::create_dir_all(&folder).expect("create destination folder");
    assert!(
        !should_handle_transfer(&archive_string, &folder.to_string_lossy())
            .expect("a plain file transfer")
    );
    assert_eq!(
        split_archive_path(&inside_archive(&archive, "docs")).expect_err("a path inside the file"),
        SEVEN_ZIP_NOTE
    );
    assert_eq!(
        unavailable_archive_note(&archive_string),
        Some(SEVEN_ZIP_NOTE)
    );
    assert_eq!(
        unavailable_archive_note(&inside_archive(&archive, r"docs\a.txt")),
        Some(SEVEN_ZIP_NOTE)
    );
    assert_eq!(
        unavailable_archive_note(&root.join("missing.7z").to_string_lossy()),
        None
    );
    assert_eq!(unavailable_archive_note(&folder.to_string_lossy()), None);

    let _ = fs::remove_dir_all(root);
}

#[test]
fn seven_z_is_registrable_now() {
    assert!(is_registrable_extension(".7z"));
    assert!(is_registrable_extension("7Z"));
}
