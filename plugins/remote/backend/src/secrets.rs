use std::collections::HashMap;
use std::sync::Mutex;

#[cfg(windows)]
use std::{ptr, slice};
#[cfg(windows)]
use windows_sys::Win32::Foundation::ERROR_NOT_FOUND;
#[cfg(windows)]
use windows_sys::Win32::Security::Credentials::{
    CredDeleteW, CredFree, CredReadW, CredWriteW, CREDENTIALW, CRED_PERSIST_LOCAL_MACHINE,
    CRED_TYPE_GENERIC,
};

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RemoteSecret {
    Password(String),
    PrivateKeyPassphrase(String),
}

pub trait RemoteSecretStore: Send + Sync {
    fn write_secret(&self, target: &str, secret: &RemoteSecret) -> Result<(), String>;
    fn read_secret(&self, target: &str) -> Result<Option<RemoteSecret>, String>;
    fn delete_secret(&self, target: &str) -> Result<(), String>;
}

#[derive(Default)]
pub struct MemoryRemoteSecretStore {
    secrets: Mutex<HashMap<String, RemoteSecret>>,
}

impl RemoteSecretStore for MemoryRemoteSecretStore {
    fn write_secret(&self, target: &str, secret: &RemoteSecret) -> Result<(), String> {
        let target = normalize_target(target)?;
        let mut secrets = self
            .secrets
            .lock()
            .map_err(|_| "secret store lock poisoned")?;
        secrets.insert(target, secret.clone());
        Ok(())
    }

    fn read_secret(&self, target: &str) -> Result<Option<RemoteSecret>, String> {
        let target = normalize_target(target)?;
        let secrets = self
            .secrets
            .lock()
            .map_err(|_| "secret store lock poisoned")?;
        Ok(secrets.get(&target).cloned())
    }

    fn delete_secret(&self, target: &str) -> Result<(), String> {
        let target = normalize_target(target)?;
        let mut secrets = self
            .secrets
            .lock()
            .map_err(|_| "secret store lock poisoned")?;
        secrets.remove(&target);
        Ok(())
    }
}

pub struct WindowsRemoteSecretStore;

#[cfg(windows)]
impl RemoteSecretStore for WindowsRemoteSecretStore {
    fn write_secret(&self, target: &str, secret: &RemoteSecret) -> Result<(), String> {
        let target = normalize_target(target)?;
        let mut target_w = wide_null(&target);
        let mut blob = encode_secret(secret);
        let blob_len = u32::try_from(blob.len())
            .map_err(|_| "remote secret is too large for Windows Credential Manager")?;

        let credential = CREDENTIALW {
            Type: CRED_TYPE_GENERIC,
            TargetName: target_w.as_mut_ptr(),
            CredentialBlobSize: blob_len,
            CredentialBlob: blob.as_mut_ptr(),
            Persist: CRED_PERSIST_LOCAL_MACHINE,
            ..CREDENTIALW::default()
        };

        let ok = unsafe { CredWriteW(&credential, 0) };
        if ok == 0 {
            return Err(format!(
                "failed to write remote credential target: {}",
                std::io::Error::last_os_error()
            ));
        }

        Ok(())
    }

    fn read_secret(&self, target: &str) -> Result<Option<RemoteSecret>, String> {
        let target = normalize_target(target)?;
        let target_w = wide_null(&target);
        let mut credential: *mut CREDENTIALW = ptr::null_mut();
        let ok = unsafe { CredReadW(target_w.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential) };
        if ok == 0 {
            let error = std::io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_NOT_FOUND as i32) {
                return Ok(None);
            }

            return Err(format!("failed to read remote credential target: {error}"));
        }

        let secret = unsafe {
            let credential_ref = &*credential;
            let blob = slice::from_raw_parts(
                credential_ref.CredentialBlob,
                credential_ref.CredentialBlobSize as usize,
            )
            .to_vec();
            CredFree(credential.cast());
            decode_secret(&blob)
        }?;

        Ok(Some(secret))
    }

    fn delete_secret(&self, target: &str) -> Result<(), String> {
        let target = normalize_target(target)?;
        let target_w = wide_null(&target);
        let ok = unsafe { CredDeleteW(target_w.as_ptr(), CRED_TYPE_GENERIC, 0) };
        if ok == 0 {
            let error = std::io::Error::last_os_error();
            if error.raw_os_error() == Some(ERROR_NOT_FOUND as i32) {
                return Ok(());
            }

            return Err(format!(
                "failed to delete remote credential target: {error}"
            ));
        }

        Ok(())
    }
}

#[cfg(not(windows))]
impl RemoteSecretStore for WindowsRemoteSecretStore {
    fn write_secret(&self, _target: &str, _secret: &RemoteSecret) -> Result<(), String> {
        Err("Windows Credential Manager support is only available on Windows".to_string())
    }

    fn read_secret(&self, _target: &str) -> Result<Option<RemoteSecret>, String> {
        Err("Windows Credential Manager support is only available on Windows".to_string())
    }

    fn delete_secret(&self, _target: &str) -> Result<(), String> {
        Err("Windows Credential Manager support is only available on Windows".to_string())
    }
}

