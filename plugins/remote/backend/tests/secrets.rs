use sumafile_remote_backend::secrets::{MemoryRemoteSecretStore, RemoteSecret, RemoteSecretStore};

#[test]
fn memory_remote_secret_store_round_trips_by_target() {
    let store = MemoryRemoteSecretStore::default();
    let target = "SumaFile.Remote.profile-1";
    let secret = RemoteSecret::Password("correct horse battery staple".to_string());

    store
        .write_secret(target, &secret)
        .expect("write remote secret");

    assert_eq!(
        store.read_secret(target).expect("read remote secret"),
        Some(secret)
    );
}

#[test]
fn memory_remote_secret_store_overwrites_existing_target() {
    let store = MemoryRemoteSecretStore::default();
    let target = "SumaFile.Remote.profile-1";

    store
        .write_secret(target, &RemoteSecret::Password("old".to_string()))
        .expect("write old secret");
    store
        .write_secret(target, &RemoteSecret::Password("new".to_string()))
        .expect("write new secret");

    assert_eq!(
        store.read_secret(target).expect("read overwritten secret"),
        Some(RemoteSecret::Password("new".to_string()))
    );
}

#[test]
fn memory_remote_secret_store_delete_removes_target() {
    let store = MemoryRemoteSecretStore::default();
    let target = "SumaFile.Remote.profile-1";

    store
        .write_secret(target, &RemoteSecret::Password("secret".to_string()))
        .expect("write remote secret");
    store.delete_secret(target).expect("delete remote secret");

    assert_eq!(store.read_secret(target).expect("read deleted"), None);
}

/// Deletes the test's own credential when the test ends, even when an
/// assertion failed first. It only ever touches the target it was given.
#[cfg(windows)]
struct DeleteCredentialOnDrop(String);

#[cfg(windows)]
impl Drop for DeleteCredentialOnDrop {
    fn drop(&mut self) {
        use sumafile_remote_backend::secrets::WindowsRemoteSecretStore;
        let _ = WindowsRemoteSecretStore.delete_secret(&self.0);
    }
}

#[cfg(windows)]
#[test]
fn windows_store_round_trips_a_real_credential_and_deletes_it() {
    use sumafile_remote_backend::secrets::WindowsRemoteSecretStore;

    let mut random = [0u8; 8];
    getrandom::fill(&mut random).expect("random target suffix");
    let suffix: String = random.iter().map(|byte| format!("{byte:02x}")).collect();
    let target = format!("SumaFile.Remote.test-{suffix}");
    let guard = DeleteCredentialOnDrop(target.clone());
    let store = WindowsRemoteSecretStore;
    assert_eq!(
        store.read_secret(&target).expect("read before write"),
        None,
        "the random target must be new"
    );

    let secret =
        RemoteSecret::Password("p\u{e4}ssw\u{f6}rd with spaces & \u{8cc7}\u{6599}".to_string());
    store
        .write_secret(&target, &secret)
        .expect("write credential");
    assert_eq!(store.read_secret(&target).expect("read"), Some(secret));

    let passphrase = RemoteSecret::PrivateKeyPassphrase("key phrase".to_string());
    store
        .write_secret(&target, &passphrase)
        .expect("overwrite credential");
    assert_eq!(
        store.read_secret(&target).expect("read overwritten"),
        Some(passphrase)
    );

    store.delete_secret(&target).expect("delete credential");
    assert_eq!(store.read_secret(&target).expect("read deleted"), None);
    // Deleting a missing target is not an error.
    store.delete_secret(&target).expect("delete again");
    drop(guard);
}
