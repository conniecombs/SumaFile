use crate::models::{AppAboutInfo, UpdateInfo};
use crate::utils::hex_encode;
use base64::{engine::general_purpose, Engine as _};
use ed25519_dalek::{Signature, Verifier, VerifyingKey};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const APP_NAME: &str = "SumaFile";
const APP_IDENTIFIER: &str = "com.simplefile.desktop";
const UPDATE_MANIFEST_URL: &str =
    "https://github.com/conniecombs/SumaFile/releases/latest/download/latest-winui.json";
const TRUSTED_ASSET_PREFIX: &str = "https://github.com/conniecombs/SumaFile/releases/";
const TRUSTED_INSTALLER_SUFFIX: &str = "_x64-winui-setup.exe";
const PUBLIC_KEY_BYTES: usize = 32;
const SIGNATURE_BYTES: usize = 64;

#[derive(Debug, Deserialize)]
struct LatestManifest {
    version: String,
    notes: Option<String>,
    pub_date: Option<String>,
    #[serde(default)]
    channel: Option<String>,
    #[serde(default)]
    platforms: HashMap<String, ManifestPlatform>,
}

#[derive(Debug, Deserialize)]
struct ManifestPlatform {
    url: String,
    #[serde(default)]
    signature: Option<String>,
    #[serde(default)]
    sha256: Option<String>,
    #[serde(default)]
    size: Option<u64>,
}

#[derive(Debug)]
struct UpdatePackage<'a> {
    url: &'a str,
    signature: &'a str,
    sha256: &'a str,
    size: u64,
}

struct DownloadedUpdate {
    path: PathBuf,
    bytes: Vec<u8>,
    size: u64,
    sha256: String,
}

pub fn get_app_about_info() -> AppAboutInfo {
    AppAboutInfo {
        name: APP_NAME.to_string(),
        version: crate::APP_DISPLAY_VERSION.to_string(),
        identifier: APP_IDENTIFIER.to_string(),
        os: std::env::consts::OS.to_string(),
        arch: std::env::consts::ARCH.to_string(),
        authors: env!("CARGO_PKG_AUTHORS").to_string(),
        repository: option_env!("CARGO_PKG_REPOSITORY")
            .unwrap_or("https://github.com/conniecombs/SumaFile")
            .to_string(),
    }
}

pub fn check_for_update() -> Result<Option<UpdateInfo>, String> {
    let manifest = load_manifest()?;
    if !is_newer_version(&manifest.version, crate::APP_DISPLAY_VERSION) {
        return Ok(None);
    }

    let platform = manifest.platforms.get(current_platform_key());
    let installable = platform
        .and_then(|platform| platform.update_package().ok())
        .is_some()
        && configured_public_key().is_some();

    Ok(Some(UpdateInfo {
        version: manifest.version,
        date: manifest.pub_date,
        body: manifest.notes,
        installable,
        channel: manifest.channel,
        url: platform.map(|platform| platform.url.clone()),
        sha256: platform.and_then(|platform| platform.sha256.clone()),
        size: platform.and_then(|platform| platform.size),
    }))
}

pub fn install_update() -> Result<(), String> {
    install_update_with_progress(|_, _| {})
}

pub fn install_update_with_progress<F>(progress: F) -> Result<(), String>
where
    F: FnMut(u64, u64),
{
    let manifest = load_manifest()?;
    if !is_newer_version(&manifest.version, crate::APP_DISPLAY_VERSION) {
        return Err("No newer SumaFile update is available.".to_string());
    }

    let platform = manifest
        .platforms
        .get(current_platform_key())
        .ok_or_else(|| {
            format!(
                "Update manifest does not include a package for {}.",
                current_platform_key()
            )
        })?;
    let package = platform.update_package()?;
    let public_key = configured_public_key().ok_or_else(|| {
        concat!(
            "Automatic update installation is not configured for this build. ",
            "Rebuild SumaFile with SIMPLEFILE_UPDATER_PUBLIC_KEY set to the ",
            "base64 Ed25519 public key that matches the release signing key."
        )
        .to_string()
    })?;
    let downloaded = download_verified_update(&package, public_key, progress)?;
    launch_installer(&downloaded.path)
}

impl ManifestPlatform {
    fn update_package(&self) -> Result<UpdatePackage<'_>, String> {
        validate_update_url(&self.url)?;
        let signature = self
            .signature
            .as_deref()
            .filter(|value| !value.trim().is_empty())
            .ok_or_else(|| "Update manifest is missing installer signature.".to_string())?;
        let sha256 = self
            .sha256
            .as_deref()
            .filter(|value| !value.trim().is_empty())
            .ok_or_else(|| "Update manifest is missing installer SHA-256.".to_string())?;
        validate_sha256(sha256)?;
        let size = self
            .size
            .filter(|value| *value > 0)
            .ok_or_else(|| "Update manifest is missing installer size.".to_string())?;

