# Plugin System Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the plugin SDK, the host that discovers, approves, loads and talks to plugins, the one archive-engine hook in the Rust service, a Settings > Plugins page, and a fixture plugin that proves every phase-1 slot, while Git, FTP/SFTP and 7-Zip stay internal.

**Architecture:** A new `SimpleFile.PluginSdk` library holds only contracts. A new `SimpleFile.PluginHost` library owns manifests, discovery, approval, signature checks, assembly loading, and a per-plugin backend process that the host spawns and talks to over the existing named-pipe JSON-RPC framing, now bidirectional. The Rust service learns one thing: an archive engine registry it can fill by dialing a plugin's pipe. A Rust helper crate gives backend authors the pipe server, handshake and host client.

**Tech Stack:** C# / .NET 10 / WinUI 3 (Windows App SDK 2.4.0), xUnit; Rust 2021 with tokio and serde; Node scripts for schema checks; BouncyCastle.Cryptography for Ed25519 in the host.

**Spec:** `docs/superpowers/specs/2026-11-07-plugin-system-design.md`. This plan covers spec section 14 phase 1 only. Phases 2 to 5 get their own plans after this one lands, because they depend on the SDK built here and on the XAML spike's result (Task 17).

## Global Constraints

- Every C# project: `net10.0-windows10.0.19041.0`, `TreatWarningsAsErrors`, `Nullable` enabled, `RestorePackagesWithLockFile` (commit the updated `packages.lock.json` after any package change; run `dotnet restore --force-evaluate` on the project).
- Windows App SDK package version is exactly `2.4.0`, matching `SimpleFile.App.csproj`.
- Rust: workspace edition 2021; `cargo test --locked --all-features` and `cargo clippy --locked --all-targets --all-features -- -D warnings` must pass; commit `Cargo.lock` changes.
- `npm run check` must stay green. In particular `scripts/check-ipc-schema.mjs` asserts the exact domain method count, which Task 13 raises from 97 to 99.
- Startup budgets in `StartupTimingBudget` and `scripts/check-winui-startup-budget.mjs` stay as they are; plugin discovery adds a new stage `MainWindow.Connect.plugins-discovered` whose delta must be under 50 ms.
- Plugin id grammar: `^[a-z0-9][a-z0-9.-]{1,63}$`. Plugin settings key grammar: `^[a-z0-9][a-zA-Z0-9_-]{0,63}$`. Settings persist as `plugin.<id>.<key>`.
- Plugin command ids are exposed to the host as `plugin:<id>:<command>`.
- Pipe name for a plugin backend: `SumaFile.Plugin.<sessionId>.<pid>.<pluginId>`. Connect timeout 5 s. Backend data directory `%LOCALAPPDATA%\SumaFile\plugins-data\<id>\`.
- Plugin roots in scan order: `<AppContext.BaseDirectory>\plugins`, then `%LOCALAPPDATA%\SumaFile\plugins`. A duplicate id resolves to the later (user) root.
- Approval store file: `%LOCALAPPDATA%\SumaFile\plugin-approvals.json`.
- Product name in user-facing text is "SumaFile"; internal namespaces keep `SimpleFile`, which `scripts/check-sumafile-identity.mjs` allows.
- Commit messages follow the repository style: one imperative sentence, no type prefix, ending with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Spec amendment made in Task 1: Ed25519 verification happens in the C# host with BouncyCastle, using the same raw 32-byte base64 key format as the Rust updater, because the host must verify before any backend pipe exists.

## Review Focus

- A manifest whose `id` has uppercase letters, `..`, or a path separator must be rejected before anything is read from its folder. Test in Task 2.
- A `.sfplugin` whose zip entries escape the destination folder (`..\` or absolute paths) must be refused with nothing written. Test in Task 12.
- A backend that starts but never answers the handshake must fail the invoking command within the 5 s timeout, be killed, and not hang the UI. Test in Task 6.
- A plugin backend calling `host.set_setting` with a key containing a dot, or longer than 64 characters, must get an invalid-params error and write nothing. Test in Task 7.
- A plugin folder deleted while the plugin is enabled must drop out on rescan with its commands, menus and toolbar actions withdrawn, not throw. Test in Task 9.

---

## File structure

**Created**

- `src-winui/SimpleFile.PluginSdk/` — contracts only: `PluginSdk.cs`, `IPlugin.cs`, `IPluginHost.cs`, `ICommandHandler.cs`, `IPluginSettings.cs`, `IPluginProgress.cs`, `IPluginDialogs.cs`, `IPluginWorkspace.cs`, `IPluginLogger.cs`.
- `src-winui/SimpleFile.PluginHost/` — `Manifest/PluginManifest.cs`, `Manifest/PluginManifestReader.cs`, `Discovery/PluginDiscovery.cs`, `Discovery/DiscoveredPlugin.cs`, `Trust/PluginApprovalStore.cs`, `Trust/PluginPackageHash.cs`, `Trust/PluginSignatureVerifier.cs`, `Backend/IPluginProcessLauncher.cs`, `Backend/PluginBackendHost.cs`, `Backend/PluginBackendConnection.cs`, `Backend/PluginHostMethods.cs`, `Loading/PluginLoadContext.cs`, `Loading/PluginActivator.cs`, `PluginManager.cs`, `PluginRecord.cs`, `PluginHostServices.cs`, `Contributions/PluginContributionAdapter.cs`, `Install/PluginPackageInstaller.cs`.
- `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/` — C# fixture plugin project `SimpleFile.Tests.Fixtures.HelloPlugin.csproj`, `HelloPlugin.cs`, `plugin.json`.
- `src-winui/SimpleFile.Tests/Fixtures/PluginSdkSurface.v1.txt` — pinned SDK surface.
- `src-winui/SimpleFile.App/PluginConsentDialog.xaml(.cs)`, `src-winui/SimpleFile.App/PluginSettingsPanelBuilder.cs`, `src-winui/SimpleFile.App/MainWindow.Plugins.cs`, `src-winui/SimpleFile.App/SettingsWindow.Plugins.cs`.
- `crates/simplefile-core/src/archive/engine.rs` — `ArchiveEngine` trait and registry.
- `crates/simplefile-service/src/plugin_archive.rs` — pipe-backed engine and registration handlers.
- `crates/sumafile-plugin/` — backend helper crate.
- `crates/hello-plugin-backend/` — fixture backend.
- `ipc/schema/plugin/v1/protocol.json` — plugin pipe protocol.
- `scripts/build-fixture-plugin.ps1` — assembles the fixture plugin folder.

**Modified**

- `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.cs` — inbound requests.
- `src-winui/SimpleFile.Core/JobObject.cs` — made public.
- `src-winui/SimpleFile.Core/AppCommandCatalog.cs`, `ContextMenuBuilder.cs`, `CommandSurfaceLayout.cs` — plugin contributions.
- `src-winui/SimpleFile.Core/StartupTimingBudget.cs`, `scripts/check-winui-startup-budget.mjs` — new stage.
- `src-winui/SimpleFile.App/MainWindow.xaml.cs`, `MainWindow.Commands.cs`, `MainWindow.CommandRouting.cs`, `SettingsWindow.xaml`, `SettingsWindow.xaml.cs`, `SimpleFile.App.csproj`.
- `src-winui/SimpleFile.Tests/FakeIpcServer.cs`, `SimpleFile.Tests.csproj`, `src-winui/SimpleFile.sln`.
- `crates/simplefile-core/src/archive/mod.rs`, `list.rs`, `extract.rs`, `create.rs`, `capabilities.rs`, `path.rs`.
- `crates/simplefile-service/src/dispatch/handlers.rs`, `src/lib.rs`, `Cargo.toml`.
- `ipc/schema/v1/commands.json`, `scripts/check-ipc-schema.mjs`, generated bindings, `Cargo.toml` (workspace members).
- `docs/superpowers/specs/2026-11-07-plugin-system-design.md` — section 9 amendment.

---

### Task 1: SDK project and surface pin

**Files:**
- Create: `src-winui/SimpleFile.PluginSdk/SimpleFile.PluginSdk.csproj` and the nine contract files listed above
- Create: `src-winui/SimpleFile.Tests/PluginSdkSurfaceTests.cs`, `src-winui/SimpleFile.Tests/Fixtures/PluginSdkSurface.v1.txt`
- Modify: `src-winui/SimpleFile.sln`, `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj` (project reference), `docs/superpowers/specs/2026-11-07-plugin-system-design.md:§9`

**Interfaces:**
- Produces, namespace `SimpleFile.PluginSdk`:
  - `public static class PluginSdk { public const string Version = "1.0.0"; public const int Major = 1; }`
  - `public interface IPlugin { Task ActivateAsync(IPluginHost host, CancellationToken ct); Task DeactivateAsync(); }`
  - `public interface IPluginHost { string PluginId { get; } Task<T> InvokeBackendAsync<T>(string method, object? args, CancellationToken ct); IDisposable OnBackendEvent<T>(string eventName, Action<T> handler); IPluginSettings Settings { get; } IPluginProgress Progress { get; } IPluginDialogs Dialogs { get; } IPluginWorkspace Workspace { get; } IPluginLogger Logger { get; } }`
  - `public interface ICommandHandler { Task ExecuteAsync(PluginCommandContext context, CancellationToken ct); }`
  - `public sealed record PluginCommandContext(string CommandId, string? ActivePath, IReadOnlyList<string> SelectedPaths);`
  - `public interface IPluginSettings { Task<string?> GetAsync(string key, CancellationToken ct); Task SetAsync(string key, string value, CancellationToken ct); }`
  - `public interface IPluginProgress { IPluginOperation Begin(string title); }` and `public interface IPluginOperation : IDisposable { CancellationToken CancellationToken { get; } void Report(long current, long total, string? detail); void Complete(); void Fail(string message); }`
  - `public interface IPluginDialogs { Task ShowMessageAsync(string title, string message, CancellationToken ct); Task<bool> ConfirmAsync(string title, string message, string confirmLabel, CancellationToken ct); Task<string?> PickFolderAsync(CancellationToken ct); }`
  - `public interface IPluginWorkspace { string? ActivePath { get; } IReadOnlyList<string> SelectedPaths { get; } event EventHandler? Changed; Task NavigateAsync(string path, CancellationToken ct); }`
  - `public interface IPluginLogger { void Info(string message); void Warn(string message); void Error(string message, Exception? exception = null); }`
- The csproj targets `net10.0-windows10.0.19041.0`, `UseWinUI` true, references `Microsoft.WindowsAppSDK` 2.4.0 so later phases can add WinUI-typed contracts without a retarget. No other references.

- [ ] **Step 1: Write the failing surface test**

```csharp
[Fact]
public void PublicSurface_MatchesPinnedSnapshot()
{
    var actual = PluginSdkSurface.Describe(typeof(PluginSdk).Assembly); // sorted "Type: member signature" lines
    var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PluginSdkSurface.v1.txt"));
    Assert.Equal(expected, actual);
}
```

`PluginSdkSurface.Describe` lives in the test project: for every public type, one line per public member using `MemberInfo.ToString()`, sorted ordinal.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginSdkSurfaceTests"`
Expected: FAIL, the project reference does not exist yet.

