use std::path::{Path, PathBuf};
use std::process::Stdio;

pub fn create_archive(
    paths: Vec<String>,
    archive_path: String,
    format: String,
) -> Result<(), String> {
    validate_archive_create_request(&paths, &archive_path)?;

    let normalized_format = format.trim().trim_start_matches('.').to_ascii_lowercase();
    match normalized_format.as_str() {
        "zip" => create_zip_archive(&paths, &archive_path),
        "tar" => create_tar_archive(&paths, &archive_path, None),
        "tar.gz" | "tgz" => create_tar_archive(&paths, &archive_path, Some("gz")),
        "rar" => Err(
            "RAR creation is not supported by SumaFile. Use ZIP, 7z, TAR, or TAR.GZ instead."
                .to_string(),
        ),
        "7z" => {
            let binary = super::seven_zip::require_seven_zip_binary()?;
            create_seven_zip_archive(&paths, &archive_path, &binary)
        }
        _ => Err(format!("Unsupported format: {format}")),
    }
}

fn validate_archive_create_request(paths: &[String], archive_path: &str) -> Result<(), String> {
    let archive_path = Path::new(archive_path);
    if archive_path.exists() {
        return Err(format!(
            "Archive already exists: {}",
            archive_path.to_string_lossy()
        ));
    }

    let archive_abs = absolute_path_for_new_file(archive_path)?;
    for source in paths {
        let source_path = Path::new(source);
        let Ok(source_abs) = std::fs::canonicalize(source_path) else {
            continue;
        };

        if archive_abs == source_abs || source_abs.is_dir() && archive_abs.starts_with(&source_abs)
        {
            return Err(format!(
                "Archive output cannot be inside a selected source: {}",
                archive_abs.to_string_lossy()
            ));
        }
    }

    Ok(())
}

fn absolute_path_for_new_file(path: &Path) -> Result<PathBuf, String> {
    let parent = path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .unwrap_or_else(|| Path::new("."));
    let file_name = path
        .file_name()
        .ok_or_else(|| "Archive output must include a file name.".to_string())?;
    let parent = std::fs::canonicalize(parent)
        .map_err(|e| format!("Cannot resolve archive output directory: {e}"))?;
    Ok(parent.join(file_name))
}

pub(super) fn create_zip_archive(paths: &[String], archive_path: &str) -> Result<(), String> {
    let file = create_new_archive_file(archive_path)?;
    let mut zip = zip::ZipWriter::new(file);
    let options = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated);
    for path_str in paths {
        let path = Path::new(path_str);
        let name = path
            .file_name()
            .ok_or_else(|| format!("Cannot get file name for: {path_str}"))?
            .to_string_lossy();
        if path.is_file() {
            zip.start_file(name.as_ref(), options)
                .map_err(|e| e.to_string())?;
            // Stream file contents rather than reading into a Vec to avoid
            // memory exhaustion when archiving large files.
            let mut src = std::fs::File::open(path).map_err(|e| e.to_string())?;
            std::io::copy(&mut src, &mut zip).map_err(|e| e.to_string())?;
        } else if path.is_dir() {
            zip.add_directory(name.as_ref(), options)
                .map_err(|e| e.to_string())?;
            add_dir_to_zip(&mut zip, path, name.as_ref(), options)?;
        }
    }
    zip.finish().map_err(|e| e.to_string())?;
    Ok(())
}

fn add_dir_to_zip<W: std::io::Write + std::io::Seek>(
    zip: &mut zip::ZipWriter<W>,
    dir: &Path,
    prefix: &str,
    options: zip::write::SimpleFileOptions,
) -> Result<(), String> {
    for entry in std::fs::read_dir(dir).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        let path = entry.path();
        let name = format!("{}/{}", prefix, entry.file_name().to_string_lossy());
        // Use symlink_metadata so we never follow circular symlinks into
        // infinite recursion during directory traversal.
        let Ok(ft) = entry.file_type() else { continue };
        if ft.is_file() {
            zip.start_file(&name, options).map_err(|e| e.to_string())?;
            // Stream rather than buffering the whole file to prevent OOM.
            let mut src = std::fs::File::open(&path).map_err(|e| e.to_string())?;
            std::io::copy(&mut src, zip).map_err(|e| e.to_string())?;
        } else if ft.is_dir() {
            zip.add_directory(&name, options)
                .map_err(|e| e.to_string())?;
            add_dir_to_zip(zip, &path, &name, options)?;
        }
        // Symlinks are intentionally skipped to avoid loops.
    }
    Ok(())
}

pub(super) fn create_seven_zip_archive(
    paths: &[String],
    archive_path: &str,
    seven_zip_binary: &str,
) -> Result<(), String> {
    if paths.is_empty() {
        return Err("No files specified".to_string());
    }

    let output = std::process::Command::new(seven_zip_binary)
        .arg("a")
        .arg("-t7z")
        .arg("-bd")
        .arg("-bb0")
        .arg("-sccUTF-8")
        .arg("-y")
        .arg("-sse")
        .arg("-spd")
        .arg("--")
        .arg(archive_path)
        .args(paths)
        .stdin(Stdio::null())
        .output()
        .map_err(|e| format!("Failed to run 7-Zip command: {e}"))?;

    super::seven_zip::ensure_seven_zip_success(&output, "7-Zip archive creation")
}

pub(super) fn create_tar_archive(
    paths: &[String],
    archive_path: &str,
    compression: Option<&str>,
) -> Result<(), String> {
    let file = create_new_archive_file(archive_path)?;

    fn add_paths_to_tar<W: std::io::Write>(
        archive: &mut tar::Builder<W>,
        paths: &[String],
    ) -> Result<(), String> {
        for path_str in paths {
            let path = Path::new(path_str);
            let name = path
                .file_name()
                .ok_or_else(|| format!("Cannot get file name for: {path_str}"))?
                .to_string_lossy();
            if path.is_file() {
                archive
                    .append_path_with_name(path, name.as_ref())
                    .map_err(|e| e.to_string())?;
            } else if path.is_dir() {
                archive
                    .append_dir_all(name.as_ref(), path)
                    .map_err(|e| e.to_string())?;
            }
        }
        Ok(())
    }

    match compression {
        Some("gz") => {
            let encoder = flate2::write::GzEncoder::new(file, flate2::Compression::default());
            let mut archive = tar::Builder::new(encoder);
            add_paths_to_tar(&mut archive, paths)?;
            archive.finish().map_err(|e| e.to_string())?;
        }
        None => {
            let mut archive = tar::Builder::new(file);
            add_paths_to_tar(&mut archive, paths)?;
            archive.finish().map_err(|e| e.to_string())?;
        }
        _ => return Err("Unsupported compression".to_string()),
    }
    Ok(())
}

fn create_new_archive_file(archive_path: &str) -> Result<std::fs::File, String> {
    std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(archive_path)
        .map_err(|e| {
            if e.kind() == std::io::ErrorKind::AlreadyExists {
                format!("Archive already exists: {archive_path}")
            } else {
                e.to_string()
            }
        })
}