        Ok(UpdatePackage {
            url: &self.url,
            signature,
            sha256,
            size,
        })
    }
}

fn configured_public_key() -> Option<&'static str> {
    option_env!("SIMPLEFILE_UPDATER_PUBLIC_KEY").filter(|value| !value.trim().is_empty())
}

fn download_verified_update<F>(
    package: &UpdatePackage<'_>,
    public_key: &str,
    mut progress: F,
) -> Result<DownloadedUpdate, String>
where
    F: FnMut(u64, u64),
{
    let path = update_installer_path(package.url)?;
    let result = download_file(package.url, &path, package.size, |downloaded, total| {
        progress(downloaded, total)
    })
    .and_then(|downloaded| {
        verify_download_metadata(&downloaded, package)?;
        verify_payload_signature(&downloaded.bytes, package.signature, public_key)?;
        Ok(downloaded)
    });

    if result.is_err() {
        let _ = std::fs::remove_file(&path);
    }

    result
}

fn verify_download_metadata(
    downloaded: &DownloadedUpdate,
    package: &UpdatePackage<'_>,
) -> Result<(), String> {
    if downloaded.size != package.size {
        return Err(format!(
            "Downloaded update size mismatch. Expected {} bytes, got {} bytes.",
            package.size, downloaded.size
        ));
    }

    if !downloaded.sha256.eq_ignore_ascii_case(package.sha256) {
        return Err(format!(
            "Downloaded update SHA-256 mismatch. Expected {}, got {}.",
            package.sha256, downloaded.sha256
        ));
    }

    Ok(())
}

fn verify_payload_signature(
    payload: &[u8],
    signature_b64: &str,
    public_key_b64: &str,
) -> Result<(), String> {
    let public_key = decode_base64(public_key_b64, "updater public key")?;
    if public_key.len() != PUBLIC_KEY_BYTES {
        return Err(format!(
            "Updater public key must be {PUBLIC_KEY_BYTES} bytes after base64 decoding."
        ));
    }
    let signature = decode_base64(signature_b64, "update signature")?;
    if signature.len() != SIGNATURE_BYTES {
        return Err(format!(
            "Update signature must be {SIGNATURE_BYTES} bytes after base64 decoding."
        ));
    }

    let public_key: [u8; PUBLIC_KEY_BYTES] = public_key
        .try_into()
        .map_err(|_| "Updater public key had an unexpected length.".to_string())?;
    let signature: [u8; SIGNATURE_BYTES] = signature
        .try_into()
        .map_err(|_| "Update signature had an unexpected length.".to_string())?;
    let verifying_key = VerifyingKey::from_bytes(&public_key)
        .map_err(|error| format!("Invalid updater public key: {error}"))?;
    let signature = Signature::from_bytes(&signature);
    verifying_key
        .verify(payload, &signature)
        .map_err(|error| format!("Update signature verification failed: {error}"))
}

fn decode_base64(value: &str, label: &str) -> Result<Vec<u8>, String> {
    general_purpose::STANDARD
        .decode(value.trim())
        .map_err(|error| format!("Invalid {label}: {error}"))
}

fn validate_update_url(url: &str) -> Result<(), String> {
    if !url.starts_with(TRUSTED_ASSET_PREFIX) {
        return Err(format!(
            "Update URL must start with trusted SumaFile release prefix {TRUSTED_ASSET_PREFIX}."
        ));
    }
    if !url.ends_with(TRUSTED_INSTALLER_SUFFIX) {
        return Err(format!(
            "Update URL must point to a SumaFile x64 WinUI setup executable ending with {TRUSTED_INSTALLER_SUFFIX}."
        ));
    }
    if url.contains('\\') || url.contains(' ') {
        return Err("Update URL contains unsupported path characters.".to_string());
    }

    Ok(())
}

fn validate_sha256(value: &str) -> Result<(), String> {
    let value = value.trim();
    if value.len() != 64 || !value.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("Update manifest SHA-256 must be a 64-character hex string.".to_string());
    }

    Ok(())
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
    Ok(std::env::temp_dir().join(format!("sumafile-update-{millis}-{file_name}")))
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

fn download_file<F>(
    url: &str,
    path: &Path,
    total: u64,
    mut progress: F,
) -> Result<DownloadedUpdate, String>
where
    F: FnMut(u64, u64),
{
    let response = ureq::get(url)
        .call()
        .map_err(|error| format!("Failed to download update: {error}"))?;
    let mut reader = response.into_body().into_reader();
    let mut file = std::fs::File::create(path)
        .map_err(|error| format!("Failed to create update file: {error}"))?;
    let mut hasher = Sha256::new();
    let mut bytes = Vec::new();
    let mut downloaded = 0u64;
    let mut buffer = [0u8; 64 * 1024];

    loop {
        let read = reader
            .read(&mut buffer)
            .map_err(|error| format!("Failed to read update response: {error}"))?;
        if read == 0 {
            break;
        }
        file.write_all(&buffer[..read])
            .map_err(|error| format!("Failed to write update file: {error}"))?;
        hasher.update(&buffer[..read]);
        bytes.extend_from_slice(&buffer[..read]);
        downloaded = downloaded.saturating_add(read as u64);
        progress(downloaded, total);
    }

    file.flush()
        .map_err(|error| format!("Failed to flush update file: {error}"))?;

    Ok(DownloadedUpdate {
        path: path.to_path_buf(),
        bytes,
        size: downloaded,
        sha256: hex_encode(hasher.finalize()),
    })
}

