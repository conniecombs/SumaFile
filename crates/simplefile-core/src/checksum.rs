use crate::utils::{hex_encode, resolve_readable_path};
use md5::Md5;
use sha1::Sha1;
use sha2::{Digest, Sha256};
use std::io::Read;

#[derive(Debug, PartialEq, Eq)]
struct Checksums {
    md5: String,
    sha1: String,
    sha256: String,
}

/// Compute MD5, SHA-1, and SHA-256 checksums for a file.
/// Returns a JSON object with "md5", "sha1", and "sha256" hex strings.
pub fn compute_checksum(path: String) -> Result<serde_json::Value, String> {
    let path_buf = resolve_readable_path(&path)?;
    if path_buf.is_dir() {
        return Err("Cannot compute checksum for a directory".to_string());
    }

    let mut file =
        std::fs::File::open(&path_buf).map_err(|e| format!("Failed to open file: {e}"))?;
    let hashes = compute_checksums(&mut file).map_err(|e| format!("Failed to read file: {e}"))?;

    Ok(serde_json::json!({
        "md5": hashes.md5,
        "sha1": hashes.sha1,
        "sha256": hashes.sha256,
    }))
}

fn compute_checksums<R: Read>(reader: &mut R) -> Result<Checksums, std::io::Error> {
    let mut md5_hasher = Md5::new();
    let mut sha1_hasher = Sha1::new();
    let mut sha256_hasher = Sha256::new();

    let mut buffer = [0u8; 65536];
    loop {
        let n = reader.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        let chunk = &buffer[..n];
        md5_hasher.update(chunk);
        sha1_hasher.update(chunk);
        sha256_hasher.update(chunk);
    }

    Ok(Checksums {
        md5: hex_encode(md5_hasher.finalize()),
        sha1: hex_encode(sha1_hasher.finalize()),
        sha256: hex_encode(sha256_hasher.finalize()),
    })
}

#[cfg(test)]
mod tests {
    use super::{compute_checksums, Checksums};

    #[test]
    fn computes_known_hash_vector_for_abc() {
        let mut input = &b"abc"[..];
        let hashes = compute_checksums(&mut input).unwrap();

        assert_eq!(
            hashes,
            Checksums {
                md5: "900150983cd24fb0d6963f7d28e17f72".to_string(),
                sha1: "a9993e364706816aba3e25717850c26c9cd0d89d".to_string(),
                sha256: "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                    .to_string(),
            }
        );
    }
}