- [ ] **Step 3: Create the SDK project, the nine contract files, add to the solution and reference from the tests**

Add `SimpleFile.PluginSdk` to `SimpleFile.sln`. In the test csproj add a `ProjectReference` and copy `Fixtures/PluginSdkSurface.v1.txt` to output with `CopyToOutputDirectory=PreserveNewest`.

- [ ] **Step 4: Generate the snapshot once and run the test**

Write the snapshot by running `Describe` from a throwaway test, save it as `Fixtures/PluginSdkSurface.v1.txt`, delete the throwaway. Run the filter from Step 2. Expected: PASS.

- [ ] **Step 5: Amend spec section 9**

Replace "using the updater's existing ed25519 verifier" with "using an Ed25519 verifier in the host (BouncyCastle) with the same raw 32-byte base64 public key format the updater uses".

- [ ] **Step 6: Commit**

```bash
git add src-winui/SimpleFile.PluginSdk src-winui/SimpleFile.Tests src-winui/SimpleFile.sln docs/superpowers/specs/2026-11-07-plugin-system-design.md
git commit -m "Add the plugin SDK contracts and pin their public surface"
```

---

### Task 2: Manifest model and reader

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/SimpleFile.PluginHost.csproj`, `Manifest/PluginManifest.cs`, `Manifest/PluginManifestReader.cs`
- Test: `src-winui/SimpleFile.Tests/PluginManifestReaderTests.cs`
- Modify: `src-winui/SimpleFile.sln`, test csproj (project reference)

**Interfaces:**
- Consumes: `PluginSdk.Major` from Task 1.
- Produces, namespace `SimpleFile.PluginHost.Manifest`:
  - `public sealed record PluginManifest(string Id, string Name, string Version, PluginPublisher Publisher, string SdkVersion, PluginUiSpec? Ui, PluginBackendSpec? Backend, PluginContributions Contributions, IReadOnlyList<PluginSettingSpec> Settings, IReadOnlyList<string> Permissions);`
  - `public sealed record PluginPublisher(string Name, string? Url, string? PublicKey);`
  - `public sealed record PluginUiSpec(string Assembly, string EntryType);`
  - `public sealed record PluginBackendSpec(string Executable, IReadOnlyList<string> Args, string ActivateOn, bool AcceptsMultipleClients);` with `ActivateOn` in `first-call` (default) or `startup`.
  - `public sealed record PluginContributions(IReadOnlyList<PluginCommandSpec> Commands, IReadOnlyList<PluginContextMenuSpec> ContextMenu, IReadOnlyList<PluginToolbarSpec> Toolbar, IReadOnlyList<PluginArchiveFormatSpec> ArchiveFormats);`
  - `public sealed record PluginCommandSpec(string Id, string Label, string Group, string? Shortcut);`
  - `public sealed record PluginContextMenuSpec(string Group, IReadOnlyList<PluginContextMenuItemSpec> Items);` and `public sealed record PluginContextMenuItemSpec(string Command, string Label, string When);`
  - `public sealed record PluginToolbarSpec(string Command, string Label, string Glyph, string Placement);` with `Placement` in `primary` or `overflow`.
  - `public sealed record PluginArchiveFormatSpec(string Extension, bool CanList, bool CanExtract, bool CanCreate, bool CanModify, string EngineName);`
  - `public sealed record PluginSettingSpec(string Key, string Type, string Label, string? Description, JsonElement? Default, string? Group, IReadOnlyList<string>? Options);` with `Type` in `boolean`, `string`, `integer`, `enum`, `path`.
  - `public sealed record ManifestReadResult(PluginManifest? Manifest, IReadOnlyList<string> Errors) { public bool IsValid => Manifest is not null; }`
  - `public static class PluginManifestReader { public static ManifestReadResult Read(string json, string folderName); public static bool IsSdkCompatible(string sdkVersion) /* major == PluginSdk.Major */; }`
- The PluginHost csproj references `SimpleFile.PluginSdk`, `SimpleFile.Ipc`, `SimpleFile.Core`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Read_ValidManifest_ReturnsAllFields()            // full sample with every section; asserts Id, Backend.ActivateOn == "startup", two commands, one archive format ".hello"
[Theory]
[InlineData("Com.Example")] [InlineData("a")] [InlineData("..")] [InlineData("ex/ample")] [InlineData("ex\\ample")]
public void Read_RejectsBadIds(string id)                            // Errors contains "id"
[Fact] public void Read_RejectsFolderNameMismatch()                  // id "com.example.a", folder "com.example.b" -> error mentions folder
[Fact] public void Read_RejectsMissingRequiredFields()                // no name, no version, no sdkVersion, no publisher.name -> four errors
[Fact] public void Read_RejectsIncompatibleSdkMajor()                 // sdkVersion "2.0.0" -> error contains "sdkVersion"
[Fact] public void Read_RejectsDuplicateCommandIds()                  // two commands "greet" -> error contains "greet"
[Fact] public void Read_RejectsUnknownEnumValues()                    // activateOn "always", placement "left", settings type "float" -> three errors
[Fact] public void Read_IgnoresUnknownTopLevelProperties()            // "panels": [] present -> IsValid
[Fact] public void Read_DefaultsOptionalSections()                    // no contributions, no settings -> empty lists, Backend null, Ui null
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~PluginManifestReaderTests"`
Expected: FAIL, types missing.

- [ ] **Step 3: Implement the project, records and reader**

Use `System.Text.Json` with `JsonDocument`, camelCase property names, case-sensitive. Collect every error rather than stopping at the first.

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Tests src-winui/SimpleFile.sln
git commit -m "Add the plugin manifest model and reader"
```

---

### Task 3: Discovery and shadowing

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/Discovery/DiscoveredPlugin.cs`, `Discovery/PluginDiscovery.cs`
- Test: `src-winui/SimpleFile.Tests/PluginDiscoveryTests.cs`

**Interfaces:**
- Consumes: `PluginManifestReader.Read`.
- Produces, namespace `SimpleFile.PluginHost.Discovery`:
  - `public enum DiscoveryState { Valid, InvalidManifest, IncompatibleSdk, Shadowed }`
  - `public sealed record DiscoveredPlugin(string Id, string Folder, string Root, DiscoveryState State, PluginManifest? Manifest, IReadOnlyList<string> Errors, string? ShadowedByFolder);`
  - `public static class PluginDiscovery { public static IReadOnlyList<DiscoveredPlugin> Scan(IReadOnlyList<string> roots); public static IReadOnlyList<string> DefaultRoots(); }`
- Folder name is the id. A folder with no `plugin.json` is skipped silently. Manifest errors that mention `sdkVersion` map to `IncompatibleSdk`, all other errors to `InvalidManifest`. When the same id appears in two roots, the earlier root's entry gets `Shadowed` with `ShadowedByFolder` set, and the later root's entry is the live one. Output is sorted by id ordinal.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Scan_FindsValidPluginsAcrossRoots()
[Fact] public void Scan_SkipsFoldersWithoutManifest()
[Fact] public void Scan_MarksInvalidManifestWithErrors()
[Fact] public void Scan_MarksIncompatibleSdk()
[Fact] public void Scan_LaterRootShadowsEarlierRoot()      // install + user copies of "com.example.a": install entry Shadowed, ShadowedByFolder == user folder; user entry Valid
[Fact] public void Scan_MissingRootIsNotAnError()
[Fact] public void DefaultRoots_InstallThenLocalAppData()
```

Each test builds temp folders under `Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"))` and deletes them in `finally`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginDiscoveryTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `PluginDiscovery`**

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Tests
git commit -m "Discover plugin folders and resolve shadowed ids"
```

---

### Task 4: Approval store, package hash and signature verification

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/Trust/PluginPackageHash.cs`, `Trust/PluginSignatureVerifier.cs`, `Trust/PluginApprovalStore.cs`
- Modify: `src-winui/SimpleFile.PluginHost/SimpleFile.PluginHost.csproj` (add `BouncyCastle.Cryptography` 2.5.1), `packages.lock.json`
- Test: `src-winui/SimpleFile.Tests/PluginTrustTests.cs`

