#![allow(dead_code, unused_imports)]
use crate::models::{AppAboutInfo, UpdateInfo};
use serde::Deserialize;
use std::collections::HashMap;
use std::io;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const APP_NAME: &str = "SumaFile";
const APP_IDENTIFIER: &str = "com.simplefile.desktop";
const UPDATE_MANIFEST_URL: &str =
    "https://github.com/conniecombs/SimpleFile-Windows/releases/latest/download/latest-winui.json";

#[derive(Debug, Deserialize)]
struct LatestManifest {
    version: String,
    notes: Option<String>,
    pub_date: Option<String>,
    platforms: HashMap<String, ManifestPlatform>,
}

#[derive(Debug, Deserialize)]
struct ManifestPlatform {
    url: String,
}

pub fn get_app_about_info() -> AppAboutInfo {
    AppAboutInfo {
        name: APP_NAME.to_string(),
        version: env!("CARGO_PKG_VERSION").to_string(),
        identifier: APP_IDENTIFIER.to_string(),
        os: std::env::consts::OS.to_string(),
        arch: std::env::consts::ARCH.to_string(),
        authors: env!("CARGO_PKG_AUTHORS").to_string(),
        repository: option_env!("CARGO_PKG_REPOSITORY")
            .unwrap_or("https://github.com/conniecombs/SimpleFile-Windows")
            .to_string(),
    }
}

pub fn check_for_update() -> Result<Option<UpdateInfo>, String> {
    let manifest = load_manifest()?;
    if !is_newer_version(&manifest.version, env!("CARGO_PKG_VERSION")) {
        return Ok(None);
    }

    Ok(Some(UpdateInfo {
        version: manifest.version,
        date: manifest.pub_date,
        body: manifest.notes,
    }))
}

pub fn install_update() -> Result<(), String> {
    install_update_with_progress(|_, _| {})
}

pub fn install_update_with_progress<F>(mut _progress: F) -> Result<(), String>
where
    F: FnMut(u64, u64),
{
    // Disabled: the updater previously downloaded and executed an unverified
    // binary from the manifest URL.  Until Ed25519 signature verification of
    // the downloaded installer is implemented (against an embedded public key),
    // in-app installation is blocked.  Users are directed to the GitHub
    // releases page where they can verify the download themselves.
    Err(concat!(
        "Automatic update installation is currently disabled because the downloaded ",
        "installer cannot yet be cryptographically verified. Please download the ",
        "latest release from the GitHub releases page."
    )
    .to_string())
}

fn load_manifest() -> Result<LatestManifest, String> {
    let content = if let Ok(json) = std::env::var("SIMPLEFILE_UPDATE_MANIFEST_JSON") {
        json
    } else if let Ok(path) = std::env::var("SIMPLEFILE_UPDATE_MANIFEST_PATH") {
        std::fs::read_to_string(path)
            .map_err(|error| format!("Failed to read update manifest: {error}"))?
    } else {
        download_text(UPDATE_MANIFEST_URL)?
    };

    serde_json::from_str(&content).map_err(|error| format!("Invalid update manifest: {error}"))
}

fn current_platform_key() -> &'static str {
    match std::env::consts::ARCH {
        "x86_64" => "windows-x86_64",
        "aarch64" => "windows-aarch64",
        _ => "windows-x86_64",
    }
}

fn is_newer_version(candidate: &str, current: &str) -> bool {
    let candidate = version_parts(candidate);
    let current = version_parts(current);
    let max_len = candidate.len().max(current.len());
    for index in 0..max_len {
        let left = *candidate.get(index).unwrap_or(&0);
        let right = *current.get(index).unwrap_or(&0);
        if left > right {
            return true;
        }
        if left < right {
            return false;
        }
    }
    false
}

fn version_parts(version: &str) -> Vec<u64> {
    version
        .trim()
        .trim_start_matches('v')
        .split(|character: char| !character.is_ascii_digit())
        .filter(|part| !part.is_empty())
        .map(|part| part.parse().unwrap_or(0))
        .collect()
}

fn update_installer_path(url: &str) -> Result<PathBuf, String> {
    let file_name = url
        .rsplit('/')
        .next()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or("SumaFile-update.exe");
    let millis = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(|error| error.to_string())?
        .as_millis();
    Ok(std::env::temp_dir().join(format!("simplefile-update-{millis}-{file_name}")))
}

fn download_text(url: &str) -> Result<String, String> {
    let response = ureq::get(url)
        .call()
        .map_err(|error| format!("Failed to check for updates: {error}"))?;
    response
        .into_body()
        .read_to_string()
        .map_err(|error| format!("Failed to read update response: {error}"))
}

fn download_file(url: &str, path: &Path) -> Result<(), String> {
    let response = ureq::get(url)
        .call()
        .map_err(|error| format!("Failed to download update: {error}"))?;
    let mut file = std::fs::File::create(path)
        .map_err(|error| format!("Failed to create update file: {error}"))?;
    std::io::copy(&mut response.into_body().into_reader(), &mut file)
        .map_err(|error| format!("Failed to write update file: {error}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::is_newer_version;

    #[test]
    fn version_compare_handles_multi_digit_parts() {
        assert!(is_newer_version("v1.10.0", "1.9.9"));
        assert!(!is_newer_version("1.1.0", "1.1.0"));
        assert!(!is_newer_version("1.0.9", "1.1.0"));
    }
}
