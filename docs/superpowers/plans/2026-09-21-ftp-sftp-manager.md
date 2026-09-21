# FTP/SFTP Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build SumaFile's native FTP/SFTP manager, beginning with profile management and a dedicated manager surface backed by schema-first IPC.

**Architecture:** Remote file-transfer support lives in Rust behind the named-pipe service and is exposed through generated C# IPC clients. WinUI starts with a dedicated FTP/SFTP manager window and command entry point, while profile metadata and secrets are kept separate so saved workspace/layout JSON never contains credentials.

**Tech Stack:** Rust workspace crates, SQLite metadata DB via `rusqlite`, Windows Credential Manager/DPAPI-compatible abstraction, named-pipe JSON-RPC IPC schema generation, C# WinUI 3, xUnit, Rust unit tests.

**Spec:** `docs/superpowers/specs/2026-09-21-ftp-sftp-manager-design.md`

## Global Constraints

- Do not add account-backed file-service surfaces outside the FTP/SFTP scope.
- Do not store passwords or private-key passphrases in workspace layout/settings JSON.
- Keep protocol code out of WinUI window classes; WinUI calls schema-backed IPC/service abstractions.
- Plain FTP is allowed only when visibly marked insecure in the profile model and UI.
- Remote-only unsupported local commands must be hidden or disabled instead of failing late.
- Existing verification gates remain authoritative: `npm run check:ipc-schema`, `npm run check:winui-parity-gate`, `npm run check:winui`, `npm run check`, Rust tests, and WinUI tests.

---

### Task 1: Remote Profile Domain Model And Persistence

**Files:**
- Create: `crates/simplefile-core/src/remote/mod.rs`
- Create: `crates/simplefile-core/src/remote/models.rs`
- Create: `crates/simplefile-core/src/remote/profiles.rs`
- Modify: `crates/simplefile-core/src/lib.rs`
- Test: Rust unit tests in `crates/simplefile-core/src/remote/profiles.rs`

**Interfaces:**
- Produces: `RemoteProtocol`, `RemoteAuthKind`, `RemoteProfile`, `RemoteProfileInput`, `RemoteProfileStore`, `RemoteProfileStore::list_profiles_at`, `RemoteProfileStore::save_profile_at`, `RemoteProfileStore::delete_profile_at`.
- Later tasks consume these exact types from `simplefile_core::remote`.

- [ ] **Step 1: Write failing Rust tests**

Add tests that prove:

```rust
let input = RemoteProfileInput {
    id: None,
    name: "Production SFTP".to_string(),
    protocol: RemoteProtocol::Sftp,
    host: "files.example.com".to_string(),
    port: 22,
    username: "deploy".to_string(),
    root_path: "/var/www".to_string(),
    auth_kind: RemoteAuthKind::Password,
    insecure_plain_ftp: false,
    passive_mode: true,
    credential_target: None,
    trusted_host_fingerprint: None,
};
```

Saving the profile returns a generated non-empty `id`, normalized root path `/var/www`, and credential target `SumaFile.Remote.<id>`. Listing returns the saved profile without any secret fields. Saving a plain FTP profile with `insecure_plain_ftp: false` fails with a message containing `Plain FTP requires insecure_plain_ftp`.

Run: `cargo test -p simplefile-core remote_profiles --locked`
Expected: FAIL because `simplefile_core::remote` does not exist.

- [ ] **Step 2: Implement profile models**

Create serializable Rust enums and structs with serde camelCase where needed:

```rust
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum RemoteProtocol { Sftp, Ftp, Ftps }

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum RemoteAuthKind { Password, PrivateKey, Agent, Anonymous }
```

`RemoteProfile` contains `id`, `name`, `protocol`, `host`, `port`, `username`, `root_path`, `auth_kind`, `insecure_plain_ftp`, `passive_mode`, `credential_target`, and `trusted_host_fingerprint`.

- [ ] **Step 3: Implement metadata DB persistence**

Use the existing metadata DB location helpers from `settings_store.rs` patterns. Create table `remote_profiles` with columns `id TEXT PRIMARY KEY`, `name TEXT NOT NULL`, `protocol TEXT NOT NULL`, `host TEXT NOT NULL`, `port INTEGER NOT NULL`, `username TEXT NOT NULL`, `root_path TEXT NOT NULL`, `auth_kind TEXT NOT NULL`, `insecure_plain_ftp INTEGER NOT NULL`, `passive_mode INTEGER NOT NULL`, `credential_target TEXT`, `trusted_host_fingerprint TEXT`, `updated_at TEXT NOT NULL`.

- [ ] **Step 4: Run task verification**

Run: `cargo test -p simplefile-core remote_profiles --locked`
Expected: PASS.

### Task 2: Remote Secret Store Abstraction

**Files:**
- Create: `crates/simplefile-service/src/remote/mod.rs`
- Create: `crates/simplefile-service/src/remote/secrets.rs`
- Modify: `crates/simplefile-service/src/lib.rs`
- Test: unit tests in `crates/simplefile-service/src/remote/secrets.rs`