**Interfaces:**
- Produces, namespace `SimpleFile.PluginHost.Trust`:
  - `public static class PluginPackageHash { public static string ComputeManifestHash(string folder); /* lowercase hex SHA-256 of plugin.json bytes */ }`
  - `public enum SignatureStatus { Unsigned, Verified, Invalid }`
  - `public sealed record SignatureState(SignatureStatus Status, string? KeyFingerprint, string? Error);` where `KeyFingerprint` is the first 16 hex chars of SHA-256 over the raw public key.
  - `public static class PluginSignatureVerifier { public static SignatureState Verify(string folder, PluginManifest manifest); public static byte[] CanonicalPayload(string folder, IReadOnlyDictionary<string, string> files); }`
  - `signature.json` shape: `{ "algorithm": "ed25519", "files": { "<relative path>": "<sha256 hex>" }, "signature": "<base64>" }`. Canonical payload = UTF-8 bytes of `plugin.json` ++ `"\n"` ++ for each file sorted ordinal `"<path>\n<sha256>\n"`. Verification also recomputes every listed file's hash; a mismatch or a listed file that is missing yields `Invalid`. A missing `publisher.publicKey` with a present `signature.json` yields `Invalid`.
  - `public enum ApprovalState { NeedsConsent, Enabled, Disabled }`
  - `public sealed record ApprovalRecord(string ManifestHash, SignatureStatus SignatureStatus, ApprovalState State, DateTimeOffset UpdatedAt);`
  - `public sealed class PluginApprovalStore { public PluginApprovalStore(string path); public static string DefaultPath(); public ApprovalState Resolve(string pluginId, string manifestHash, SignatureStatus signature); public void Set(string pluginId, string manifestHash, SignatureStatus signature, ApprovalState state); public void Remove(string pluginId); }` — `Resolve` returns `NeedsConsent` when there is no record or the stored hash or signature status differs from the current one. The file is JSON, written atomically via temp file and `File.Move(overwrite: true)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Verify_NoSignatureFile_IsUnsigned()
[Fact] public void Verify_ValidSignature_IsVerifiedWithFingerprint()      // generate an Ed25519 key pair with BouncyCastle in the test, sign CanonicalPayload, write signature.json
[Fact] public void Verify_TamperedListedFile_IsInvalid()
[Fact] public void Verify_TamperedManifest_IsInvalid()
[Fact] public void Verify_SignatureWithoutPublicKey_IsInvalid()
[Fact] public void Resolve_NoRecord_NeedsConsent()
[Fact] public void Resolve_RecordMatches_ReturnsStoredState()
[Fact] public void Resolve_HashChanged_NeedsConsent()
[Fact] public void Resolve_SignatureStatusChanged_NeedsConsent()
[Fact] public void Set_PersistsAcrossInstances()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginTrustTests"`
Expected: FAIL.

- [ ] **Step 3: Add the BouncyCastle package, implement the three classes**

Run `dotnet restore src-winui/SimpleFile.PluginHost/SimpleFile.PluginHost.csproj --force-evaluate` so the lock file updates. Use `Org.BouncyCastle.Crypto.Signers.Ed25519Signer` with `Ed25519PublicKeyParameters`.

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Tests
git commit -m "Add plugin approval storage and Ed25519 signature verification"
```

---

### Task 5: Inbound requests on the pipe client

**Files:**
- Modify: `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.cs:347-376` (`HandlePayload`, `IsNotification`), `src-winui/SimpleFile.Ipc/ISimpleFileIpc.cs`
- Modify: `src-winui/SimpleFile.Tests/FakeIpcServer.cs`
- Test: `src-winui/SimpleFile.Tests/NamedPipeJsonClientTests.cs`

**Interfaces:**
- Produces on `NamedPipeJsonClient` and `ISimpleFileIpc`:
  - `public delegate Task<object?> InboundRequestHandler(JsonElement @params, CancellationToken ct);`
  - `IDisposable OnRequest(string method, InboundRequestHandler handler);`
- Behaviour: a frame with a string `method` and a numeric `id` and no `result`/`error` is an inbound request. The client runs the handler on the thread pool and writes `{jsonrpc, id, result}` or `{jsonrpc, id, error:{code,message}}`. No handler registered: error `Protocol.ErrMethodNotFound`, message `"Unknown host method '<method>'."`. Handler throws `IpcException`: its code and message. Handler throws anything else: `Protocol.ErrInternal` with the exception message. Frames with `method` and null or absent `id` stay notifications.
- `FakeIpcServer` gains `Task SendRequestAsync(int id, string method, object? @params, CancellationToken ct = default)` and `Task<JsonElement> ReadResponseAsync(CancellationToken ct = default)` returning the parsed root.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task InboundRequest_DispatchesToHandlerAndReplies()          // server sends id 7 "host.log" {message:"hi"}; handler returns new { ok = true }; response id 7, result.ok == true
[Fact] public async Task InboundRequest_UnknownMethod_RepliesMethodNotFound()   // error.code == -32601
[Fact] public async Task InboundRequest_HandlerThrowsIpcException_RepliesWithItsCode()
[Fact] public async Task InboundRequest_HandlerThrows_RepliesInternalError()
[Fact] public async Task Notification_WithoutId_StillDispatchesToOn()           // existing On<T> path unaffected
[Fact] public async Task InboundRequest_DoesNotBlockConcurrentOutboundCall()    // handler awaits a TaskCompletionSource; meanwhile client InvokeAsync round-trips
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~NamedPipeJsonClientTests"`
Expected: new tests FAIL; existing ones PASS.

- [ ] **Step 3: Implement**

Change `IsNotification` so `method` + numeric `id` + no `result`/`error` returns false, then branch in `HandlePayload` to a new `DispatchInboundRequest(JsonElement root)` that looks up `_requestHandlers` (same locking pattern as `_handlers`) and writes the reply through `WriteFrameAsync`.

- [ ] **Step 4: Run the whole test project**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.Ipc src-winui/SimpleFile.Tests
git commit -m "Answer inbound JSON-RPC requests on the named-pipe client"
```

---

### Task 6: Plugin backend process host

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/Backend/IPluginProcessLauncher.cs`, `Backend/PluginBackendHost.cs`, `Backend/PluginBackendConnection.cs`
- Modify: `src-winui/SimpleFile.Core/JobObject.cs` (`internal sealed class` to `public sealed class`; `Create`, `Assign`, `TryAssign`, `Dispose` public)
- Modify: `src-winui/SimpleFile.Tests/FakeIpcServer.cs` (add `Create(string pipeName)` overload)
- Test: `src-winui/SimpleFile.Tests/PluginBackendHostTests.cs`

**Interfaces:**
- Consumes: `NamedPipeJsonClient.ConnectAsync`, `OnRequest` (Task 5), `JobObject`.
- Produces, namespace `SimpleFile.PluginHost.Backend`:
  - `public interface IPluginProcess : IDisposable { int Id { get; } bool HasExited { get; } event EventHandler? Exited; void Kill(); }`
  - `public interface IPluginProcessLauncher { IPluginProcess Launch(string executable, IReadOnlyList<string> args, string workingDirectory, Action<string> onStderrLine); }`
  - `public sealed class ProcessLauncher : IPluginProcessLauncher` — real `System.Diagnostics.Process` with `CreateNoWindow`, stderr redirected, assigned to a fresh `JobObject.Create()` per process; the job is disposed with the `IPluginProcess`.
  - `public sealed record PluginHandshakeResult(string PluginId, string PluginVersion, string SdkVersion);`
  - `public sealed class PluginBackendConnection : IAsyncDisposable { public PluginHandshakeResult Handshake { get; } public Task<T> InvokeAsync<T>(string method, object? args, CancellationToken ct); public IDisposable On<T>(string eventName, Action<T> handler); public IDisposable OnHostRequest(string method, InboundRequestHandler handler); public event EventHandler<Exception?>? Lost; }`
  - `public sealed class PluginBackendHost { public PluginBackendHost(IPluginProcessLauncher launcher, Func<string, string> pipeNameFor, string dataRoot, Action<string, string> log); public Task<PluginBackendConnection> StartAsync(DiscoveredPlugin plugin, CancellationToken ct); }`
- Start sequence: compute pipe name, create data dir, launch `<folder>\<executable>` with args `--pipe-name <name> --data-dir <dir>` plus manifest args, connect with `TimeSpan.FromSeconds(5)` (retry connect every 100 ms until the deadline since the server may not exist yet), then `plugin.handshake` with params `{ pluginId, hostVersion, sdkVersion }` expecting `PluginHandshakeResult`. On any failure: kill the process, throw `PluginBackendException(message)`. Handshake result `pluginId` mismatch or `sdkVersion` major mismatch is a failure. Process exit raises `Lost`.

- [ ] **Step 1: Write the failing tests**

Tests use a `FakePluginProcessLauncher` that records the launch and, on launch, starts a `FakeIpcServer.Create(pipeName)` and a background task answering requests per test.

```csharp
[Fact] public async Task StartAsync_LaunchesWithPipeAndDataDirArgs()           // args contain "--pipe-name" <name> and "--data-dir" ending in plugin id
[Fact] public async Task StartAsync_HandshakeSucceeds_ReturnsConnection()
[Fact] public async Task StartAsync_HandshakeNeverAnswered_FailsWithinTimeoutAndKills()  // server accepts but never replies; use a 1 s timeout override via internal ctor; asserts elapsed < 3 s and process.Kill called
[Fact] public async Task StartAsync_NoServer_FailsAfterConnectTimeoutAndKills()
[Fact] public async Task StartAsync_WrongPluginId_Fails()
[Fact] public async Task StartAsync_SdkMajorMismatch_Fails()
[Fact] public async Task Connection_ProcessExit_RaisesLost()
[Fact] public async Task Connection_HostRequestFromBackend_IsAnswered()       // server sends request "host.log"; OnHostRequest handler replies
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginBackendHostTests"`
Expected: FAIL.

