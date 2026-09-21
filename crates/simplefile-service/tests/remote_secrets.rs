use simplefile_service::remote::secrets::{
    MemoryRemoteSecretStore, RemoteSecret, RemoteSecretStore,
};

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
