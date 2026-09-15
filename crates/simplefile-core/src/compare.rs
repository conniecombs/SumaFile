use crate::utils::resolve_readable_path;
use serde::Serialize;
use std::fs;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

const MAX_COMPARE_BYTES: u64 = 2 * 1024 * 1024;
const MAX_COMPARE_LINES: usize = 2_000;
const BINARY_ROW_BYTES: usize = 16;
const BINARY_READ_CHUNK: usize = 64 * 1024;
const MAX_BINARY_DIFF_ROWS: usize = 128;

#[derive(Debug, Serialize)]
pub struct DiffRow {
    pub kind: String,
    pub left_line: Option<usize>,
    pub right_line: Option<usize>,
    pub left_text: Option<String>,
    pub right_text: Option<String>,
}

#[derive(Debug, Serialize)]
pub struct BinaryDiffRow {
    pub offset: u64,
    pub left_hex: String,
    pub right_hex: String,
    pub left_ascii: String,
    pub right_ascii: String,
    pub different: bool,
}

#[derive(Debug, Serialize)]
pub struct FileComparison {
    pub left_path: String,
    pub right_path: String,
    pub left_name: String,
    pub right_name: String,
    pub left_size: u64,
    pub right_size: u64,
    pub identical: bool,
    pub added: usize,
    pub removed: usize,
    pub changed: usize,
    pub comparison_type: String,
    pub compared_bytes: Option<u64>,
    pub different_bytes: Option<u64>,
    pub first_difference: Option<u64>,
    pub binary_rows_truncated: bool,
    pub rows: Vec<DiffRow>,
    pub binary_rows: Vec<BinaryDiffRow>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DiffOp {
    Equal(usize, usize),
    Delete(usize),
    Insert(usize),
}

enum CompareInput {
    Text { text: String, size: u64 },
    Binary,
}

#[derive(Default)]
struct BinaryScan {
    changed_bytes: u64,
    added_bytes: u64,
    removed_bytes: u64,
    first_difference: Option<u64>,
    row_offsets: Vec<u64>,
    rows_truncated: bool,
}

pub fn compare_files(path_a: String, path_b: String) -> Result<FileComparison, String> {
    let left_path = resolve_readable_path(&path_a)?;
    let right_path = resolve_readable_path(&path_b)?;

    let left = read_compare_input(&left_path)?;
    let right = read_compare_input(&right_path)?;

    let (
        CompareInput::Text {
            text: left_text,
            size: left_size,
        },
        CompareInput::Text {
            text: right_text,
            size: right_size,
        },
    ) = (&left, &right)
    else {
        return compare_binary_files(path_a, path_b, &left_path, &right_path);
    };

    let left_lines = split_lines(left_text);
    let right_lines = split_lines(right_text);

    if left_lines.len() > MAX_COMPARE_LINES || right_lines.len() > MAX_COMPARE_LINES {
        return compare_binary_files(path_a, path_b, &left_path, &right_path);
    }

    let ops = build_diff_ops(&left_lines, &right_lines);
    let (rows, added, removed, changed) = build_rows(&ops, &left_lines, &right_lines);

    Ok(FileComparison {
        left_path: path_a,
        right_path: path_b,
        left_name: file_name(&left_path),
        right_name: file_name(&right_path),
        left_size: *left_size,
        right_size: *right_size,
        identical: added == 0 && removed == 0 && changed == 0,
        added,
        removed,
        changed,
        comparison_type: "text".to_string(),
        compared_bytes: None,
        different_bytes: None,
        first_difference: None,
        binary_rows_truncated: false,
        rows,
        binary_rows: Vec::new(),
    })
}

fn read_compare_input(path: &Path) -> Result<CompareInput, String> {
    let metadata = fs::metadata(path).map_err(|e| format!("Failed to read metadata: {e}"))?;
    if metadata.is_dir() {
        return Err("File comparison is available for files, not folders.".to_string());
    }
    if metadata.len() > MAX_COMPARE_BYTES {
        return Ok(CompareInput::Binary);
    }

    let bytes = fs::read(path).map_err(|e| format!("Failed to read file: {e}"))?;
    if crate::native_accel::contains_zero_byte(&bytes) {
        return Ok(CompareInput::Binary);
    }

    match String::from_utf8(bytes) {
        Ok(text) => Ok(CompareInput::Text {
            text,
            size: metadata.len(),
        }),
        Err(_) => Ok(CompareInput::Binary),
    }
}

fn split_lines(text: &str) -> Vec<String> {
    text.lines()
        .map(|line| line.trim_end_matches('\r').to_string())
        .collect()
}

fn file_name(path: &Path) -> String {
    path.file_name().map_or_else(
        || path.to_string_lossy().to_string(),
        |name| name.to_string_lossy().to_string(),
    )
}

fn build_diff_ops(left: &[String], right: &[String]) -> Vec<DiffOp> {
    let left_len = left.len();
    let right_len = right.len();
    let width = right_len + 1;
    let mut table = vec![0usize; (left_len + 1) * width];

    for i in (0..left_len).rev() {
        for j in (0..right_len).rev() {
            let idx = i * width + j;
            table[idx] = if left[i] == right[j] {
                1 + table[(i + 1) * width + j + 1]
            } else {
                table[(i + 1) * width + j].max(table[i * width + j + 1])
            };
        }
    }

    let mut ops = Vec::new();
    let mut i = 0usize;
    let mut j = 0usize;
    while i < left_len && j < right_len {
        if left[i] == right[j] {
            ops.push(DiffOp::Equal(i, j));
            i += 1;
            j += 1;
        } else if table[(i + 1) * width + j] >= table[i * width + j + 1] {
            ops.push(DiffOp::Delete(i));
            i += 1;
        } else {
            ops.push(DiffOp::Insert(j));
            j += 1;
        }
    }

    while i < left_len {
        ops.push(DiffOp::Delete(i));
        i += 1;
    }
    while j < right_len {
        ops.push(DiffOp::Insert(j));
        j += 1;
    }

    ops
}

fn build_rows(
    ops: &[DiffOp],
    left: &[String],
    right: &[String],
) -> (Vec<DiffRow>, usize, usize, usize) {
    let mut rows = Vec::new();
    let mut added = 0usize;
    let mut removed = 0usize;
    let mut changed = 0usize;
    let mut index = 0usize;

    while index < ops.len() {
        match ops[index] {
            DiffOp::Equal(left_idx, right_idx) => {
                rows.push(DiffRow {
                    kind: "equal".to_string(),
                    left_line: Some(left_idx + 1),
                    right_line: Some(right_idx + 1),
                    left_text: Some(left[left_idx].clone()),
                    right_text: Some(right[right_idx].clone()),
                });
                index += 1;
            }
            DiffOp::Delete(_) | DiffOp::Insert(_) => {
                let mut deletes = Vec::new();
                let mut inserts = Vec::new();

                while index < ops.len() {
                    match ops[index] {
                        DiffOp::Delete(left_idx) => deletes.push(left_idx),
                        DiffOp::Insert(right_idx) => inserts.push(right_idx),
                        DiffOp::Equal(_, _) => break,
                    }
                    index += 1;
                }

                let row_count = deletes.len().max(inserts.len());
                for offset in 0..row_count {
                    match (deletes.get(offset), inserts.get(offset)) {
                        (Some(&left_idx), Some(&right_idx)) => {
                            changed += 1;
                            rows.push(DiffRow {
                                kind: "modified".to_string(),
                                left_line: Some(left_idx + 1),
                                right_line: Some(right_idx + 1),
                                left_text: Some(left[left_idx].clone()),
                                right_text: Some(right[right_idx].clone()),
                            });
                        }
                        (Some(&left_idx), None) => {
                            removed += 1;
                            rows.push(DiffRow {
                                kind: "removed".to_string(),
                                left_line: Some(left_idx + 1),
                                right_line: None,
                                left_text: Some(left[left_idx].clone()),
                                right_text: None,
                            });
                        }
                        (None, Some(&right_idx)) => {
                            added += 1;
                            rows.push(DiffRow {
                                kind: "added".to_string(),
                                left_line: None,
                                right_line: Some(right_idx + 1),
                                left_text: None,
                                right_text: Some(right[right_idx].clone()),
                            });
                        }
                        (None, None) => {}
                    }
                }
            }
        }
    }

    (rows, added, removed, changed)
}

fn compare_binary_files(
    left_display_path: String,
    right_display_path: String,
    left_path: &Path,
    right_path: &Path,
) -> Result<FileComparison, String> {
    let left_size = binary_file_size(left_path)?;
    let right_size = binary_file_size(right_path)?;
    let scan = scan_binary_differences(left_path, right_path)?;
    let different_bytes = scan.changed_bytes + scan.added_bytes + scan.removed_bytes;
    let binary_rows = scan
        .row_offsets
        .iter()
        .map(|offset| build_binary_diff_row(left_path, right_path, *offset))
        .collect::<Result<Vec<_>, _>>()?;

    Ok(FileComparison {
        left_path: left_display_path,
        right_path: right_display_path,
        left_name: file_name(left_path),
        right_name: file_name(right_path),
        left_size,
        right_size,
        identical: different_bytes == 0,
        added: cap_legacy_count(scan.added_bytes),
        removed: cap_legacy_count(scan.removed_bytes),
        changed: cap_legacy_count(scan.changed_bytes),
        comparison_type: "binary".to_string(),
        compared_bytes: Some(left_size.max(right_size)),
        different_bytes: Some(different_bytes),
        first_difference: scan.first_difference,
        binary_rows_truncated: scan.rows_truncated,
        rows: Vec::new(),
        binary_rows,
    })
}

fn binary_file_size(path: &Path) -> Result<u64, String> {
    let metadata = fs::metadata(path).map_err(|e| format!("Failed to read metadata: {e}"))?;
    if metadata.is_dir() {
        return Err("File comparison is available for files, not folders.".to_string());
    }

    Ok(metadata.len())
}

fn scan_binary_differences(left_path: &Path, right_path: &Path) -> Result<BinaryScan, String> {
    let mut left_file =
        fs::File::open(left_path).map_err(|e| format!("Failed to read left file: {e}"))?;
    let mut right_file =
        fs::File::open(right_path).map_err(|e| format!("Failed to read right file: {e}"))?;
    let mut left = vec![0u8; BINARY_READ_CHUNK];
    let mut right = vec![0u8; BINARY_READ_CHUNK];
    let mut offset = 0u64;
    let mut scan = BinaryScan::default();

    loop {
        let left_read = read_full_chunk(&mut left_file, &mut left)
            .map_err(|e| format!("Failed to read left file: {e}"))?;
        let right_read = read_full_chunk(&mut right_file, &mut right)
            .map_err(|e| format!("Failed to read right file: {e}"))?;
        if left_read == 0 && right_read == 0 {
            break;
        }

        let common = left_read.min(right_read);
        if left[..common] != right[..common] {
            for index in 0..common {
                if left[index] != right[index] {
                    scan.changed_bytes += 1;
                    record_binary_difference(&mut scan, offset + index as u64);
                }
            }
        }

        if left_read > common {
            scan.removed_bytes += (left_read - common) as u64;
            for index in common..left_read {
                record_binary_difference(&mut scan, offset + index as u64);
            }
        }

        if right_read > common {
            scan.added_bytes += (right_read - common) as u64;
            for index in common..right_read {
                record_binary_difference(&mut scan, offset + index as u64);
            }
        }

        offset += left_read.max(right_read) as u64;
    }

    Ok(scan)
}

fn read_full_chunk(file: &mut fs::File, buffer: &mut [u8]) -> std::io::Result<usize> {
    let mut filled = 0usize;
    while filled < buffer.len() {
        let read = file.read(&mut buffer[filled..])?;
        if read == 0 {
            break;
        }

        filled += read;
    }

    Ok(filled)
}

fn record_binary_difference(scan: &mut BinaryScan, offset: u64) {
    scan.first_difference.get_or_insert(offset);
    let row_offset = offset - (offset % BINARY_ROW_BYTES as u64);
    if scan.row_offsets.last() == Some(&row_offset) || scan.row_offsets.contains(&row_offset) {
        return;
    }

    if scan.row_offsets.len() >= MAX_BINARY_DIFF_ROWS {
        scan.rows_truncated = true;
        return;
    }

    scan.row_offsets.push(row_offset);
}

fn build_binary_diff_row(
    left_path: &Path,
    right_path: &Path,
    offset: u64,
) -> Result<BinaryDiffRow, String> {
    let left = read_binary_row(left_path, offset)?;
    let right = read_binary_row(right_path, offset)?;
    Ok(BinaryDiffRow {
        offset,
        left_hex: format_hex_row(&left),
        right_hex: format_hex_row(&right),
        left_ascii: format_ascii_row(&left),
        right_ascii: format_ascii_row(&right),
        different: left != right,
    })
}

fn read_binary_row(path: &Path, offset: u64) -> Result<Vec<u8>, String> {
    let mut file = fs::File::open(path).map_err(|e| format!("Failed to read file: {e}"))?;
    file.seek(SeekFrom::Start(offset))
        .map_err(|e| format!("Failed to seek file: {e}"))?;
    let mut buffer = vec![0u8; BINARY_ROW_BYTES];
    let read =
        read_full_chunk(&mut file, &mut buffer).map_err(|e| format!("Failed to read file: {e}"))?;
    buffer.truncate(read);
    Ok(buffer)
}

fn format_hex_row(bytes: &[u8]) -> String {
    let mut values = Vec::with_capacity(BINARY_ROW_BYTES);
    for index in 0..BINARY_ROW_BYTES {
        if let Some(byte) = bytes.get(index) {
            values.push(format!("{byte:02X}"));
        } else {
            values.push("  ".to_string());
        }
    }

    values.join(" ")
}

fn format_ascii_row(bytes: &[u8]) -> String {
    let mut text = String::with_capacity(BINARY_ROW_BYTES);
    for byte in bytes {
        let value = if byte.is_ascii_graphic() || *byte == b' ' {
            *byte as char
        } else {
            '.'
        };
        text.push(value);
    }

    while text.len() < BINARY_ROW_BYTES {
        text.push(' ');
    }

    text
}

fn cap_legacy_count(value: u64) -> usize {
    value.min(i32::MAX as u64) as usize
}

#[cfg(test)]
mod tests {
    use super::{build_diff_ops, build_rows, compare_files, MAX_COMPARE_BYTES};
    use std::fs;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn lines(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| value.to_string()).collect()
    }