- [ ] **Step 3: Implement; make `JobObject` public**

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Core/JobObject.cs src-winui/SimpleFile.Tests
git commit -m "Spawn plugin backends in their own job and handshake over the pipe"
```

---

### Task 7: Host methods for backends and the IPluginHost implementation

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/Backend/PluginHostMethods.cs`, `src-winui/SimpleFile.PluginHost/PluginHostServices.cs`, `src-winui/SimpleFile.PluginHost/IPluginProgressSink.cs`
- Test: `src-winui/SimpleFile.Tests/PluginHostMethodsTests.cs`

**Interfaces:**
- Consumes: `ISettingsBackend` from `SimpleFile.Core` (`GetSettingAsync`, `SetSettingAsync`), `PluginBackendConnection`.
- Produces:
  - `public interface IPluginProgressSink { string Begin(string pluginId, string title, Action cancel); void Report(string operationId, long current, long total, string? detail); void Complete(string operationId); void Fail(string operationId, string message); }` — the App implements this over `TransferManagerViewModel` in Task 11; tests use a recording fake.
  - `public interface IPluginNotifier { void Notify(string pluginId, string message, bool isError); }`
  - `public static class PluginHostMethods { public const string GetSettings = "host.get_settings"; public const string SetSetting = "host.set_setting"; public const string ProgressBegin = "host.progress_begin"; public const string ProgressReport = "host.progress_report"; public const string ProgressComplete = "host.progress_complete"; public const string ProgressFail = "host.progress_fail"; public const string Log = "host.log"; public const string Notify = "host.notify"; public static void Register(PluginBackendConnection connection, string pluginId, ISettingsBackend settings, IPluginProgressSink progress, IPluginNotifier notifier, Action<string, string> log); public static string SettingsKey(string pluginId, string key); public static bool IsValidSettingKey(string key); }`
  - Params and results: `host.get_settings {keys:[string]} -> {values:{key:string|null}}`; `host.set_setting {key,value} -> null`; `host.progress_begin {title} -> {operationId}`; `host.progress_report {operationId,current,total,detail?} -> null`; `host.progress_complete {operationId}`; `host.progress_fail {operationId,message}`; `host.log {level:"info"|"warn"|"error", message}`; `host.notify {message, isError?}`. An invalid key throws `IpcException(Protocol.ErrInvalidParams, ...)`. An unknown `operationId` throws `IpcException(Protocol.ErrInvalidParams, ...)`.
  - `public sealed class PluginHostServices : IPluginHost` constructed with `(string pluginId, Func<CancellationToken, Task<PluginBackendConnection>> backend, ISettingsBackend settings, IPluginProgressSink progress, IPluginDialogs dialogs, IPluginWorkspace workspace, Action<string,string> log)`. `InvokeBackendAsync` lazily starts the backend through the factory. `Settings` prefixes keys with `SettingsKey`. `Progress.Begin` returns an `IPluginOperation` whose `CancellationToken` cancels when the sink's `cancel` callback fires.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task GetSettings_PrefixesKeysWithPluginId()           // backend asks ["theme"]; settings fake receives "plugin.com.example.a.theme"
[Fact] public async Task SetSetting_InvalidKey_IsInvalidParams()         // "a.b", "", 65 chars, "../x" -> IpcException code -32602; settings fake untouched
[Fact] public async Task Progress_BeginReportComplete_FlowsToSink()
[Fact] public async Task Progress_UnknownOperation_IsInvalidParams()
[Fact] public async Task Log_WritesWithPluginId()
[Fact] public async Task Notify_ReachesNotifier()
[Fact] public async Task Services_Settings_UsesPrefixedKey()
[Fact] public async Task Services_InvokeBackend_StartsBackendOnce()       // factory invoked once across two calls
[Fact] public void Services_ProgressOperation_CancelFromSinkCancelsToken()
```

Drive the handlers through a `FakeIpcServer.SendRequestAsync` into a real `PluginBackendConnection` from Task 6, or through a small `InboundRequestDispatcher` the handlers register on. Either way the assertions above hold.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginHostMethodsTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Tests
git commit -m "Serve host.* requests to plugin backends and implement IPluginHost"
```

---

### Task 8: Assembly loading and the fixture plugin's C# half

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/Loading/PluginLoadContext.cs`, `Loading/PluginActivator.cs`
- Create: `src-winui/SimpleFile.Tests/Fixtures/HelloPlugin/SimpleFile.Tests.Fixtures.HelloPlugin.csproj`, `HelloPlugin.cs`, `plugin.json`
- Modify: `src-winui/SimpleFile.sln`, `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj`
- Test: `src-winui/SimpleFile.Tests/PluginLoadingTests.cs`

**Interfaces:**
- Produces, namespace `SimpleFile.PluginHost.Loading`:
  - `public sealed class PluginLoadContext : AssemblyLoadContext { public PluginLoadContext(string pluginFolder, string mainAssemblyPath); }` — non-collectible; `Load` returns null (defer to default context) for assembly names starting with `SimpleFile.PluginSdk`, `Microsoft.WindowsAppSDK`, `Microsoft.UI`, `Microsoft.Windows`, `WinRT`, `CommunityToolkit.Mvvm`, `System.`, `Microsoft.Extensions.`; everything else resolves through an `AssemblyDependencyResolver` for the main assembly path.
  - `public static class PluginActivator { public static IPlugin CreateInstance(DiscoveredPlugin plugin); }` — loads `Ui.Assembly` from the plugin folder, finds `Ui.EntryType`, requires a public parameterless constructor and `IPlugin`; failures throw `PluginLoadException(string message, Exception? inner)`.
- Fixture `HelloPlugin` (`SimpleFile.Tests.Fixtures.HelloPlugin.HelloPlugin : IPlugin, ICommandHandler`): on `ActivateAsync` stores the host and sets `static bool Activated`; `ExecuteAsync` records `context.CommandId` into `static List<string> ExecutedCommands` and for command `plugin:com.sumafile.hello:backend-ping` calls `host.InvokeBackendAsync<JsonElement>("ping", null, ct)` and logs the result. The fixture project references only `SimpleFile.PluginSdk`. Its `plugin.json`:

```json
{
  "id": "com.sumafile.hello",
  "name": "Hello fixture",
  "version": "0.1.0",
  "publisher": { "name": "SumaFile Team" },
  "sdkVersion": "1.0.0",
  "ui": { "assembly": "SimpleFile.Tests.Fixtures.HelloPlugin.dll", "entryType": "SimpleFile.Tests.Fixtures.HelloPlugin.HelloPlugin" },
  "backend": { "executable": "hello-plugin-backend.exe", "args": [], "activateOn": "first-call", "acceptsMultipleClients": true },
  "permissions": ["backend-process"],
  "contributions": {
    "commands": [
      { "id": "greet", "label": "Hello: greet", "group": "Hello" },
      { "id": "backend-ping", "label": "Hello: ping backend", "group": "Hello" }
    ],
    "contextMenu": [ { "group": "Hello", "items": [ { "command": "greet", "label": "Say hello", "when": "selectionCount >= 1" } ] } ],
    "toolbar": [ { "command": "greet", "label": "Hello", "glyph": "\uE899", "placement": "overflow" } ],
    "archiveFormats": [ { "extension": ".hello", "canList": true, "canExtract": true, "canCreate": true, "canModify": false, "engineName": "hello" } ]
  },
  "settings": [
    { "key": "greeting", "type": "string", "label": "Greeting", "default": "Hello", "group": "General" },
    { "key": "loud", "type": "boolean", "label": "Shout", "default": false, "group": "General" }
  ]
}
```

- The test csproj references the fixture project with `ReferenceOutputAssembly="false"` and a post-build `Copy` of the fixture output plus `plugin.json` into `$(OutDir)Fixtures\plugins\com.sumafile.hello\`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void CreateInstance_LoadsFixtureAndReturnsIPlugin()         // DiscoveredPlugin built by PluginDiscovery.Scan([Fixtures/plugins]); instance.GetType().FullName == entryType
[Fact] public void CreateInstance_SdkTypeUnifies()                         // typeof(IPlugin).IsAssignableFrom(instance.GetType()) and instance is IPlugin (same SDK assembly)
[Fact] public void CreateInstance_MissingAssembly_ThrowsPluginLoadException()
[Fact] public void CreateInstance_MissingType_ThrowsPluginLoadException()
[Fact] public async Task Fixture_ExecuteGreet_RecordsCommand()             // host = fake IPluginHost; ExecutedCommands contains the id
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginLoadingTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the load context, activator and fixture project**

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Tests src-winui/SimpleFile.sln
git commit -m "Load plugin assemblies in their own context and add the Hello fixture plugin"
```

---

### Task 9: Plugin manager state machine

