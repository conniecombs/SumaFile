use crate::utils::validate_existing_path_no_resolve;
use std::path::Path;
use std::process::Command;

pub fn open_terminal(path: String) -> Result<(), String> {
    let directory = validate_terminal_directory(&path)?;
    let escaped_path = directory.to_string_lossy().replace('\'', "''");

    let mut command = Command::new("powershell");
    command
        .arg("-NoExit")
        .arg("-Command")
        .arg(format!("Set-Location -LiteralPath '{escaped_path}'"));
    spawn_terminal(&mut command)
}

pub fn open_powershell_admin(path: String) -> Result<(), String> {
    let directory = validate_terminal_directory(&path)?;
    let encoded = powershell_encoded_command(&directory);

    let mut command = Command::new("powershell");
    command
        .arg("-NoProfile")
        .arg("-ExecutionPolicy")
        .arg("Bypass")
        .arg("-EncodedCommand")
        .arg(encoded);
    spawn_terminal(&mut command)
}

fn validate_terminal_directory(path: &str) -> Result<std::path::PathBuf, String> {
    let path = validate_existing_path_no_resolve(path)?;
    if !path.is_dir() {
        return Err("Terminal can only be opened for directories".to_string());
    }
    Ok(path)
}

fn spawn_terminal(command: &mut Command) -> Result<(), String> {
    command
        .spawn()
        .map(|_| ())
        .map_err(|error| format!("Failed to open terminal: {error}"))
}

fn powershell_encoded_command(path: &Path) -> String {
    use base64::Engine;

    let escaped_path = path.to_string_lossy().replace('\'', "''");
    let command = format!(
        "Start-Process PowerShell -Verb RunAs -ArgumentList '-NoExit','-Command','Set-Location -LiteralPath ''{escaped_path}'''"
    );
    let utf16le: Vec<u8> = command
        .encode_utf16()
        .flat_map(|character| character.to_le_bytes())
        .collect();
    base64::engine::general_purpose::STANDARD.encode(utf16le)
}
