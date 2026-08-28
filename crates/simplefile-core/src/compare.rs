use crate::utils::validate_existing_path_no_resolve;
use serde::Serialize;
use std::fs;
use std::path::{Path, PathBuf};

const MAX_COMPARE_BYTES: u64 = 2 * 1024 * 1024;
const MAX_COMPARE_LINES: usize = 2_000;

#[derive(Debug, Serialize)]
pub struct DiffRow {
    pub kind: String,
    pub left_line: Option<usize>,
    pub right_line: Option<usize>,
    pub left_text: Option<String>,
    pub right_text: Option<String>,
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
    pub rows: Vec<DiffRow>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum DiffOp {
    Equal(usize, usize),
    Delete(usize),
    Insert(usize),
}

pub fn compare_files(path_a: String, path_b: String) -> Result<FileComparison, String> {
    let left_path = resolve_readable_path(&path_a)?;
    let right_path = resolve_readable_path(&path_b)?;

    let (left_text, left_size) = read_text_file(&left_path)?;
    let (right_text, right_size) = read_text_file(&right_path)?;

    let left_lines = split_lines(&left_text);
    let right_lines = split_lines(&right_text);

    if left_lines.len() > MAX_COMPARE_LINES || right_lines.len() > MAX_COMPARE_LINES {
        return Err(format!(
            "File comparison supports up to {MAX_COMPARE_LINES} lines per file."
        ));
    }

    let ops = build_diff_ops(&left_lines, &right_lines);
    let (rows, added, removed, changed) = build_rows(&ops, &left_lines, &right_lines);

    Ok(FileComparison {
        left_path: path_a,
        right_path: path_b,
        left_name: file_name(&left_path),
        right_name: file_name(&right_path),
        left_size,
        right_size,
        identical: added == 0 && removed == 0 && changed == 0,
        added,
        removed,
        changed,
        rows,
    })
}

fn resolve_readable_path(path: &str) -> Result<PathBuf, String> {
    if crate::archive::is_archive_virtual_path(path) {
        return crate::archive::materialize_archive_entry_to_temp(path);
    }

    validate_existing_path_no_resolve(path)
}

fn read_text_file(path: &Path) -> Result<(String, u64), String> {
    let metadata = fs::metadata(path).map_err(|e| format!("Failed to read metadata: {e}"))?;
    if metadata.is_dir() {
        return Err("File comparison is available for files, not folders.".to_string());
    }
    if metadata.len() > MAX_COMPARE_BYTES {
        return Err(format!(
            "File comparison supports text files up to {} MiB.",
            MAX_COMPARE_BYTES / 1024 / 1024
        ));
    }

    let bytes = fs::read(path).map_err(|e| format!("Failed to read file: {e}"))?;
    if crate::native_accel::contains_zero_byte(&bytes) {
        return Err("File comparison is available for text files, not binary files.".to_string());
    }

    String::from_utf8(bytes)
        .map(|text| (text, metadata.len()))
        .map_err(|_| "File comparison supports UTF-8 text files.".to_string())
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

#[cfg(test)]
mod tests {
    use super::{build_diff_ops, build_rows, read_text_file};
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
    fn read_text_file_rejects_nul_bytes_as_binary() {
        let path = unique_temp_path("binary");
        fs::write(&path, b"text before\0text after").unwrap();

        let err = read_text_file(&path).unwrap_err();

        assert_eq!(
            err,
            "File comparison is available for text files, not binary files."
        );
        let _ = fs::remove_file(path);
    }
}