**Files:**
- Create: `src-winui/SimpleFile.PluginHost/PluginRecord.cs`, `PluginManager.cs`
- Test: `src-winui/SimpleFile.Tests/PluginManagerTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 4, 6, 7, 8.
- Produces, namespace `SimpleFile.PluginHost`:
  - `public enum PluginState { NeedsConsent, Enabled, Disabled, IncompatibleSdk, InvalidManifest, LoadFailed, Faulted, Shadowed }`
  - `public sealed class PluginRecord { public string Id; public DiscoveredPlugin Discovered; public PluginState State; public SignatureState Signature; public string? Error; public bool BackendRunning; }` (get-only outside the manager).
  - `public sealed class PluginManagerOptions { public IReadOnlyList<string> Roots; public string ApprovalStorePath; public string DataRoot; public IPluginProcessLauncher Launcher; public Func<string, string> PipeNameFor; public ISettingsBackend Settings; public IPluginProgressSink Progress; public IPluginNotifier Notifier; public Func<string, IPluginDialogs> DialogsFor; public IPluginWorkspace Workspace; public Action<string, string> Log; }`
  - `public sealed class PluginManager : IAsyncDisposable { public PluginManager(PluginManagerOptions options); public IReadOnlyList<PluginRecord> Plugins { get; } public event EventHandler? Changed; public Task DiscoverAsync(CancellationToken ct); public Task RescanAsync(CancellationToken ct); public Task EnableAsync(string id, CancellationToken ct); public Task DisableAsync(string id, CancellationToken ct); public Task RestartBackendAsync(string id, CancellationToken ct); public Task RunCommandAsync(string hostCommandId, PluginCommandContext context, CancellationToken ct); public IReadOnlyList<PluginRecord> EnabledPlugins { get; } public static string HostCommandId(string pluginId, string command) => $"plugin:{pluginId}:{command}"; public static bool TryParseHostCommandId(string id, out string pluginId, out string command); }`
- Rules: `DiscoverAsync` scans, verifies signatures, resolves approval, and sets each state. Only `Enabled` records contribute. `RunCommandAsync` activates the UI assembly on first use (`PluginActivator` + `ActivateAsync`), then calls the `ICommandHandler`; a load failure sets `LoadFailed` with `Error`. Backends start on first `InvokeBackendAsync`, or during `DiscoverAsync` when `ActivateOn == "startup"`. Three backend start failures within 60 s set `Faulted`. `Lost` on a running backend sets `Faulted` and raises `Changed`. `DisableAsync` stops the backend, calls `DeactivateAsync` when activated, writes `Disabled` to the approval store. `RescanAsync` re-runs discovery, keeps activated instances for unchanged enabled plugins, and removes records whose folders vanished. `DisposeAsync` stops every backend.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task Discover_NewPlugin_NeedsConsent()
[Fact] public async Task Enable_WritesApprovalAndContributes()
[Fact] public async Task Discover_ApprovedUnchangedPlugin_IsEnabled()
[Fact] public async Task Discover_ApprovedButManifestChanged_NeedsConsentAgain()
[Fact] public async Task RunCommand_ActivatesOnceThenExecutes()           // fixture from Task 8; HelloPlugin.Activated true; two runs -> one activation
[Fact] public async Task RunCommand_LoadFailure_SetsLoadFailedWithError()  // manifest pointing at missing dll
[Fact] public async Task Backend_StartupActivate_StartsDuringDiscover()    // fake launcher sees a launch during DiscoverAsync
[Fact] public async Task Backend_ThreeFailures_SetsFaulted()
[Fact] public async Task Backend_Lost_SetsFaultedAndRaisesChanged()
[Fact] public async Task Disable_StopsBackendAndPersists()
[Fact] public async Task Rescan_RemovedFolder_DropsRecordAndContributions()  // Review Focus item
[Fact] public void HostCommandId_RoundTrips()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginManagerTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run the tests**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Tests
git commit -m "Add the plugin manager that tracks consent, loading and backend state"
```

---

### Task 10: Contribution slots in Core

**Files:**
- Modify: `src-winui/SimpleFile.Core/AppCommandCatalog.cs`, `src-winui/SimpleFile.Core/ContextMenuBuilder.cs:22-46,53-115,117-140`, `src-winui/SimpleFile.Core/CommandSurfaceLayout.cs:235-293`
- Create: `src-winui/SimpleFile.Core/PluginMenuWhen.cs`, `src-winui/SimpleFile.PluginHost/Contributions/PluginContributionAdapter.cs`
- Test: `src-winui/SimpleFile.Tests/PluginMenuWhenTests.cs`, `src-winui/SimpleFile.Tests/ContextMenuBuilderTests.cs`, `src-winui/SimpleFile.Tests/AppCommandCatalogTests.cs`, `src-winui/SimpleFile.Tests/PluginContributionAdapterTests.cs`

**Interfaces:**
- Produces in `SimpleFile.Core`:
  - `AppCommandCatalog`: rename the existing list to `public static readonly IReadOnlyList<AppCommand> BuiltIn`; add `public static IReadOnlyList<AppCommand> All { get; }` (BuiltIn followed by registered plugin commands), `public static void SetPluginCommands(IReadOnlyList<AppCommand> commands)`, `public static event EventHandler? Changed`.
  - `public sealed record PluginContextMenuGroup(string PluginId, string Label, IReadOnlyList<PluginContextMenuItem> Items);` and `public sealed record PluginContextMenuItem(string HostCommandId, string Label, string When);`
  - `ContextMenuRequest` gains `public IReadOnlyList<PluginContextMenuGroup> PluginMenus { get; init; } = [];`, `public IReadOnlySet<string> FolderFlags { get; init; }` (format `<pluginId>:<flag>`), `public IReadOnlySet<string> SelectionDecorationPlugins { get; init; }`. `Build` and `BuildBackgroundMenu` append one submenu per group after `GitMenu`, entry id `ctx-plugin-menu:<pluginId>`, items with id equal to `HostCommandId`, hidden when `When` evaluates false; a group with no visible items is hidden.
  - `public static class PluginMenuWhen { public static bool Evaluate(string expression, string pluginId, ContextMenuRequest request); public static bool IsValid(string expression, out string error); }` — grammar: `&&`, `||`, `!`, parentheses; atoms `selectionCount <op> <int>` with `== != < <= > >=`, `singleFile`, `singleFolder`, `hasExtension(.ext)` (case-insensitive against `SelectedExtension`), `hasDecoration` (true when `SelectionDecorationPlugins` contains `pluginId`), `folderFlag(name)` (true when `FolderFlags` contains `<pluginId>:<name>`). Empty expression is true. Invalid expression evaluates false.
  - `ToolbarActionCatalog`: `All` becomes a property backed by a field rebuilt by `public static void SetPluginActions(IReadOnlyList<ToolbarAction> actions)`; `public static event EventHandler? Changed`.
  - `CommandSurfaceLayout`: `public bool EnsurePrimaryItem(string commandId)` appends `CommandSurfaceItem.Command(id)` before the Settings item when absent, returns true when it changed the layout.
- Produces in `SimpleFile.PluginHost.Contributions`:
  - `public static class PluginContributionAdapter { public static IReadOnlyList<AppCommand> Commands(IEnumerable<PluginRecord> enabled); public static IReadOnlyList<PluginContextMenuGroup> ContextMenus(IEnumerable<PluginRecord> enabled); public static IReadOnlyList<ToolbarAction> ToolbarActions(IEnumerable<PluginRecord> enabled); public static IReadOnlyList<string> PrimaryToolbarCommandIds(IEnumerable<PluginRecord> enabled); }` — command ids go through `PluginManager.HostCommandId`.

- [ ] **Step 1: Write the failing tests**

```csharp
// PluginMenuWhenTests
[Theory]
[InlineData("", 0, true)] [InlineData("selectionCount >= 1", 1, true)] [InlineData("selectionCount == 2 && singleFile", 2, false)]
[InlineData("!singleFolder || selectionCount > 5", 1, true)] [InlineData("hasExtension(.TXT)", 1, true)] [InlineData("selectionCount >", 1, false)]
public void Evaluate_Cases(string when, int selectionCount, bool expected)   // request SelectedExtension "txt", SelectedIsDirectory false
[Fact] public void Evaluate_FolderFlag_UsesPluginPrefix()                     // FolderFlags {"com.a:inRepository"}; plugin "com.a" true, plugin "com.b" false
[Fact] public void Evaluate_HasDecoration_UsesPluginId()
// ContextMenuBuilderTests additions
[Fact] public void Build_AppendsPluginGroupsAfterGit()
[Fact] public void Build_HidesPluginItemWhenFalse_AndGroupWhenEmpty()
// AppCommandCatalogTests additions
[Fact] public void SetPluginCommands_AppendsAndRaisesChanged()
[Fact] public void SetPluginCommands_Empty_RestoresBuiltIn()
// PluginContributionAdapterTests
[Fact] public void Commands_PrefixIdsWithPlugin()                             // "plugin:com.sumafile.hello:greet"
[Fact] public void ToolbarActions_UseGlyphAndCommandId()
[Fact] public void PrimaryToolbarCommandIds_OnlyPrimaryPlacement()
// CommandSurfaceLayoutTests
[Fact] public void EnsurePrimaryItem_InsertsBeforeSettingsOnce()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginMenuWhen|FullyQualifiedName~ContextMenuBuilder|FullyQualifiedName~AppCommandCatalog|FullyQualifiedName~PluginContributionAdapter|FullyQualifiedName~CommandSurfaceLayout"`
Expected: new tests FAIL; existing PASS.

- [ ] **Step 3: Implement**

Write `PluginMenuWhen` as a small recursive-descent parser over a tokenizer; no library.

