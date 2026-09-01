use std::process::{Output, Stdio};

#[derive(Debug, Clone, PartialEq, Eq)]
pub(super) struct SevenZipEntry {
    pub path: String,
    pub is_dir: bool,
    pub size: u64,
    pub compressed_size: u64,
}

#[derive(Default)]
struct SevenZipEntryBlock {
    path: Option<String>,
    size: Option<u64>,
    compressed_size: Option<u64>,
    attributes: Option<String>,
}

pub(super) fn resolve_seven_zip_binary() -> Option<String> {
    if let Ok(path) = std::env::var("SIMPLEFILE_7Z") {
        let trimmed = path.trim();
        if !trimmed.is_empty() && std::path::Path::new(trimmed).exists() {
            return Some(trimmed.to_string());
        }
    }

    for command in ["7z", "7za"] {
        if std::process::Command::new(command)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .output()
            .is_ok()
        {
            return Some(command.to_string());
        }
    }

    for path in [
        r"C:\Program Files\7-Zip\7z.exe",
        r"C:\Program Files (x86)\7-Zip\7z.exe",
    ] {
        if std::path::Path::new(path).exists() {
            return Some(path.to_string());
        }
    }

    None
}

pub(super) fn require_seven_zip_binary() -> Result<String, String> {
    resolve_seven_zip_binary().ok_or_else(|| {
        "7-Zip command not found. Install 7-Zip or set SIMPLEFILE_7Z to 7z.exe.".to_string()
    })
}

pub(super) fn list_seven_zip_entries(path: &str) -> Result<Vec<SevenZipEntry>, String> {
    let binary = require_seven_zip_binary()?;
    list_seven_zip_entries_with_binary(&binary, path)
}

pub(super) fn list_seven_zip_entries_with_binary(
    binary: &str,
    path: &str,
) -> Result<Vec<SevenZipEntry>, String> {
    let output = std::process::Command::new(binary)
        .arg("l")
        .arg("-slt")
        .arg("-bd")
        .arg("-bb0")
        .arg("-sccUTF-8")
        .arg("--")
        .arg(path)
        .stdin(Stdio::null())
        .output()
        .map_err(|e| format!("Failed to run 7-Zip list command: {e}"))?;

    ensure_seven_zip_success(&output, "7-Zip list")?;
    let stdout = String::from_utf8_lossy(&output.stdout);
    Ok(parse_seven_zip_list_output(&stdout))
}

pub(super) fn ensure_seven_zip_success(output: &Output, action: &str) -> Result<(), String> {
    let code = output.status.code().unwrap_or(2);
    if code <= 1 {
        return Ok(());
    }

    Err(format_seven_zip_error(output, action, code))
}

fn format_seven_zip_error(output: &Output, action: &str, code: i32) -> String {
    let stderr = String::from_utf8_lossy(&output.stderr);
    let stdout = String::from_utf8_lossy(&output.stdout);
    let mut detail = format!("{stderr}{stdout}")
        .lines()
        .map(str::trim_end)
        .filter(|line| !line.is_empty())
        .collect::<Vec<_>>()
        .join("\n");
    if detail.len() > 4000 {
        detail.truncate(4000);
        detail.push_str("\n...");
    }

    if detail.is_empty() {
        format!("{action} failed (exit code {code})")
    } else {
        format!("{action} failed: {detail}")
    }
}

pub(super) fn parse_seven_zip_list_output(output: &str) -> Vec<SevenZipEntry> {
    let mut entries = Vec::new();
    let mut block = SevenZipEntryBlock::default();
    let mut in_entries = false;

    for raw_line in output.lines() {
        let line = raw_line.trim_end_matches('\r');
        if !in_entries {
            if line.trim() == "----------" {
                in_entries = true;
            }
            continue;
        }

        if line.trim().is_empty() {
            push_entry_block(&mut block, &mut entries);
            continue;
        }

        let Some((key, value)) = line.split_once('=') else {
            continue;
        };
        let key = key.trim_end();
        let value = value.strip_prefix(' ').unwrap_or(value);

        match key {
            "Path" => block.path = Some(value.to_string()),
            "Size" => block.size = parse_u64(value),
            "Packed Size" => block.compressed_size = parse_u64(value),
            "Attributes" => block.attributes = Some(value.to_string()),
            _ => {}
        }
    }

    push_entry_block(&mut block, &mut entries);
    entries
}

fn push_entry_block(block: &mut SevenZipEntryBlock, entries: &mut Vec<SevenZipEntry>) {
    let Some(path) = block.path.take() else {
        *block = SevenZipEntryBlock::default();
        return;
    };

    let attributes = block.attributes.take().unwrap_or_default();
    let is_dir = attributes.contains('D') || path.ends_with(['\\', '/']);
    entries.push(SevenZipEntry {
        path,
        is_dir,
        size: block.size.take().unwrap_or(0),
        compressed_size: block.compressed_size.take().unwrap_or(0),
    });
    *block = SevenZipEntryBlock::default();
}

fn parse_u64(value: &str) -> Option<u64> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        trimmed.parse().ok()
    }
}