**Interfaces:**
- Consumes: `RemoteProfile.credential_target`.
- Produces: `RemoteSecretStore` trait with `write_secret`, `read_secret`, and `delete_secret`; `MemoryRemoteSecretStore` for tests; `WindowsRemoteSecretStore` behind `cfg(windows)`.

- [ ] **Step 1: Write failing tests**

Add tests proving `MemoryRemoteSecretStore` round-trips a password by target name and delete removes it.

Run: `cargo test -p simplefile-service remote_secrets --locked`
Expected: FAIL because the module does not exist.

- [ ] **Step 2: Implement memory store and Windows store shape**

Implement `MemoryRemoteSecretStore` using `Mutex<HashMap<String, RemoteSecret>>`. Add a Windows type with method signatures ready for `CredWriteW`/`CredReadW`; return a clear `Windows Credential Manager support is not available in this build` error on non-Windows.

- [ ] **Step 3: Run task verification**

Run: `cargo test -p simplefile-service remote_secrets --locked`
Expected: PASS.

### Task 3: Schema-First Remote Profile IPC

**Files:**
- Modify: `ipc/schema/v1/types.json`
- Modify: `ipc/schema/v1/commands.json`
- Modify: generated files via `npm run check:ipc-generated -- --write` if the script supports writing; otherwise run `node scripts/generate-ipc-bindings.mjs --write`
- Modify: `crates/simplefile-service/src/dispatch/params.rs`
- Modify: `crates/simplefile-service/src/dispatch/handlers.rs`
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`
- Test: `crates/simplefile-service/src/dispatch/tests.rs`
- Test: `src-winui/SimpleFile.Tests/NamedPipeJsonClientTests.cs`

**Interfaces:**
- Consumes: Task 1 profile store and Task 2 secret target contract.
- Produces: `remote_list_profiles`, `remote_save_profile`, `remote_delete_profile`, and `remote_test_profile` IPC methods.

- [ ] **Step 1: Write failing schema/dispatch tests**

Add one Rust dispatch test invoking `remote_save_profile` then `remote_list_profiles`. Add one C# client test that deserializes a `RemoteProfile` with `protocol: "sftp"` and `credentialTarget` but no password.

Run: `cargo test -p simplefile-service remote_profile_ipc --locked`
Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~NamedPipeJsonClientTests"`
Expected: FAIL because methods/types do not exist.

- [ ] **Step 2: Update schema and generate bindings**

Add the remote profile model types and methods to schema JSON. Regenerate `simplefile-ipc`, `SimpleFile.Ipc`, and generated client methods with the repo script.

- [ ] **Step 3: Implement dispatch handlers**

Parse `RemoteProfileInput`, call profile persistence, and ensure any provided secret is written only through the secret store target. `remote_test_profile` validates profile fields and returns `{ ok, message, capabilities }` without making network calls until provider work lands.

- [ ] **Step 4: Run task verification**

Run:
`npm run check:ipc-schema`
`cargo test -p simplefile-service remote_profile_ipc --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~NamedPipeJsonClientTests"`

Expected: PASS.

### Task 4: Dedicated Manager Command And View Model Shell

**Files:**
- Modify: `src-winui/SimpleFile.Core/AppCommandCatalog.cs`
- Modify: `src-winui/SimpleFile.Core/ContextMenuIconCatalog.cs`
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`
- Create: `src-winui/SimpleFile.Core/RemoteManagerViewModel.cs`
- Create: `src-winui/SimpleFile.App/RemoteManagerWindow.xaml`
- Create: `src-winui/SimpleFile.App/RemoteManagerWindow.xaml.cs`
- Modify: `src-winui/SimpleFile.App/SimpleFile.App.csproj`
- Modify: `src-winui/SimpleFile.App/MainWindow.Commands.cs`
- Test: `src-winui/SimpleFile.Tests/AppCommandCatalogTests.cs`
- Test: `src-winui/SimpleFile.Tests/RemoteManagerViewModelTests.cs`

**Interfaces:**
- Consumes: IPC profile methods from Task 3.
- Produces: command `ftp-sftp-manager` and a manager window that can list profiles, show empty state, and open the profile editor shell.

- [ ] **Step 1: Write failing C# tests**

Add tests proving `AppCommandCatalog.Find("ftp-sftp-manager")` returns label `FTP/SFTP manager`, and `RemoteManagerViewModel.LoadProfilesAsync` exposes profiles returned by `FileOperationService`.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AppCommandCatalogTests|FullyQualifiedName~RemoteManagerViewModelTests"`
Expected: FAIL because command and view model do not exist.

- [ ] **Step 2: Implement command and view model**

Add the command to group `Tools`. Implement view-model state with `Profiles`, `SelectedProfile`, `IsBusy`, `StatusText`, `CanConnect`, `CanDeleteProfile`, and `LoadProfilesAsync`.

- [ ] **Step 3: Implement window shell**

Create a compact work-focused WinUI window with profile list, connection buttons, local/remote list placeholders, and transfer queue area. Keep controls disabled until profile IPC exists.

