use super::extract::{
    extract_archive_entry_to_directory, extract_archive_to_directory, extract_tar, extract_zip,
    ExtractLimits,
};
use super::mutate::materialize_archive_entry_to_temp_with_limits;
use super::path::{
    archive_entry_relative_path, archive_format_for_path, build_virtual_archive_path,
    ensure_extract_path_within_destination, path_is_within_prefix, zip_entry_relative_path,
    ArchiveFormat,
};
use super::seven_zip::{parse_seven_zip_list_output, resolve_seven_zip_binary};
use super::*;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

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

#[test]
fn archive_format_recognizes_7z_extension() {
    assert_eq!(
        archive_format_for_path(Path::new("sample.7z")),
        Some(ArchiveFormat::SevenZip)
    );
}

#[test]
fn seven_zip_listing_parser_reads_files_and_directories() {
    let output = r#"
7-Zip 26.02 (x64)

Listing archive: sample.7z

--
Path = sample.7z
Type = 7z
Physical Size = 194

----------
Path = folder
Size = 0
Packed Size = 0
Attributes = D

Path = alpha.txt
Size = 5
Packed Size = 14
Attributes = A

Path = folder\beta.txt
Size = 5
Packed Size =
Attributes = A
"#;

    let entries = parse_seven_zip_list_output(output);

    assert_eq!(entries.len(), 3);
    assert!(entries[0].is_dir);
    assert_eq!(entries[1].path, "alpha.txt");
    assert_eq!(entries[1].size, 5);
    assert_eq!(entries[1].compressed_size, 14);
    assert_eq!(entries[2].path, r"folder\beta.txt");
    assert_eq!(entries[2].compressed_size, 0);
}

#[test]
fn seven_zip_create_list_extract_round_trip_when_available() {
    if resolve_seven_zip_binary().is_none() {
        return;
    }

    let root = unique_temp_dir("sevenzip-round-trip");
    let source = root.join("alpha.txt");
    fs::write(&source, b"hello").expect("write source file");
    let folder = root.join("folder");
    fs::create_dir_all(&folder).expect("create source folder");
    fs::write(folder.join("beta.txt"), b"world").expect("write nested source file");

    let archive_path = root.join("sample.7z");
    create_archive(
        vec![
            source.to_string_lossy().to_string(),
            folder.to_string_lossy().to_string(),
        ],
        archive_path.to_string_lossy().to_string(),
        "7z".to_string(),
    )
    .expect("create 7z archive");

    let info =
        list_archive(archive_path.to_string_lossy().to_string()).expect("list 7z archive info");
    assert_eq!(info.format, "7z");
    assert!(info.entries.iter().any(|entry| entry.name == "alpha.txt"));
    assert!(info.entries.iter().any(|entry| entry.name == "beta.txt"));

    let out = root.join("out");
    fs::create_dir_all(&out).expect("create out dir");
    extract_archive_to_directory(&archive_path, &out).expect("extract 7z archive");
    assert_eq!(
        fs::read(out.join("alpha.txt")).expect("read extracted alpha"),
        b"hello"
    );
    assert_eq!(
        fs::read(out.join("folder").join("beta.txt")).expect("read extracted beta"),
        b"world"
    );

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
    assert!(!path_is_within_prefix(Path::new("fold"), Path::new("folder")));
}

#[test]
fn materialize_archive_entry_cleans_temp_on_drop() {
    let root = unique_temp_dir("materialize-cleanup");
    let zip_path = root.join("sample.zip");
    write_test_zip(
        &zip_path,
        &[
            ("keep.txt", b"keep"),
            ("folder/other.txt", b"other"),
        ],
    );

    let virtual_path = build_virtual_archive_path(&zip_path, Path::new("keep.txt"));
    let materialized = materialize_archive_entry_to_temp(&virtual_path).expect("materialize");
    let cleanup_root = materialized
        .cleanup_root()
        .expect("archive materialize should own a work root")
        .to_path_buf();
    assert!(cleanup_root.exists(), "work root should exist while guard is alive");
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
    assert!(
        err.contains("size limit"),
        "unexpected error: {err}"
    );

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