    fn unique_temp_path(name: &str) -> PathBuf {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("simplefile_compare_test_{}_{}", name, nanos))
    }

    #[test]
    fn pairs_adjacent_delete_and_insert_as_modified() {
        let left = lines(&["alpha", "bravo", "charlie"]);
        let right = lines(&["alpha", "beta", "charlie"]);
        let ops = build_diff_ops(&left, &right);
        let (rows, added, removed, changed) = build_rows(&ops, &left, &right);

        assert_eq!(added, 0);
        assert_eq!(removed, 0);
        assert_eq!(changed, 1);
        assert_eq!(rows[1].kind, "modified");
        assert_eq!(rows[1].left_text.as_deref(), Some("bravo"));
        assert_eq!(rows[1].right_text.as_deref(), Some("beta"));
    }

    #[test]
    fn records_unpaired_inserts_and_deletes() {
        let left = lines(&["alpha", "bravo", "charlie"]);
        let right = lines(&["alpha", "charlie", "delta"]);
        let ops = build_diff_ops(&left, &right);
        let (rows, added, removed, changed) = build_rows(&ops, &left, &right);

        assert_eq!(added, 1);
        assert_eq!(removed, 1);
        assert_eq!(changed, 0);
        assert!(rows.iter().any(|row| row.kind == "removed"));
        assert!(rows.iter().any(|row| row.kind == "added"));
    }

    #[test]
    fn compare_files_keeps_text_diff_for_utf8_text() {
        let left = unique_temp_path("left-text");
        let right = unique_temp_path("right-text");
        fs::write(&left, b"alpha\nbravo\n").unwrap();
        fs::write(&right, b"alpha\nbeta\n").unwrap();

        let comparison = compare_files(
            left.to_string_lossy().to_string(),
            right.to_string_lossy().to_string(),
        )
        .unwrap();

        assert_eq!(comparison.comparison_type, "text");
        assert_eq!(comparison.changed, 1);
        assert_eq!(comparison.different_bytes, None);
        assert!(comparison.binary_rows.is_empty());

        let _ = fs::remove_file(left);
        let _ = fs::remove_file(right);
    }

    #[test]
    fn compare_files_returns_binary_hex_rows_for_binary_inputs() {
        let left = unique_temp_path("left-binary");
        let right = unique_temp_path("right-binary");
        let left_bytes: Vec<u8> = (0u8..32).collect();
        let mut right_bytes = left_bytes.clone();
        right_bytes[5] = 0xFF;
        fs::write(&left, &left_bytes).unwrap();
        fs::write(&right, &right_bytes).unwrap();

        let comparison = compare_files(
            left.to_string_lossy().to_string(),
            right.to_string_lossy().to_string(),
        )
        .unwrap();

        assert_eq!(comparison.comparison_type, "binary");
        assert!(!comparison.identical);
        assert_eq!(comparison.first_difference, Some(5));
        assert_eq!(comparison.different_bytes, Some(1));
        assert_eq!(comparison.changed, 1);
        assert_eq!(comparison.added, 0);
        assert_eq!(comparison.removed, 0);
        assert!(comparison.rows.is_empty());
        assert_eq!(comparison.binary_rows.len(), 1);
        assert_eq!(comparison.binary_rows[0].offset, 0);
        assert!(comparison.binary_rows[0]
            .left_hex
            .starts_with("00 01 02 03 04 05"));
        assert!(comparison.binary_rows[0]
            .right_hex
            .starts_with("00 01 02 03 04 FF"));

        let _ = fs::remove_file(left);
        let _ = fs::remove_file(right);
    }

    #[test]
    fn compare_files_reports_binary_size_differences() {
        let left = unique_temp_path("left-short-binary");
        let right = unique_temp_path("right-long-binary");
        fs::write(&left, [0, 1]).unwrap();
        fs::write(&right, [0, 1, 2, 3]).unwrap();

        let comparison = compare_files(
            left.to_string_lossy().to_string(),
            right.to_string_lossy().to_string(),
        )
        .unwrap();

        assert_eq!(comparison.comparison_type, "binary");
        assert_eq!(comparison.first_difference, Some(2));
        assert_eq!(comparison.different_bytes, Some(2));
        assert_eq!(comparison.added, 2);
        assert_eq!(comparison.removed, 0);
        assert_eq!(comparison.changed, 0);
        assert_eq!(comparison.binary_rows[0].offset, 0);

        let _ = fs::remove_file(left);
        let _ = fs::remove_file(right);
    }

    #[test]
    fn compare_files_falls_back_to_binary_for_oversized_text() {
        let left = unique_temp_path("left-large-text");
        let right = unique_temp_path("right-large-text");
        let left_bytes = vec![b'a'; MAX_COMPARE_BYTES as usize + 1];
        let mut right_bytes = left_bytes.clone();
        right_bytes[MAX_COMPARE_BYTES as usize] = b'b';
        fs::write(&left, &left_bytes).unwrap();
        fs::write(&right, &right_bytes).unwrap();

        let comparison = compare_files(
            left.to_string_lossy().to_string(),
            right.to_string_lossy().to_string(),
        )
        .unwrap();

        assert_eq!(comparison.comparison_type, "binary");
        assert_eq!(comparison.first_difference, Some(MAX_COMPARE_BYTES));
        assert_eq!(comparison.different_bytes, Some(1));
        assert_eq!(
            comparison.binary_rows[0].offset,
            MAX_COMPARE_BYTES - (MAX_COMPARE_BYTES % 16)
        );

        let _ = fs::remove_file(left);
        let _ = fs::remove_file(right);
    }
}
