//! Entries of a `.7z` as `7za l -slt` reports them, and the entry-name
//! rules every listed name passes before anything is written.

/// One entry of a `7za l -slt` listing. `path` is as 7-Zip printed it,
/// `\`-separated on Windows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SevenZipEntry {
    pub path: String,
    pub is_dir: bool,
    pub size: u64,
    pub compressed_size: u64,
}

#[derive(Default)]
struct EntryBlock {
    path: Option<String>,
    size: Option<u64>,
    compressed_size: Option<u64>,
    attributes: Option<String>,
}

/// Parses the technical listing (`l -slt`): one `Key = Value` block per
/// entry after the `----------` line that ends the archive's own block.
pub fn parse_list_output(output: &str) -> Vec<SevenZipEntry> {
    let mut entries = Vec::new();
    let mut block = EntryBlock::default();
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

fn push_entry_block(block: &mut EntryBlock, entries: &mut Vec<SevenZipEntry>) {
    let finished = std::mem::take(block);
    let Some(path) = finished.path else {
        return;
    };
    let attributes = finished.attributes.unwrap_or_default();
    let is_dir = attributes.contains('D') || path.ends_with(['\\', '/']);
    entries.push(SevenZipEntry {
        path,
        is_dir,
        size: finished.size.unwrap_or(0),
        compressed_size: finished.compressed_size.unwrap_or(0),
    });
}

fn parse_u64(value: &str) -> Option<u64> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        None
    } else {
        trimmed.parse().ok()
    }
}

/// Checks an entry name and returns it `/`-separated, without empty or `.`
/// segments. The rules are core's: no NUL, not rooted, and every segment is
/// a valid Windows file name that is not `..` and holds no `:`. Fails with
/// `7-Zip entry has unsafe path: <name>`.
pub fn validate_entry_path(name: &str) -> Result<String, String> {
    let unsafe_path = || format!("7-Zip entry has unsafe path: {name}");
    if name.contains('\0') {
        return Err(unsafe_path());
    }
    let normalized = name.replace('\\', "/");
    if normalized.starts_with('/') {
        return Err(unsafe_path());
    }
    let mut segments = Vec::new();
    for segment in normalized.split('/') {
        if segment.is_empty() || segment == "." {
            continue;
        }
        if segment == ".." || segment.contains(':') || !is_valid_windows_name(segment) {
            return Err(unsafe_path());
        }
        segments.push(segment);
    }
    if segments.is_empty() {
        return Err(unsafe_path());
    }
    Ok(segments.join("/"))
}

/// Core's `utils::validate_name`: not blank, not ending in a space or
/// period, no reserved or control characters, not a device name.
fn is_valid_windows_name(name: &str) -> bool {
    if name.trim().is_empty() || name.ends_with([' ', '.']) {
        return false;
    }
    if name.chars().any(|ch| {
        ch.is_control() || matches!(ch, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*')
    }) {
        return false;
    }
    !is_reserved_device_name(name)
}

fn is_reserved_device_name(name: &str) -> bool {
    let base = name
        .split('.')
        .next()
        .unwrap_or(name)
        .trim_end_matches(' ')
        .to_ascii_uppercase();
    if matches!(base.as_str(), "CON" | "PRN" | "AUX" | "NUL") {
        return true;
    }
    let numbered = |prefix: &str| {
        base.strip_prefix(prefix).is_some_and(|digit| {
            matches!(digit, "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9")
        })
    };
    numbered("COM") || numbered("LPT")
}

/// True when `entry` is `prefix` or sits under it, by whole `/` segments:
/// `docs` never matches `docs2`.
pub fn is_within(entry: &str, prefix: &str) -> bool {
    entry == prefix
        || entry
            .strip_prefix(prefix)
            .is_some_and(|rest| rest.starts_with('/'))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parser_reads_files_and_directories() {
        let output = r#"
7-Zip (a) 26.03 (x64) : Copyright (c) 1999-2026 Igor Pavlov : 2026-09-03

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

        let entries = parse_list_output(output);

        assert_eq!(entries.len(), 3);
        assert!(entries[0].is_dir);
        assert_eq!(entries[0].path, "folder");
        assert_eq!(entries[1].path, "alpha.txt");
        assert!(!entries[1].is_dir);
        assert_eq!(entries[1].size, 5);
        assert_eq!(entries[1].compressed_size, 14);
        assert_eq!(entries[2].path, r"folder\beta.txt");
        assert_eq!(entries[2].compressed_size, 0);
    }

    #[test]
    fn validate_entry_path_rejects_unsafe_names() {
        for name in ["../x", "/abs", "C:x", "a/../b", "CON", "a\0b", "dot.", ""] {
            assert_eq!(
                validate_entry_path(name),
                Err(format!("7-Zip entry has unsafe path: {name}")),
                "{name:?} should be rejected"
            );
        }
        assert_eq!(
            validate_entry_path("café 資料/hello.txt"),
            Ok("café 資料/hello.txt".to_string())
        );
        assert_eq!(
            validate_entry_path(r"folder\beta.txt"),
            Ok("folder/beta.txt".to_string())
        );
        assert_eq!(validate_entry_path("docs/"), Ok("docs".to_string()));
    }

    #[test]
    fn is_within_is_segment_aware() {
        assert!(is_within("docs/a", "docs"));
        assert!(!is_within("docs2/a", "docs"));
        assert!(is_within("docs", "docs"));
        assert!(!is_within("doc", "docs"));
    }
}