fn encode_secret(secret: &RemoteSecret) -> Vec<u8> {
    let (kind, value) = match secret {
        RemoteSecret::Password(value) => ("password", value),
        RemoteSecret::PrivateKeyPassphrase(value) => ("private-key-passphrase", value),
    };
    let mut bytes = Vec::with_capacity(kind.len() + 1 + value.len());
    bytes.extend_from_slice(kind.as_bytes());
    bytes.push(0);
    bytes.extend_from_slice(value.as_bytes());
    bytes
}

fn decode_secret(bytes: &[u8]) -> Result<RemoteSecret, String> {
    let delimiter = bytes
        .iter()
        .position(|byte| *byte == 0)
        .ok_or_else(|| "remote secret blob is missing a kind delimiter".to_string())?;
    let kind = std::str::from_utf8(&bytes[..delimiter])
        .map_err(|_| "remote secret kind is not valid UTF-8")?;
    let value = String::from_utf8(bytes[delimiter + 1..].to_vec())
        .map_err(|_| "remote secret value is not valid UTF-8")?;

    match kind {
        "password" => Ok(RemoteSecret::Password(value)),
        "private-key-passphrase" => Ok(RemoteSecret::PrivateKeyPassphrase(value)),
        _ => Err(format!("unsupported remote secret kind: {kind}")),
    }
}

#[cfg(windows)]
fn wide_null(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

fn normalize_target(target: &str) -> Result<String, String> {
    let trimmed = target.trim();
    if trimmed.is_empty() {
        return Err("remote credential target cannot be empty".to_string());
    }

    Ok(trimmed.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn secret_encoding_round_trips_passwords_and_passphrases() {
        let password = RemoteSecret::Password("s3cret".to_string());
        let passphrase = RemoteSecret::PrivateKeyPassphrase("key phrase".to_string());

        assert_eq!(decode_secret(&encode_secret(&password)).unwrap(), password);
        assert_eq!(
            decode_secret(&encode_secret(&passphrase)).unwrap(),
            passphrase
        );
    }

    #[test]
    fn secret_decoding_rejects_unknown_kinds() {
        let error = decode_secret(b"token\0abc").expect_err("unknown kind");

        assert!(error.contains("unsupported remote secret kind"));
    }

    /// The blob 1.0.4 wrote (`crates/simplefile-service/src/remote/secrets.rs` on `main`): the kind, a NUL, then
    /// the value as UTF-8. Credentials saved by 1.0.4 must keep reading after the upgrade.
    const GOLDEN_PASSWORD: &[u8] = b"password\0p\xc3\xa4ssw\xc3\xb6rd";
    const GOLDEN_PASSPHRASE: &[u8] = b"private-key-passphrase\0k\xc3\xa9y";

    #[test]
    fn blob_matches_the_1_0_4_golden_bytes() {
        assert_eq!(
            encode_secret(&RemoteSecret::Password("pässwörd".into())),
            GOLDEN_PASSWORD
        );
        assert_eq!(
            encode_secret(&RemoteSecret::PrivateKeyPassphrase("kéy".into())),
            GOLDEN_PASSPHRASE
        );
    }

    #[test]
    fn golden_1_0_4_blobs_decode() {
        assert_eq!(
            decode_secret(GOLDEN_PASSWORD).unwrap(),
            RemoteSecret::Password("pässwörd".into())
        );
        assert_eq!(
            decode_secret(GOLDEN_PASSPHRASE).unwrap(),
            RemoteSecret::PrivateKeyPassphrase("kéy".into())
        );
    }

    /// Deletes the test's Credential Manager target however the test ends.
    #[cfg(windows)]
    struct CredentialGuard(String);

    #[cfg(windows)]
    impl Drop for CredentialGuard {
        fn drop(&mut self) {
            let target_w = wide_null(&self.0);
            unsafe {
                CredDeleteW(target_w.as_ptr(), CRED_TYPE_GENERIC, 0);
            }
        }
    }

    /// Writes `blob` the way 1.0.4 did, with `CredWriteW` directly rather than through the store under test.
    #[cfg(windows)]
    fn write_as_1_0_4(target: &str, blob: &[u8]) {
        let mut target_w = wide_null(target);
        let mut blob = blob.to_vec();
        let credential = CREDENTIALW {
            Type: CRED_TYPE_GENERIC,
            TargetName: target_w.as_mut_ptr(),
            CredentialBlobSize: u32::try_from(blob.len()).unwrap(),
            CredentialBlob: blob.as_mut_ptr(),
            Persist: CRED_PERSIST_LOCAL_MACHINE,
            ..CREDENTIALW::default()
        };
        let ok = unsafe { CredWriteW(&credential, 0) };
        assert_ne!(ok, 0, "CredWriteW: {}", std::io::Error::last_os_error());
    }

    #[cfg(windows)]
    #[test]
    fn windows_store_reads_a_credential_written_as_1_0_4_wrote_it() {
        let mut random = [0u8; 8];
        getrandom::fill(&mut random).unwrap();
        let hex: String = random.iter().map(|byte| format!("{byte:02x}")).collect();
        let guard = CredentialGuard(format!("SumaFile.Remote.test-{hex}"));
        write_as_1_0_4(&guard.0, GOLDEN_PASSWORD);

        let secret = WindowsRemoteSecretStore.read_secret(&guard.0).unwrap();

        assert_eq!(secret, Some(RemoteSecret::Password("pässwörd".into())));
    }
}