- [ ] **Step 4: Run task verification**

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AppCommandCatalogTests|FullyQualifiedName~RemoteManagerViewModelTests"`
`npm run build:winui`

Expected: PASS.

### Task 5: Provider Sessions And Remote Listing

**Files:**
- Create: `crates/simplefile-service/src/remote/provider.rs`
- Create: `crates/simplefile-service/src/remote/session.rs`
- Create: `crates/simplefile-service/src/remote/sftp.rs`
- Create: `crates/simplefile-service/src/remote/ftp.rs`
- Modify: `crates/simplefile-service/Cargo.toml`
- Modify: IPC schema for `remote_connect`, `remote_disconnect`, `remote_list_directory`, `remote_create_directory`, `remote_rename_entry`, and `remote_delete_entries`
- Test: service unit tests with a fake provider

**Interfaces:**
- Consumes: remote profiles and secret store.
- Produces: `RemoteSessionRegistry` and provider trait methods for connection and directory mutation.

- [ ] **Step 1: Write failing fake-provider tests**

Test that connecting a profile creates a session ID, listing `/` returns a directory entry, disconnect removes the session, and listing after disconnect returns `remote session not found`.

Run: `cargo test -p simplefile-service remote_session --locked`
Expected: FAIL because session registry does not exist.

- [ ] **Step 2: Implement registry and fake-provider-backed tests**

Add session map keyed by generated IDs. Keep provider construction injectable so tests do not touch the network.

- [ ] **Step 3: Add protocol dependencies and real provider skeletons**

Add SFTP and FTP/FTPS dependencies only after fake-provider tests pass. Implement connect/list/stat/mkdir/rename/delete with clear provider error mapping. Avoid upload/download until Task 6.

- [ ] **Step 4: Run task verification**

Run:
`cargo test -p simplefile-service remote_session --locked`
`npm run check:ipc-schema`

Expected: PASS.

### Task 6: Upload/Download Transfer Integration

**Files:**
- Create: `crates/simplefile-service/src/remote/transfers.rs`
- Modify: `crates/simplefile-service/src/session/jobs.rs`
- Modify: `crates/simplefile-service/src/progress.rs` only if shared progress helpers are needed
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`
- Modify: `src-winui/SimpleFile.Core/TransferManagerViewModel.cs` if labels need remote verbs
- Test: Rust remote transfer tests with fake provider streams
- Test: C# transfer-label tests

**Interfaces:**
- Consumes: `operation-progress` and existing transfer queue.
- Produces: `remote_upload`, `remote_download`, and `remote_cancel_operation`.

- [ ] **Step 1: Write failing upload/download tests**

Use fake providers to assert upload writes local bytes to remote destination, download writes remote bytes to local staging, cancel removes partial files, and conflict `skip` returns skipped results.

Run: `cargo test -p simplefile-service remote_transfer --locked`
Expected: FAIL because transfer integration does not exist.

- [ ] **Step 2: Implement transfer operations**

Reuse existing progress update shapes and operation IDs. Emit `running`, `completed`, `cancelled`, and `error` statuses. Keep partial remote uploads hidden with `.sumafile-partial` suffix when provider supports rename.

- [ ] **Step 3: Wire C# service methods and manager buttons**

Add `RemoteUploadAsync` and `RemoteDownloadAsync` wrappers and call them from `RemoteManagerViewModel` commands.

- [ ] **Step 4: Run task verification**

Run:
`cargo test -p simplefile-service remote_transfer --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~Transfer|FullyQualifiedName~RemoteManagerViewModelTests"`

Expected: PASS.

### Task 7: Final Integration And Gates

**Files:**
- Modify: `README.md`
- Modify: `docs/SECURITY.md`
- Modify: `scripts/check-provider-surface.mjs` only if the new FTP/SFTP scope requires a guard update.
- Modify: `docs/winui-migration/parity-gate.md` if command counts or parity rows change.

**Interfaces:**
- Consumes: all previous tasks.
- Produces: documented FTP/SFTP scope and updated release/checker expectations.

- [ ] **Step 1: Update docs and guards**

Document FTP/SFTP as protocol-transfer support. Keep non-FTP/SFTP account surfaces blocked.

- [ ] **Step 2: Run final gates**

Run:
`cargo test --workspace --all-features`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`
`npm run check:ipc-schema`
`npm run check:winui-parity-gate`
`npm run check:winui`
`npm run check`
`git diff --check`

Expected: every command exits 0. Any guard count changes must be backed by source and doc updates in the same diff.

## Self-Review

- Spec coverage: tasks cover profile persistence, credential separation, IPC methods, manager entry surface, provider sessions, upload/download, docs, and verification.
- Placeholder scan: no task uses TBD/TODO or asks for unspecified edge handling.
- Type consistency: profile models, IPC method names, and manager command ID are named once and reused consistently.
- Scope check: this plan is large but intentionally staged; Task 1 through Task 4 produce a usable profile manager shell even before real network transfers land, while Tasks 5 and 6 complete protocol browsing and transfer behavior.