- [ ] **Step 4: Run the whole test project**

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src-winui/SimpleFile.Core src-winui/SimpleFile.PluginHost src-winui/SimpleFile.Tests
git commit -m "Let plugins contribute commands, context-menu groups and toolbar actions"
```

---

### Task 11: MainWindow wiring, consent dialog, progress sink, status center

**Files:**
- Create: `src-winui/SimpleFile.App/MainWindow.Plugins.cs`, `src-winui/SimpleFile.App/PluginConsentDialog.xaml`, `PluginConsentDialog.xaml.cs`, `src-winui/SimpleFile.App/PluginProgressSink.cs`, `src-winui/SimpleFile.App/PluginDialogs.cs`, `src-winui/SimpleFile.App/PluginWorkspaceView.cs`
- Modify: `src-winui/SimpleFile.App/SimpleFile.App.csproj` (reference `SimpleFile.PluginHost`, `SimpleFile.PluginSdk`), `MainWindow.xaml.cs:203-246`, `MainWindow.Commands.cs:884-915,1021-1026`, `MainWindow.CommandRouting.cs:10-22`, `MainWindow.CommandSurface.cs` (call `ApplyCommandSurfaceLayout` on catalog change), `MainWindow.StatusCenter.cs`
- Modify: `src-winui/SimpleFile.Core/StartupTimingBudget.cs`, `scripts/check-winui-startup-budget.mjs`
- Test: `src-winui/SimpleFile.Tests/StartupTimingBudgetTests.cs`

**Interfaces:**
- Consumes: `PluginManager`, `PluginContributionAdapter`, `IPluginProgressSink`, `IPluginNotifier`, `TransferManagerViewModel.Enqueue` and `TransferOperationViewModel.ApplyProgress`/`SetErrorMessage` (`src-winui/SimpleFile.Core/TransferManagerViewModel.cs:245-260,525-545`).
- Produces:
  - `MainWindow.Plugins.cs`: `private PluginManager? _plugins; private async Task InitializePluginsAsync(FileOperationService fileOps, StartupTimer timer)` called in `ConnectAsync` right after `open-with-preferences`; it builds `PluginManagerOptions` with `ProcessLauncher`, `PluginDiscovery.DefaultRoots()`, `PluginApprovalStore.DefaultPath()`, pipe names `SumaFile.Plugin.{sessionId}.{pid}.{pluginId}`, then `await _plugins.DiscoverAsync(ct)`, then `timer.Mark("plugins-discovered", $"count={_plugins.Plugins.Count}")`, then `ApplyPluginContributions()`. Consent dialogs for `NeedsConsent` records are shown after `ready`, one at a time, from `QueueDeferredStartupNavigation`'s continuation so they never block first paint.
  - `private void ApplyPluginContributions()`: `AppCommandCatalog.SetPluginCommands`, `ToolbarActionCatalog.SetPluginActions`, `EnsurePrimaryItem` for primary placements followed by `ApplyCommandSurfaceLayout()`, and stores `_pluginMenus` used by `BuildContextMenuRequest` (`PluginMenus = _pluginMenus`). Subscribed to `_plugins.Changed`.
  - `RunAppCommandAsync` / the context-menu switch: when `PluginManager.TryParseHostCommandId(id, ...)` succeeds, call `_plugins.RunCommandAsync(id, new PluginCommandContext(id, _workspace.Active.Path, SelectedPaths()), ct)` inside `RunUiActionAsync("Plugin")`.
  - `PluginConsentDialog : ContentDialog` showing name, publisher, version, permissions, backend presence, signature line ("Verified publisher · <fingerprint>" / "Unsigned plugin" / "Invalid signature: <error>"), primary "Enable", close "Keep disabled".
  - `PluginProgressSink : IPluginProgressSink` adapting to `TransferManagerViewModel`; `PluginDialogs : IPluginDialogs` over `DialogHost`; `PluginWorkspaceView : IPluginWorkspace` over `ExplorerWorkspace`.
  - Status center: a `Faulted` record adds a row "Plugin <name> stopped" with a Restart command calling `RestartBackendAsync`.
  - `StartupTimingBudget`: `public const double PluginDiscoveryMaxDeltaMs = 50;` and `Evaluate` adds a violation when stage `MainWindow.Connect.plugins-discovered` has `delta_ms` above it; the stage is optional (absent is not a violation). `check-winui-startup-budget.mjs` mirrors: `pluginsDiscoveredDeltaMs: 50` checked with a new `checkMaximumDelta`.

- [ ] **Step 1: Write the failing budget tests**

```csharp
[Fact] public void Evaluate_FlagsSlowPluginDiscovery()      // plugins-discovered delta 80 -> violation mentions "plugins-discovered"
[Fact] public void Evaluate_PassesWithoutPluginStage()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~StartupTimingBudgetTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the budget change in C# and the mjs script, then the MainWindow wiring**

- [ ] **Step 4: Build and run everything**

Run: `dotnet build src-winui/SimpleFile.sln -c Debug` then `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo`
Expected: build succeeds with zero warnings; tests PASS.

- [ ] **Step 5: Manual check**

Run: `npm run dev:winui` with `Fixtures\plugins` copied to `%LOCALAPPDATA%\SumaFile\plugins`. Expected: consent dialog for "Hello fixture" appears after the window is ready; after Enable, the command palette lists "Hello: greet"; right-click on a file shows a "Hello" submenu; the Transfers window is unchanged.

- [ ] **Step 6: Commit**

```bash
git add src-winui scripts/check-winui-startup-budget.mjs
git commit -m "Discover plugins at startup, ask for consent and route their commands"
```

---

### Task 12: Settings > Plugins

**Files:**
- Create: `src-winui/SimpleFile.App/SettingsWindow.Plugins.cs`, `src-winui/SimpleFile.App/PluginSettingsPanelBuilder.cs`, `src-winui/SimpleFile.PluginHost/Install/PluginPackageInstaller.cs`
- Modify: `src-winui/SimpleFile.App/SettingsWindow.xaml:47-55,558-574` (nav item `Plugins`, a `PluginsPanel`), `SettingsWindow.xaml.cs:167-200` (`ShowCategory`, `CategoryPanels`), `SettingsWindow.SettingsState.cs:63` (`LoadSettingsAsync` gains a `PluginManager?` parameter), `src-winui/SimpleFile.App/FileOperationDialogService.cs:599-615`
- Test: `src-winui/SimpleFile.Tests/PluginPackageInstallerTests.cs`, `src-winui/SimpleFile.Tests/PluginSettingsSchemaTests.cs`

**Interfaces:**
- Produces:
  - `public static class PluginPackageInstaller { public static string Install(string sfpluginPath, string userRoot); /* returns the installed folder */ }` — opens the zip, requires `plugin.json` at the root, reads it with `PluginManifestReader` to learn the id, rejects any entry whose normalized full path is not under `<userRoot>\<id>\` (throws `PluginInstallException`), extracts into a temp folder next to the target, then moves it into place replacing an existing folder. Nothing is written when validation fails.
  - `public static class PluginSettingsSchema { public static string? ValidateValue(PluginSettingSpec spec, string value); /* null when valid, else message */ public static string DefaultValue(PluginSettingSpec spec); }` in `SimpleFile.PluginHost`.
  - `PluginSettingsPanelBuilder.Build(PluginRecord record, Func<string, Task<string?>> read, Func<string, string, Task> write) -> FrameworkElement`: a card with the name, version, publisher, state text, signature line, an enable `ToggleSwitch`, Remove (user root only, with `ContentDialog` confirmation) and the declared controls: `boolean` → `ToggleSwitch`, `string` → `TextBox`, `integer` → `NumberBox`, `enum` → `ComboBox`, `path` → `TextBox` plus Browse. Each control has a `Header` so settings search indexes it. Values round-trip through `FileOperationService.GetSettingAsync/SetSettingAsync` with `PluginHostMethods.SettingsKey`.
  - `PluginsPanel` top bar: "Install from file…" (`FileOpenPicker` filtered to `.sfplugin`, then `PluginPackageInstaller.Install`, then `RescanAsync`), "Rescan", "Open plugins folder" (`Process.Start("explorer.exe", userRoot)`). A plugin `NeedsConsent` row has an "Review…" button opening `PluginConsentDialog`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Install_ValidPackage_ExtractsIntoIdFolder()
[Fact] public void Install_ReplacesExistingFolder()
[Fact] public void Install_MissingManifest_Throws()
[Fact] public void Install_ZipSlipEntry_ThrowsAndWritesNothing()       // entry "..\\evil.txt" and entry "C:\\evil.txt"; Review Focus item
[Theory]
[InlineData("integer", "12", null)] [InlineData("integer", "x", "whole number")] [InlineData("boolean", "maybe", "true or false")] [InlineData("enum", "c", "one of")]
public void ValidateValue_Cases(string type, string value, string? expectedFragment)  // enum options ["a","b"]
[Fact] public void DefaultValue_UsesSpecDefaultOrTypeDefault()
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test ... --filter "FullyQualifiedName~PluginPackageInstallerTests|FullyQualifiedName~PluginSettingsSchemaTests"`
Expected: FAIL.

- [ ] **Step 3: Implement the installer, the schema helper, the panel and the Settings window changes**

Add `("Plugins", PluginsPanel)` to `CategoryPanels()` and a `case "Plugins"` to `ShowCategory`. `LoadSettingsAsync` stores the manager and calls `RenderPluginsPanel()`; it re-renders on `PluginManager.Changed` while the window is open.

