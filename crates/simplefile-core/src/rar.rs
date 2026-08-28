use crate::models::RarInstallPlan;
use crate::utils::hidden_command;
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, SystemTime};

const DOWNLOAD_URL: &str = "https://www.rarlab.com/rar/winrar-x64-723.exe";
const EXPECTED_DOWNLOAD_SHA256: &str =
    "8ff0daf3ed564cc743c0e23ff2e253997ffc74460f9673f0b6dd037b2db4ce7b";
const PENDING_INSTALL_TTL: Duration = Duration::from_secs(30 * 60);

#[derive(Clone)]
struct PendingRarInstall {
    created_at: SystemTime,
    installer_path: PathBuf,
}

pub fn check_rar_installed() -> bool {
    crate::archive::resolve_rar_binary().is_some()
}

pub fn prepare_rar_install() -> Result<RarInstallPlan, String> {
    let token = generate_confirmation_token()?;
    let installer_path = pending_installer_path(&token)?;

    download_file(DOWNLOAD_URL, &installer_path)?;
    let sha256 = verify_sha256_file(&installer_path)?;
    let publisher = match verify_windows_authenticode(&installer_path) {
        Ok(publisher) => publisher,
        Err(error) => {
            let _ = std::fs::remove_file(&installer_path);
            return Err(error);
        }
    };

    {
        let mut pending = pending_installs()
            .lock()
            .map_err(|_| "RAR install state is unavailable".to_string())?;
        prune_pending_installs(&mut pending);
        pending.insert(
            token.clone(),
            PendingRarInstall {
                created_at: SystemTime::now(),
                installer_path: installer_path.clone(),
            },
        );
    }

    Ok(RarInstallPlan {
        confirmation_token: token,
        download_url: DOWNLOAD_URL.to_string(),
        file_name: installer_path
            .file_name()
            .map(|name| name.to_string_lossy().to_string())
            .unwrap_or_else(|| "rar-installer".to_string()),
        installer_path: installer_path.to_string_lossy().to_string(),
        publisher,
        sha256,
    })
}

pub fn discard_rar_install(confirmation_token: String) -> Result<(), String> {
    if confirmation_token.trim().is_empty() {
        return Ok(());
    }

    let mut pending = pending_installs()
        .lock()
        .map_err(|_| "RAR install state is unavailable".to_string())?;
    if let Some(install) = pending.remove(&confirmation_token) {
        let _ = std::fs::remove_file(install.installer_path);
    }
    Ok(())
}

pub fn install_rar(confirmation_token: String) -> Result<String, String> {
    if confirmation_token.trim().is_empty() {
        return Err("RAR installation requires explicit user confirmation.".to_string());
    }

    let pending = take_pending_install(&confirmation_token)?;
    let result = install_rar_windows(&pending.installer_path);
    let _ = std::fs::remove_file(&pending.installer_path);
    result
}

fn pending_installs() -> &'static Mutex<HashMap<String, PendingRarInstall>> {
    static PENDING: OnceLock<Mutex<HashMap<String, PendingRarInstall>>> = OnceLock::new();
    PENDING.get_or_init(|| Mutex::new(HashMap::new()))
}

fn take_pending_install(token: &str) -> Result<PendingRarInstall, String> {
    let mut pending = pending_installs()
        .lock()
        .map_err(|_| "RAR install state is unavailable".to_string())?;
    prune_pending_installs(&mut pending);
    pending.remove(token).ok_or_else(|| {
        "RAR installation confirmation expired or was not prepared. Try Install RAR again."
            .to_string()
    })
}

fn prune_pending_installs(pending: &mut HashMap<String, PendingRarInstall>) {
    let now = SystemTime::now();
    let expired: Vec<String> = pending
        .iter()
        .filter(|(_, install)| {
            now.duration_since(install.created_at)
                .map(|age| age > PENDING_INSTALL_TTL)
                .unwrap_or(true)
        })
        .map(|(token, _)| token.clone())
        .collect();

    for token in expired {
        if let Some(install) = pending.remove(&token) {
            let _ = std::fs::remove_file(install.installer_path);
        }
    }
}

