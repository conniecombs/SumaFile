# FTP/SFTP Manager Design

## Goal

Build a native FTP/SFTP manager for SumaFile that starts as a dedicated transfer manager surface and grows into normal remote browsing once the contracts are proven. The first user-visible milestone must let a user create remote connection profiles, connect, browse a remote directory, create folders, delete/rename entries, and upload/download through the existing transfer-progress UX.

## Product Scope

The manager supports:

- SFTP over SSH with host-key verification.
- FTP and explicit FTPS profiles, with plain FTP clearly marked as insecure.
- Saved connection profiles with display name, protocol, host, port, username, root path, passive-mode preference for FTP, and authentication kind.
- Secrets stored outside workspace layout/settings JSON. Passwords and private-key passphrases use Windows Credential Manager when available; non-Windows test paths use an injectable in-memory store.
- A dedicated WinUI manager reachable from command palette, toolbar/menu command routing, and Settings/Tools entry points.
- Dual-pane transfer workflows between local files and the active remote profile.
- Progress, cancellation, retry, conflict handling, and operation history through the existing transfer manager patterns.

Out of scope for the first implementation wave:

- Account-backed file-service integrations outside the FTP/SFTP scope.
- Full remote support for every local-only SumaFile tool such as Git workbench, disk cleanup, duplicate checker, archive mutation, Windows shell Open With, terminal, thumbnail generation, and Recycle Bin restore.
- Mounting remotes into Windows as drive letters.

## Architecture

Add a Rust remote subsystem behind the named-pipe IPC service. WinUI continues to call schema-generated IPC clients; no FTP/SFTP protocol code lives in XAML or window partials. Remote paths use explicit opaque handles and profile IDs instead of pretending that `sftp://...` is a Windows path.

The first UI is a dedicated FTP/SFTP manager window:

- Left side: local path picker/listing using existing SumaFile local abstractions.
- Right side: selected remote profile, connection state, remote breadcrumb/path box, remote listing.
- Bottom/status area: queued uploads/downloads using existing transfer-progress models.
- Profile management: add/edit/delete/test connection, save secret toggle, host-key trust prompt for SFTP.

Later integration can add remote profiles to the sidebar and allow remote tabs in the main explorer once remote capability flags are stable.

## Rust Units

- `crates/simplefile-core/src/remote/models.rs`: serializable profile, protocol, auth, listing, capability, and error models.
- `crates/simplefile-core/src/remote/profiles.rs`: profile metadata persistence in the existing metadata DB.
- `crates/simplefile-service/src/remote/secrets.rs`: Windows Credential Manager backed secret store, plus test double.
- `crates/simplefile-service/src/remote/session.rs`: session registry keyed by `remoteSessionId`, with connect/disconnect/list/stat/mkdir/rename/delete/upload/download methods.
- `crates/simplefile-service/src/remote/provider.rs`: protocol-agnostic trait implemented by SFTP and FTP/FTPS providers.
- `crates/simplefile-service/src/remote/sftp.rs`: SFTP provider.
- `crates/simplefile-service/src/remote/ftp.rs`: FTP/FTPS provider.
- `crates/simplefile-service/src/remote/transfers.rs`: remote upload/download transfer planning, progress emission, cancellation, and partial-file cleanup.

## IPC Contract

Add schema-first methods:

- `remote_list_profiles` -> `RemoteProfile[]`
- `remote_save_profile(profile, secret?)` -> `RemoteProfile`
- `remote_delete_profile(profileId)` -> `null`
- `remote_test_profile(profile, secret?)` -> `RemoteConnectionTestResult`
- `remote_connect(profileId, secret?)` -> `RemoteSession`
- `remote_disconnect(remoteSessionId)` -> `null`
- `remote_list_directory(remoteSessionId, path, options?)` -> `RemoteDirectoryListing`
- `remote_create_directory(remoteSessionId, path, name)` -> `RemoteEntry`
- `remote_rename_entry(remoteSessionId, path, newName)` -> `RemoteEntry`
- `remote_delete_entries(remoteSessionId, paths[])` -> `RemoteDeleteResult`
- `remote_upload(localPaths[], remoteDestination, remoteSessionId, operationId?, conflictAction)` -> `TransferResult[]`
- `remote_download(remotePaths[], localDestination, remoteSessionId, operationId?, conflictAction)` -> `TransferResult[]`
- `remote_cancel_operation(operationId)` -> `bool`

Progress uses the existing `operation-progress` event with `operationId` filtering.

## Security

- Secrets are never written into workspace layout, saved profiles JSON, logs, screenshots, or operation history.
- Profile records store only a credential target key such as `SumaFile.Remote.<profileId>`.
- SFTP stores trusted host-key fingerprints per profile and refuses changed fingerprints unless the user explicitly replaces trust.
- FTP profiles warn when protocol is plain FTP; passwords may still be saved if the user chooses to accept the risk.
- Private-key files are referenced by path, not copied into the metadata DB.

## UI Behavior

- The command palette includes `ftp-sftp-manager`.
- The Tools menu and Settings tools list expose the manager.
- The manager disables remote mutation buttons until connected.
- Plain FTP profiles show an insecure badge in profile lists and the editor.
- Upload/download use existing conflict prompts where possible.
- Remote unsupported commands are hidden or disabled rather than failing late.

## Verification

Required gates:

- Rust unit tests for profile persistence, secret-store abstraction, session registry behavior, path normalization, and provider error mapping.
- C# tests for command catalog, settings/tools entry points, manager view model state transitions, and transfer routing labels.
- IPC schema generation and consistency checks.
- `cargo test --workspace --all-features`
- `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`
- `npm run check:ipc-schema`
- `npm run check:winui-parity-gate`
- `npm run check:winui`
- `npm run check`
- `git diff --check`