- [ ] **Step 4: Run tests and build**

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo` and `dotnet build src-winui/SimpleFile.sln -c Debug`
Expected: PASS, zero warnings.

- [ ] **Step 5: Manual check**

Open Settings, Plugins category: the Hello fixture shows Enabled with "Greeting" and "Shout" controls. Type "Shout" in settings search: it jumps to Plugins. Toggle Enabled off: the palette loses "Hello: greet".

- [ ] **Step 6: Commit**

```bash
git add src-winui
git commit -m "Add the Plugins settings category with install, consent and generated controls"
```

---

### Task 13: Archive engine registry and plugin registration in the service

**Files:**
- Create: `crates/simplefile-core/src/archive/engine.rs`, `crates/simplefile-service/src/plugin_archive.rs`
- Modify: `crates/simplefile-core/src/archive/mod.rs`, `list.rs:11-26`, `extract.rs:12-18`, `create.rs:4-26`, `capabilities.rs`, `path.rs:87-96` (`is_archive_path`)
- Modify: `crates/simplefile-service/src/lib.rs`, `src/dispatch/handlers.rs`, `src/dispatch/params.rs`
- Modify: `ipc/schema/v1/commands.json` (two methods, `domainMethodCount` 99), `scripts/check-ipc-schema.mjs:113-117` (97 → 99), regenerate `crates/simplefile-ipc/src/protocol_generated.rs` and `src-winui/SimpleFile.Ipc/Protocol.Generated.cs` via `npm run generate:ipc-bindings`
- Test: `crates/simplefile-core/src/archive/tests.rs`, `crates/simplefile-service/src/dispatch/tests.rs`, `crates/simplefile-service/tests/plugin_archive.rs`

**Interfaces:**
- Produces in `simplefile_core::archive::engine` (re-exported from `archive`):
  - `pub struct EngineEntry { pub path: String, pub is_dir: bool, pub size: u64, pub compressed_size: u64 }`
  - `pub trait ArchiveEngine: Send + Sync { fn name(&self) -> &str; fn list_entries(&self, archive: &Path) -> Result<Vec<EngineEntry>, String>; fn extract(&self, archive: &Path, destination: &Path, inner_path: Option<&Path>) -> Result<(), String>; fn create(&self, archive: &Path, paths: &[PathBuf]) -> Result<(), String>; }`
  - `pub struct EngineFormat { pub extension: String, pub can_list: bool, pub can_extract: bool, pub can_create: bool, pub can_modify: bool }`
  - `pub fn register_engine(owner: &str, formats: Vec<EngineFormat>, engine: Arc<dyn ArchiveEngine>)`, `pub fn unregister_engine(owner: &str)`, `pub fn engine_for_path(path: &Path) -> Option<(Arc<dyn ArchiveEngine>, EngineFormat)>` (longest matching extension, case-insensitive, after built-in formats), `pub fn registered_formats() -> Vec<(String /*owner*/, EngineFormat, String /*engine name*/)>`. Storage is a process-global `RwLock<Vec<Registration>>` behind `OnceLock`.
  - `list_archive`, `extract_archive`, `create_archive` and `is_archive_path` consult `engine_for_path` when `archive_format_for_path` returns `None`. `list_archive` maps `EngineEntry` to `ArchiveEntry` reusing `archive_entry_relative_path_from_name` for the unsafe-entry split and sets `format` to the extension without the dot. `get_archive_capabilities` appends one `ArchiveFormatCapability` per registered format with `engine` = `plugin:<owner>` and `note: None`. In-pane browsing and mutation of plugin formats stay unsupported in this phase: `split_archive_path` ignores engine formats.
- Produces in `simplefile_service::plugin_archive`:
  - `pub struct PipeArchiveEngine` connecting synchronously to `\\.\pipe\<name>` with `std::fs::OpenOptions::new().read(true).write(true)` and the `simplefile_ipc::frame` helpers; one connection guarded by a `Mutex`; it sends `plugin.handshake` with `{ pluginId, hostVersion, sdkVersion: "1.0.0", client: "simplefile-service" }` on connect, then `archive.list { archivePath } -> { entries: [{ path, isDir, size, compressedSize }] }`, `archive.extract { archivePath, destination, innerPath? } -> null`, `archive.create { archivePath, paths } -> null`. A transport error marks the engine broken; the next call reconnects once.
  - Dispatch methods: `register_archive_engine { pluginId, pipeName, formats: [{ extension, canList, canExtract, canCreate, canModify, engineName }] } -> null` (blocking lane General; connects, handshakes, registers; connect failure is an application error and registers nothing) and `unregister_archive_engine { pluginId } -> null`.
- Schema: add both to `commands.json` with `"params"` shapes above and `"result": "null"`.

- [ ] **Step 1: Write the failing core tests**

```rust
#[test] fn engine_registry_resolves_by_extension_case_insensitively()
#[test] fn list_archive_uses_registered_engine()           // MockEngine returns two entries incl. "../evil" -> ArchiveInfo has one entry and one unsafe_entries
#[test] fn extract_and_create_forward_to_engine()
#[test] fn capabilities_include_registered_formats()       // engine == "plugin:com.test"
#[test] fn unregister_removes_formats()
#[test] fn is_archive_path_true_for_registered_extension()
```

Tests share the global registry, so each uses a unique extension and owner, and `unregister_engine` in a drop guard.

- [ ] **Step 2: Run them to verify they fail**

Run: `cargo test -p simplefile-core archive::tests::engine_ -- --nocapture` (name tests with the `engine_` prefix) 
Expected: compile FAIL.

- [ ] **Step 3: Implement `engine.rs` and the call sites**

- [ ] **Step 4: Run core tests**

Run: `cargo test -p simplefile-core`
Expected: PASS.

- [ ] **Step 5: Write the failing service tests**

In `crates/simplefile-service/tests/plugin_archive.rs`: spawn a tokio `NamedPipeServer` acting as the plugin that answers `plugin.handshake`, `archive.list` and `archive.extract`; dispatch `register_archive_engine` through `serve_connection` over an in-memory duplex; then dispatch `list_archive` on a temp file `x.hello` and assert the entries came from the fake. Also: `register_archive_engine` with a pipe nobody serves returns an application error and `get_archive_capabilities` does not list `.hello`. In `dispatch/tests.rs`: `unregister_archive_engine` for an unknown id is `Ok(null)`.

- [ ] **Step 6: Run them to verify they fail, implement, run again**

Run: `cargo test -p simplefile-service`
Expected: FAIL, then PASS.

- [ ] **Step 7: Schema and bindings**

Edit `commands.json`, bump `domainMethodCount` to 99, update the two `97` literals in `check-ipc-schema.mjs`, run `npm run generate:ipc-bindings`, then `npm run check:ipc-schema` and `npm run check:ipc-generated`. Expected: both print success. `Protocol.DomainMethodCount` is now 99, so update the `methods=84`-style detail in any test that asserts the handshake count against the constant (they use the constant already).

- [ ] **Step 8: Full Rust check and commit**

Run: `cargo fmt --all -- --check && cargo clippy --locked --all-targets --all-features -- -D warnings && cargo test --locked --all-features`
Expected: PASS.

```bash
git add crates ipc scripts/check-ipc-schema.mjs src-winui/SimpleFile.Ipc/Protocol.Generated.cs
git commit -m "Let the service register archive engines served over a plugin pipe"
```

---

### Task 14: Rust helper crate for plugin backends

**Files:**
- Create: `crates/sumafile-plugin/Cargo.toml`, `src/lib.rs`, `src/server.rs`, `src/host.rs`, `src/handshake.rs`, `tests/roundtrip.rs`
- Modify: `Cargo.toml` (workspace member)

**Interfaces:**
- Produces, crate `sumafile-plugin`:
  - `pub struct PluginInfo { pub id: String, pub version: String }`; `pub const SDK_VERSION: &str = "1.0.0";`
  - `pub struct Args { pub pipe_name: String, pub data_dir: PathBuf, pub rest: Vec<String> }` with `pub fn parse(args: impl Iterator<Item = String>) -> Result<Args, String>` understanding `--pipe-name <v>` and `--data-dir <v>`.
  - `pub struct HostClient` with `pub async fn get_settings(&self, keys: &[&str]) -> Result<HashMap<String, Option<String>>, String>`, `pub async fn set_setting(&self, key: &str, value: &str) -> Result<(), String>`, `pub async fn progress_begin(&self, title: &str) -> Result<String, String>`, `pub async fn progress_report(&self, id: &str, current: u64, total: u64, detail: Option<&str>) -> Result<(), String>`, `pub async fn progress_complete(&self, id: &str) -> Result<(), String>`, `pub async fn progress_fail(&self, id: &str, message: &str) -> Result<(), String>`, `pub async fn log(&self, level: &str, message: &str) -> Result<(), String>`, `pub async fn notify(&self, message: &str, is_error: bool) -> Result<(), String>`, `pub async fn emit(&self, event: &str, params: Value) -> Result<(), String>`.
  - `#[async_trait] pub trait PluginHandler: Send + Sync + 'static { async fn handle(&self, method: &str, params: Value, host: &HostClient) -> Result<Value, PluginError>; }` and `pub enum PluginError { InvalidParams(String), Application(String), MethodNotFound }` mapping to `-32602`, `-32000`, `-32601`.
  - `pub async fn serve(args: Args, info: PluginInfo, accepts_multiple_clients: bool, handler: Arc<dyn PluginHandler>) -> Result<(), String>` — creates the named pipe server (`tokio::net::windows::named_pipe::ServerOptions`, `max_instances` 1 or `PIPE_UNLIMITED_INSTANCES`), answers `plugin.handshake` itself with `{ pluginId, pluginVersion, sdkVersion }`, rejects any other method before handshake with `-32600`, answers `cancel { operationId }` by setting a per-connection cancel flag readable via `HostClient::is_cancelled(&self, operation_id)`, and returns when the last client disconnects. Any method whose client is the service (handshake `client == "simplefile-service"`) is still routed to the same handler.
  - Frames use `simplefile_ipc::frame`. The crate depends on `simplefile-ipc`, `tokio`, `serde`, `serde_json`, `async-trait`.
  - Behind a `test-support` cargo feature: `impl HostClient { pub fn fake(settings: HashMap<String, String>) -> HostClient; pub fn fake_log(&self) -> Vec<(String, String)>; }` — an in-memory host that answers `get_settings` from the map, records `set_setting` into it, and records `log` calls. Task 15 depends on it.

- [ ] **Step 1: Write the failing round-trip test**

`tests/roundtrip.rs`: start `serve` on a unique pipe with an `EchoHandler` (method `echo` returns params; method `ask_host` calls `host.log("info","x")` and returns `{"logged":true}`); connect a tokio client that does the handshake, calls `echo`, then calls `ask_host` while answering the inbound `host.log` request; assert the results; drop the client and assert `serve` returns within 2 s. A second test: calling `echo` before handshake yields `-32600`. A third: `Args::parse` round-trip and missing `--pipe-name` error.

- [ ] **Step 2: Run to verify it fails**

Run: `cargo test -p sumafile-plugin`
Expected: compile FAIL.

- [ ] **Step 3: Implement the crate**

- [ ] **Step 4: Run the tests and the workspace check**