fn install_rar_windows(installer_path: &Path) -> Result<String, String> {
    let status = hidden_command(installer_path.as_os_str())
        .arg("/S")
        .status()
        .map_err(|error| format!("Failed to run WinRAR installer: {error}"))?;

    if !status.success() {
        return Err(format!(
            "WinRAR installer exited with code {}",
            status.code().unwrap_or(-1)
        ));
    }

    Ok(crate::archive::resolve_rar_binary().unwrap_or_else(|| {
        "WinRAR installed successfully. Restart the app if RAR creation does not work immediately."
            .to_string()
    }))
}

fn download_file(url: &str, path: &Path) -> Result<(), String> {
    let script = r#"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -Uri $args[0] -OutFile $args[1]
"#;
    let output = hidden_command("powershell")
        .arg("-NoProfile")
        .arg("-NonInteractive")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-Command")
        .arg(script)
        .arg(url)
        .arg(path)
        .output()
        .map_err(|error| format!("Failed to download WinRAR installer: {error}"))?;

    if output.status.success() {
        Ok(())
    } else {
        let detail = command_detail(&output);
        Err(format!("Failed to download WinRAR installer: {detail}"))
    }
}

fn verify_sha256_file(path: &Path) -> Result<String, String> {
    let bytes =
        std::fs::read(path).map_err(|error| format!("Failed to read WinRAR installer: {error}"))?;
    let mut hasher = Sha256::new();
    hasher.update(bytes);
    let actual = hex_encode(&hasher.finalize());
    if actual != EXPECTED_DOWNLOAD_SHA256 {
        return Err(format!(
            "Downloaded RAR artifact SHA-256 mismatch. Expected {EXPECTED_DOWNLOAD_SHA256}, got {actual}."
        ));
    }
    Ok(actual)
}

fn verify_windows_authenticode(path: &Path) -> Result<String, String> {
    let script = r#"
$ErrorActionPreference = 'Stop'
$signature = Get-AuthenticodeSignature -LiteralPath $args[0]
if ($signature.Status -ne 'Valid') {
  throw "Authenticode signature is $($signature.Status): $($signature.StatusMessage)"
}
$subject = $signature.SignerCertificate.Subject
if ($subject -notlike '*CN=win.rar GmbH*' -or $subject -notlike '*O=win.rar GmbH*') {
  throw "Unexpected installer publisher: $subject"
}
$subject
"#;
    let output = hidden_command("powershell")
        .arg("-NoProfile")
        .arg("-NonInteractive")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-Command")
        .arg(script)
        .arg(path)
        .output()
        .map_err(|error| format!("Failed to verify WinRAR Authenticode signature: {error}"))?;

    if !output.status.success() {
        return Err(format!(
            "WinRAR Authenticode verification failed: {}",
            command_detail(&output)
        ));
    }

    let subject = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if subject.is_empty() {
        return Err("WinRAR Authenticode verification did not return a signer.".to_string());
    }
    Ok(subject)
}

fn command_detail(output: &std::process::Output) -> String {
    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if !stderr.is_empty() {
        stderr
    } else if !stdout.is_empty() {
        stdout
    } else {
        "PowerShell returned an error without details".to_string()
    }
}

fn generate_confirmation_token() -> Result<String, String> {
    let mut bytes = [0u8; 16];
    getrandom::fill(&mut bytes)
        .map_err(|error| format!("Failed to create confirmation token: {error}"))?;
    Ok(hex_encode(&bytes))
}

fn pending_installer_path(token: &str) -> Result<PathBuf, String> {
    let source_name = DOWNLOAD_URL
        .rsplit('/')
        .next()
        .filter(|value| !value.trim().is_empty())
        .unwrap_or("rar-installer.download");
    let filename = format!("simplefile-rar-installer-{token}-{source_name}");
    Ok(std::env::temp_dir().join(filename))
}

fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        output.push(HEX[(byte >> 4) as usize] as char);
        output.push(HEX[(byte & 0x0f) as usize] as char);
    }
    output
}