#[cfg(windows)]
fn launch_installer(path: &Path) -> Result<(), String> {
    std::process::Command::new(path)
        .arg("/S")
        .spawn()
        .map_err(|error| format!("Failed to launch verified SumaFile installer: {error}"))?;
    Ok(())
}

#[cfg(not(windows))]
fn launch_installer(_path: &Path) -> Result<(), String> {
    Err("Automatic update installation is only supported on Windows.".to_string())
}

#[cfg(test)]
mod tests {
    use super::{
        is_newer_version, validate_sha256, validate_update_url, verify_download_metadata,
        verify_payload_signature, DownloadedUpdate, ManifestPlatform, UpdatePackage,
    };
    use std::path::PathBuf;

    const RFC8032_PUBLIC_KEY: &str = "11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=";
    const RFC8032_EMPTY_MESSAGE_SIGNATURE: &str =
        "5VZDAMNgrHKQhuLMgG6CioSHfx645dl02HPgZSJJAVVfuIIVkKM7rMYeOXAc+bRr0lv18FlbviRlUUFDjnoQCw==";

    #[test]
    fn version_compare_handles_multi_digit_parts() {
        assert!(is_newer_version("v1.10.0", "1.9.9"));
        assert!(!is_newer_version("1.1.0", "1.1.0"));
        assert!(!is_newer_version("1.0.9", "1.1.0"));
    }

    #[test]
    fn platform_metadata_requires_signed_sumafile_setup() {
        let valid = ManifestPlatform {
            url: "https://github.com/conniecombs/SumaFile/releases/download/v1.2.3/SumaFile_1.2.3_x64-winui-setup.exe".to_string(),
            signature: Some(RFC8032_EMPTY_MESSAGE_SIGNATURE.to_string()),
            sha256: Some("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855".to_string()),
            size: Some(123),
        };
        assert!(valid.update_package().is_ok());

        let missing_signature = ManifestPlatform {
            signature: Some(String::new()),
            ..valid
        };
        assert!(missing_signature.update_package().is_err());
    }

    #[test]
    fn update_urls_are_allowlisted() {
        assert!(validate_update_url(
            "https://github.com/conniecombs/SumaFile/releases/latest/download/SumaFile_1.0.0_x64-winui-setup.exe"
        )
        .is_ok());
        assert!(validate_update_url(
            "https://example.com/conniecombs/SumaFile/releases/latest/download/SumaFile_1.0.0_x64-winui-setup.exe"
        )
        .is_err());
        assert!(validate_update_url(
            "https://github.com/conniecombs/SumaFile/releases/latest/download/SumaFile_1.0.0_x64-winui.msi"
        )
        .is_err());
    }

    #[test]
    fn sha256_must_be_hex_encoded() {
        assert!(validate_sha256(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        )
        .is_ok());
        assert!(validate_sha256("not-a-hash").is_err());
    }

    #[test]
    fn payload_signature_uses_ed25519() {
        verify_payload_signature(&[], RFC8032_EMPTY_MESSAGE_SIGNATURE, RFC8032_PUBLIC_KEY)
            .expect("valid RFC8032 signature");
        assert!(verify_payload_signature(
            b"tampered",
            RFC8032_EMPTY_MESSAGE_SIGNATURE,
            RFC8032_PUBLIC_KEY
        )
        .is_err());
    }

    #[test]
    fn downloaded_metadata_must_match_manifest() {
        let package = UpdatePackage {
            url: "https://github.com/conniecombs/SumaFile/releases/latest/download/SumaFile_1.0.0_x64-winui-setup.exe",
            signature: RFC8032_EMPTY_MESSAGE_SIGNATURE,
            sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            size: 0,
        };
        let downloaded = DownloadedUpdate {
            path: PathBuf::from("SumaFile_1.0.0_x64-winui-setup.exe"),
            bytes: Vec::new(),
            size: 0,
            sha256: package.sha256.to_string(),
        };

        verify_download_metadata(&downloaded, &package).expect("matching metadata");
        let wrong_hash = DownloadedUpdate {
            sha256: "0000000000000000000000000000000000000000000000000000000000000000".to_string(),
            ..downloaded
        };
        assert!(verify_download_metadata(&wrong_hash, &package).is_err());
    }
}