Run: `cargo test -p sumafile-plugin` then `cargo clippy --locked --all-targets --all-features -- -D warnings`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Cargo.toml Cargo.lock crates/sumafile-plugin
git commit -m "Add the sumafile-plugin crate for backend pipe servers"
```

---

### Task 15: Fixture backend and the fixture plugin folder

**Files:**
- Create: `crates/hello-plugin-backend/Cargo.toml`, `src/main.rs`, `scripts/build-fixture-plugin.ps1`
- Modify: `Cargo.toml` (workspace member), `src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj` (nothing new; the script stages the exe)

**Interfaces:**
- Consumes: `sumafile-plugin` (Task 14).
- Produces: `hello-plugin-backend.exe` with `PluginInfo { id: "com.sumafile.hello", version: "0.1.0" }`, `accepts_multiple_clients: true`, handler methods:
  - `ping {}` → `{ "pong": true, "greeting": <value of setting "greeting" via host.get_settings, default "Hello"> }`.
  - `archive.list { archivePath }` → a `.hello` file is UTF-8 text with one entry per line `"<path>\t<size>"`; returns entries with `isDir` false and `compressedSize` equal to `size`.
  - `archive.extract { archivePath, destination, innerPath? }` → writes each listed entry as a file of `size` spaces under `destination`, filtered by `innerPath` prefix when given.
  - `archive.create { archivePath, paths }` → writes the listing from the given files' names and sizes.
  - Anything else → `PluginError::MethodNotFound`.
- `scripts/build-fixture-plugin.ps1 [-Configuration Debug]`: `cargo build -p hello-plugin-backend`, `dotnet build` the fixture C# project, and copies `plugin.json`, the fixture DLL and the exe into `target\fixture-plugins\com.sumafile.hello\`; prints the folder. The test csproj post-build copy from Task 8 is extended to also copy `target\debug\hello-plugin-backend.exe` when it exists.

- [ ] **Step 1: Write the failing tests**

In `crates/hello-plugin-backend/src/main.rs` `#[cfg(test)]`: `list_parses_hello_file`, `extract_writes_sized_files`, `create_writes_listing`, `ping_uses_greeting_setting` (handler tested through a `HostClient` test double provided by `sumafile-plugin` under a `test-support` feature: `HostClient::fake(settings: HashMap<String,String>)`).

- [ ] **Step 2: Run to verify they fail**

Run: `cargo test -p hello-plugin-backend`
Expected: compile FAIL.

- [ ] **Step 3: Implement the backend and the staging script**

- [ ] **Step 4: Run tests, then the end-to-end check**

Run: `cargo test -p hello-plugin-backend`, then `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-fixture-plugin.ps1`, copy the printed folder to `%LOCALAPPDATA%\SumaFile\plugins\com.sumafile.hello`, run `npm run dev:winui`. Expected: after enabling, "Hello: ping backend" logs `pong` in `%LOCALAPPDATA%\SumaFile\startup.log` (host log sink) and a `.hello` file created in a folder shows in Create archive's format list and extracts.

- [ ] **Step 5: Commit**

```bash
git add Cargo.toml Cargo.lock crates/hello-plugin-backend scripts/build-fixture-plugin.ps1 src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj
git commit -m "Add the Hello fixture backend and a script that stages the fixture plugin"
```

---

### Task 16: UI smoke, packaging check and plugin protocol doc

**Files:**
- Modify: `scripts/smoke-winui-ui.ps1:3-11` (new `-PluginsDir` and `-NoPlugins` switches), `src-winui/SimpleFile.App/MainWindow.UiSmoke.cs` (new checks), `scripts/check-winui-packaging.mjs`, `.github/workflows/ci.yml:172-184`
- Create: `ipc/schema/plugin/v1/protocol.json`, `ipc/schema/plugin/README.md`
- Modify: `scripts/check-ipc-schema.mjs` (validate the plugin protocol file)

**Interfaces:**
- `protocol.json` lists `handshake.method = "plugin.handshake"`, `handshake.params = ["pluginId","hostVersion","sdkVersion","client?"]`, `handshake.result = ["pluginId","pluginVersion","sdkVersion"]`, `hostMethods` as the eight names from `PluginHostMethods`, `archiveEngineMethods` = `archive.list`, `archive.extract`, `archive.create`, `cancel.method = "cancel"`, `framing = "same as v1"`, `sdkVersion = "1.0.0"`.
- `check-ipc-schema.mjs` additionally asserts every `hostMethods` entry appears as a string literal in `src-winui/SimpleFile.PluginHost/Backend/PluginHostMethods.cs` and in `crates/sumafile-plugin/src/host.rs`, and that `sdkVersion` equals `PluginSdk.Version` in `src-winui/SimpleFile.PluginSdk/PluginSdk.cs`.
- UI smoke: when `-PluginsDir` is given, the smoke sets `SUMAFILE_PLUGIN_ROOTS` (a new env override read by `PluginDiscovery.DefaultRoots()` when present, semicolon separated) and pre-writes the approval store so no dialog appears; `MainWindow.UiSmoke.cs` asserts the palette contains `plugin:com.sumafile.hello:greet`, runs it, and asserts `HelloPlugin.ExecutedCommands` is non-empty via a reflection-free marker file the fixture writes to its data dir. With `-NoPlugins` it asserts no `plugin:` command exists and the Plugins settings category lists zero plugins.
- `check-winui-packaging.mjs` adds `src-winui/SimpleFile.PluginSdk/SimpleFile.PluginSdk.csproj`, `src-winui/SimpleFile.PluginHost/SimpleFile.PluginHost.csproj`, `crates/sumafile-plugin/Cargo.toml` to `requiredFiles`.
- CI: the WinUI job runs `scripts/build-fixture-plugin.ps1` before `npm run check:winui`, and `smoke:winui-ui` twice: once with `-PluginsDir target\fixture-plugins` and once with `-NoPlugins`.

- [ ] **Step 1: Add the schema file and the check**

Run: `npm run check:ipc-schema`. Expected: fails until the literals line up, then passes.

- [ ] **Step 2: Extend the smoke and run it locally**

Run: `npm run smoke:winui-ui -- -Counts 10000 -PluginsDir target\fixture-plugins` and `npm run smoke:winui-ui -- -Counts 10000 -NoPlugins`
Expected: both report `passed: true` in their `report.json`.

- [ ] **Step 3: Run the full check suite**

Run: `npm run check && npm run check:winui && npm run check:rust`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add scripts ipc .github src-winui/SimpleFile.App/MainWindow.UiSmoke.cs src-winui/SimpleFile.PluginHost
git commit -m "Cover plugins in the UI smoke, packaging check and schema check"
```

---

### Task 17: XAML spike

**Files:**
- Create (throwaway unless it passes): `src-winui/SimpleFile.Spike.XamlLib/SimpleFile.Spike.XamlLib.csproj` with a copy of `GitWorkbenchView.xaml(.cs)` renamed `SpikeWorkbenchView`
- Modify on pass only: `src-winui/SimpleFile.PluginHost/Loading/PluginActivator.cs`, `src-winui/SimpleFile.App/App.xaml.cs`
- Create: `docs/superpowers/specs/2026-11-07-plugin-system-design.md:§5.3` outcome paragraph

**Question:** can a WinUI class library's compiled XAML page be instantiated when its assembly is loaded through `PluginLoadContext` from a folder outside the app directory in this unpackaged, self-contained app?

**Probe, time-boxed to one day:**

- [ ] **Step 1: Build the spike library** as a WinUI class library (`UseWinUI`, `EnableMsixTooling` false) and copy its output, including `.xbf` and `.pri`, into `target\fixture-plugins\com.sumafile.spike\`.
- [ ] **Step 2: Load it** through `PluginActivator` from a temporary `IPlugin` whose `ActivateAsync` creates `new SpikeWorkbenchView()` and places it in a `ContentDialog`. Try the three known levers in order until one works or all fail: (a) nothing extra; (b) register the library's generated `XamlMetaDataProvider` by adding it to the `OtherProviders` list of the App's generated provider through a new `App.RegisterXamlMetadataProvider(IXamlMetadataProvider)` hook; (c) merge the library's `resources.pri` by copying it next to `SumaFile.exe` under a unique name and calling `ResourceManager`-based registration.
- [ ] **Step 3: Record the result** in spec §5.3: which lever worked, or that none did. On pass: keep lever (b) or (c) as production code in `PluginActivator` and `App.xaml.cs`, add a loader test that instantiates the spike view's type, and delete the spike project once the Git plugin reuses the path in phase 4. On fail: delete the spike project and the plan for phase 4 converts the Git views to code-built UI.
- [ ] **Step 4: Commit** the spec note and any kept code.

```bash
git add docs/superpowers/specs/2026-11-07-plugin-system-design.md src-winui
git commit -m "Record the compiled-XAML plugin spike outcome"
```

---

## Self-review notes

- Spec coverage for phase 1: §4 manifest and locations (Tasks 2, 3, 12), §5.1 SDK (Task 1), §5.2 loading (Task 8), §5.3 (Task 17), §6.1 to §6.3 (Tasks 5, 6, 7), §6.4 (Task 13), §6.5 (Task 14), §7 command, context-menu and toolbar slots and status center (Tasks 10, 11), §8 (Task 12), §9 (Tasks 4, 11), §10 (Tasks 6, 9, 11), §13 fixture, contract tests, schema, packaging, smoke, startup stage (Tasks 1, 8, 13, 15, 16, 11). Sidebar, panel, row-decoration and custom-settings-page slots are phases 3 and 4 by spec §14; the `when` evaluator already accepts `hasDecoration` and `folderFlag` so phase 4 only has to populate the request sets.
- Deviation recorded: Ed25519 in the host via BouncyCastle (Task 1, Step 5).
- Scope note: in-pane browsing and mutation of plugin archive formats is deferred to phase 2, where `ArchiveFormat` gains a plugin variant alongside the 7-Zip move.
