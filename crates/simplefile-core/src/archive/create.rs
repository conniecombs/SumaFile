use std::path::{Path, PathBuf};
use std::process::Stdio;

pub fn create_archive(
    paths: Vec<String>,
    archive_path: String,
    format: String,
) -> Result<(), String> {
    let normalized_format = format.trim().trim_start_matches('.').to_ascii_lowercase();
    match normalized_format.as_str() {
        "zip" => create_zip_archive(&paths, &archive_path),
        "tar" => create_tar_archive(&paths, &archive_path, None),
        "tar.gz" | "tgz" => create_tar_archive(&paths, &archive_path, Some("gz")),
        "rar" => {
            let binary = resolve_rar_binary().ok_or_else(|| {
                "RAR command not found. Install it from Settings -> RAR Tools.".to_string()
            })?;
            create_rar_archive(&paths, &archive_path, &binary)
        }
        "7z" => {
            let binary = super::seven_zip::require_seven_zip_binary()?;
            create_seven_zip_archive(&paths, &archive_path, &binary)
        }
        _ => Err(format!("Unsupported format: {format}")),
    }
}

pub(super) fn create_zip_archive(paths: &[String], archive_path: &str) -> Result<(), String> {
    let file = std::fs::File::create(archive_path).map_err(|e| e.to_string())?;
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

pub fn resolve_rar_binary() -> Option<String> {
    if let Ok(path) = std::env::var("SIMPLEFILE_RAR") {
        let trimmed = path.trim();
        if !trimmed.is_empty() && Path::new(trimmed).exists() {
            return Some(trimmed.to_string());
        }
    }

    if std::process::Command::new("rar")
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .output()
        .is_ok()
    {
        return Some("rar".to_string());
    }

    for path in [
        r"C:\Program Files\WinRAR\rar.exe",
        r"C:\Program Files (x86)\WinRAR\rar.exe",
    ] {
        if Path::new(path).exists() {
            return Some(path.to_string());
        }
    }

    if let Ok(local_app_data) = std::env::var("LOCALAPPDATA") {
        let path = PathBuf::from(local_app_data)
            .join("Programs")
            .join("WinRAR")
            .join("rar.exe");
        if path.exists() {
            return Some(path.to_string_lossy().to_string());
        }
    }

    None
}

pub(super) fn create_rar_archive(
    paths: &[String],
    archive_path: &str,
    rar_binary: &str,
) -> Result<(), String> {
    if paths.is_empty() {
        return Err("No files specified".to_string());
    }

    // Single invocation with absolute paths.
    // -ep1 strips the leading path components so files appear as basenames inside the archive,
    // consistent with ZIP/TAR behaviour.
    // -r recurses into subdirectories.
    // stdin is set to null to prevent the process from blocking on terminal input.

    // Prevent argument injection: rar parses switches by position, so archive_path
    // is consumed before the "--" end-of-options delimiter and can still be
    // misinterpreted as a flag if it starts with '-'.  Prepend "./" to any
    // path that begins with '-' so the rar binary always sees it as a filename.
    let archive_arg: std::borrow::Cow<str> = if archive_path.starts_with('-') {
        format!("./{archive_path}").into()
    } else {
        archive_path.into()
    };

    let output = std::process::Command::new(rar_binary)
        .arg("a")
        .arg("-r")
        .arg("-ep1")
        .arg(archive_arg.as_ref())
        .arg("--") // POSIX end-of-options: prevents filenames starting with '-' being
        // interpreted as flags by the rar binary (argument injection guard).
        .args(paths)
        .stdin(std::process::Stdio::null())
        .output()
        .map_err(|e| format!("Failed to run rar command: {e}"))?;

    // Exit code 0 = success; 1 = warning (non-fatal, archive was still written).
    // Treat anything above 1 as a hard failure.
    let code = output.status.code().unwrap_or(2);
    if code > 1 {
        let stderr = String::from_utf8_lossy(&output.stderr);
        let stdout = String::from_utf8_lossy(&output.stdout);
        let detail = format!("{stderr}{stdout}").trim().to_string();
        return Err(if detail.is_empty() {
            format!("RAR creation failed (exit code {code})")
        } else {
            format!("RAR creation failed: {detail}")
        });
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
        .stdin(std::process::Stdio::null())
        .output()
        .map_err(|e| format!("Failed to run 7-Zip command: {e}"))?;

    super::seven_zip::ensure_seven_zip_success(&output, "7-Zip archive creation")
}

pub(super) fn create_tar_archive(
    paths: &[String],
    archive_path: &str,
    compression: Option<&str>,
) -> Result<(), String> {
    let file = std::fs::File::create(archive_path).map_err(|e| e.to_string())?;

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
