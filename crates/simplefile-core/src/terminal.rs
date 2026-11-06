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

    // The path sits inside two single-quoted PowerShell strings: the inner
    // `-Command` literal, and the outer `-ArgumentList` element that carries
    // it. Each level needs its own doubling, so one quote becomes four.
    let inner_command = format!(
        "Set-Location -LiteralPath '{}'",
        path.to_string_lossy().replace('\'', "''")
    );
    let command = format!(
        "Start-Process PowerShell -Verb RunAs -ArgumentList '-NoExit','-Command','{}'",
        inner_command.replace('\'', "''")
    );
    let utf16le: Vec<u8> = command
        .encode_utf16()
        .flat_map(|character| character.to_le_bytes())
        .collect();
    base64::engine::general_purpose::STANDARD.encode(utf16le)
}

#[cfg(test)]
mod tests {
    use super::powershell_encoded_command;
    use base64::Engine;
    use std::path::Path;

    fn decode(encoded: &str) -> String {
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(encoded)
            .unwrap();
        let utf16: Vec<u16> = bytes
            .chunks(2)
            .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
            .collect();
        String::from_utf16(&utf16).unwrap()
    }

    #[test]
    fn admin_command_escapes_quotes_for_both_nesting_levels() {
        let command = decode(&powershell_encoded_command(Path::new(r"C:\x\a'b")));
        // The inner -Command literal needs '' for the quote; embedding that inside
        // the outer single-quoted -ArgumentList element doubles it again.
        assert_eq!(
            command,
            r"Start-Process PowerShell -Verb RunAs -ArgumentList '-NoExit','-Command','Set-Location -LiteralPath ''C:\x\a''''b'''"
        );
    }

    #[test]
    fn admin_command_leaves_plain_paths_alone() {
        let command = decode(&powershell_encoded_command(Path::new(r"C:\Users\me")));
        assert!(
            command.ends_with(r"'Set-Location -LiteralPath ''C:\Users\me'''"),
            "{command}"
        );
    }
}
