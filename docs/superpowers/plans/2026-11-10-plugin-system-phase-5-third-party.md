# Plugin System Phase 5: Third-Party Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the plugin system usable by an author outside this repository, with nothing published. That means:
- a plugin template folder that builds and loads in SumaFile;
- an author guide, `docs/PLUGINS.md`;
- dry-run packaging of the SDK (`dotnet pack`) and the Rust helper crate (`cargo package`);
- a signing tool for `signature.json`;
- the phase 1 deferred docs items;
- the review items parked "for phase 5";
- a UI smoke that cleans up its own run folder after a pass.

**Architecture:** No new host slot and no SDK contract change: `PluginSdk.Version` stays `"1.0.0"` and `Fixtures/PluginSdkSurface.v1.txt` does not change. Phase 5 tightens the host where third-party input reaches it:
- window options, manifest string lengths, the `plugin.json` size and the shared-assembly prefixes;
- the SDK gets XML documentation, which ships in its package.

The template is a third, minimal first-party-style plugin (`com.example.template`), built by CI but never bundled or staged into the payload.

**Tech Stack:** C# / .NET 10, xUnit, Rust 2021 (`sumafile-plugin`), Node 22 (`node:crypto` Ed25519 for the signing tool, `node --test`), PowerShell 5.1 scripts.

**Spec:** `docs/superpowers/specs/2026-11-07-plugin-system-design.md` §9, §12, §13 and §14 phase 5. The scope is in "Phase 5: Third-party readiness" of the handoff scopes. Review notes: `docs/superpowers/notes/2026-11-07-plugin-system-phase-1-review-notes.md` (item 5 "The protocol docs are too thin", the Task 1 "SDK has no XML docs" minor, and the README section on Settings > Plugins).

## Global Constraints

- Everything from the phase 4 plan's Global Constraints still holds: offline NuGet, never a package that is not in a lock file, lock files consistent, `TreatWarningsAsErrors`, no compiled XAML in plugin folders, the 50 ms `plugins-discovered` budget never raised, `npm run check` and `npm run test:maintenance` green.
- **Nothing is published.** `dotnet pack` writes to `target\packages`, and `cargo package` only runs as a dry run (`cargo package --no-verify` is not used: the dry run must also build the packaged sources). Never `cargo publish` or `dotnet nuget push`.
- **No SDK surface change.** Docs, package metadata and host-side validation only. `PluginSdkSurface.v1.txt` stays byte-identical.
- **Verification bench.** The cloud session cannot run WinUI, so:
  - `SimpleFile.Tests` is built and run on Linux with `-p:EnableWindowsTargeting=true -p:AppxGeneratePriEnabled=false`, with the Linux-only baseline failures known and unchanged;
  - Rust and Node checks run locally;
  - the windows-2025 CI jobs are the bench for everything else.
- Product name in user-facing text is "SumaFile".

## Parked items this phase closes

| Item | Where | Decision |
| --- | --- | --- |
| #17/#22 | `PluginLoadContext.cs` shared prefixes | `Microsoft.Graphics` matches Win2D (`Microsoft.Graphics.Canvas*`), which the host does not ship. Replace it with the two families the host ships, `Microsoft.Graphics.Display` and `Microsoft.Graphics.Imaging`, so a plugin can bundle Win2D. |
| #72/#92 | `PluginWindowOptions`, `PluginWindowHost.Attach` | The host checks the options before tracking. Width and height are clamped to 320-3840 and 240-2160 DIPs. A null, empty or whitespace title falls back to the plugin's manifest name, and titles are cut to 120 characters. `Attach` off the UI thread throws `InvalidOperationException("Attach must be called on the UI thread.")` before anything is tracked. The clamped arithmetic cannot overflow the DPI scale. |
| #73 | `PluginManifestReader` | Bounded strings. The id is unchanged (grammar). Limits: `name` ≤ 100, `publisher.name` ≤ 100, `description` ≤ 2000, each contribution `label`/`title`/`group` ≤ 200, `when` ≤ 512 (the existing, documented limit; kept as built), setting `label`/`group` ≤ 200, setting `description` ≤ 1000, `glyph` ≤ 8. A longer value makes the manifest invalid with `<field> is longer than <n> characters.` |
| #28 | `PluginDiscovery` manifest read | `plugin.json` larger than 1 MiB (`PluginManifestReader.MaxManifestBytes`, shared with the installer) is invalid with `plugin.json is larger than 1 MiB.`, read at most cap + 1 bytes, never allocated whole. |
| #70/#75 | SDK doc wording | The SDK gets `GenerateDocumentationFile`, so every public type and member is documented (CS1591 is an error under `TreatWarningsAsErrors`). The docs state the threading rules, the `FolderFlags` grammar `^[A-Za-z][A-Za-z0-9_-]{0,63}$` (anything else is dropped), the window-option ranges above, the `object` hand-off rule (§5.1) and the deadlines. |
| Mvvm prefix | `PluginLoadContext`, spec §12 | Keep `CommunityToolkit.Mvvm` host-shared, as ruled. PLUGINS.md documents that a plugin is bound to the host's Mvvm, WinAppSDK and BuildTools versions. A test pins the shared prefix list against the table PLUGINS.md prints. |
| UI smoke disk use | `scripts/smoke-winui-ui.ps1` | After a passing run the script deletes its `test-results\ui-smoke\<run>` folder. It keeps the folder on failure, and when `-KeepResults` is passed. The report path printed on success points at a copy of `report.json` written beside the folder (`test-results\ui-smoke\last-passed-report.json`), so CI still uploads a report. |

Phase 1 deferred docs items closed here: review notes item 5 (protocol docs), the SDK XML docs minor, and a README section on Settings > Plugins.

---

### Task 1: Host hardening for third-party input (#17/#22, #72/#92, #73, #28)

**Files:** `src-winui/SimpleFile.PluginHost/Loading/PluginLoadContext.cs`, `Manifest/PluginManifestReader.cs`, `Discovery/PluginDiscovery.cs`, `Install/PluginPackageInstaller.cs` (shared cap), `IPluginWindowHost.cs` (a public static `PluginWindowOptionsPolicy.Normalize(PluginWindowOptions, string fallbackTitle)`), `src-winui/SimpleFile.App/PluginWindowHost.cs` (calls it and checks the thread). Tests in `PluginLoadingTests`, `PluginManifestReaderTests` (or the existing manifest tests), `PluginDiscoveryTests`/`PluginEnvironmentTests`, a new `PluginWindowOptionsPolicyTests`, and `WinUiSourceShapeTests` for the App call.

- [ ] Step 1: failing tests:
  - `SharedPrefixes_DoNotCaptureWin2D`: `Microsoft.Graphics.Canvas` and `Microsoft.Graphics.Canvas.Interop` are not host-shared; `Microsoft.Graphics.Display` and `Microsoft.Graphics.Imaging` are.
  - `Read_StringLongerThanItsLimit_IsInvalid`, a theory over each bounded field, with exactly-at-limit valid.
  - `Discover_ManifestLargerThanTheCap_IsInvalidWithoutReadingItWhole`.
  - `Normalize_ClampsSizesAndFallsBackToTheManifestName`, including `int.MaxValue`, `0` and negatives.
  - `PluginWindowHost_NormalizesOptionsAndChecksTheThreadBeforeTracking` (source shape).
- [ ] Step 2: run them; they fail.
- [ ] Step 3: implement.
- [ ] Step 4: the filters pass; the full Linux run shows only the baseline failures.
- [ ] Step 5: spec §4.3 (limits), §5.2 (prefixes), §7 (window options); the review notes record the closures.

### Task 2: SDK documentation and `dotnet pack` as `SumaFile.PluginSdk` (#70/#75)

**Files:** `src-winui/SimpleFile.PluginSdk/SimpleFile.PluginSdk.csproj` (`GenerateDocumentationFile`, `PackageId=SumaFile.PluginSdk`, `PackageLicenseExpression=MIT`, `RepositoryUrl`, `PackageReadmeFile=README.md`, `Description`, `Authors`, `IsPackable=true`, version `1.0.0` from `PluginSdk.Version`), a new `src-winui/SimpleFile.PluginSdk/README.md`, every public SDK file (XML docs), `package.json` (`pack:sdk`: `dotnet pack src-winui/SimpleFile.PluginSdk -c Release -o target/packages`), `scripts/check-winui-packaging.mjs` (the metadata), `.github/workflows/ci.yml` (a "Pack the plugin SDK (dry run)" step in the WinUI host job, after the tests), `scripts/check-github-workflows.mjs` if it pins the step list. Test: `PluginSdkPackageTests.SdkProject_DeclaresThePackageMetadata`, and `SdkDocs_EveryPublicMemberIsDocumented` (the XML file next to the built SDK lists every public member of the surface snapshot).

- [ ] Step 1: failing tests, then implement docs until the build has 0 warnings.
- [ ] Step 2: `dotnet pack` locally (Linux, with `EnableWindowsTargeting`) produces `target/packages/SumaFile.PluginSdk.1.0.0.nupkg` holding `lib/net10.0-windows7.0/SimpleFile.PluginSdk.dll`, its `.xml` and `README.md`; the surface snapshot is unchanged.
- [ ] Step 3: spec §12 (the package id and what it contains).

### Task 3: `cargo package` dry run for `sumafile-plugin`

`sumafile-plugin` depends on `simplefile-ipc` by path, so a package needs both. The dry run packages the two together with `cargo package -p simplefile-ipc -p sumafile-plugin`, which builds each package from its packaged sources against a temporary local registry.

**Files:** `crates/sumafile-plugin/Cargo.toml` (`version = "1.0.4"` on the path dependency, `readme`, `keywords`, `categories`, `rust-version`), `crates/sumafile-plugin/README.md`, `crates/simplefile-ipc/Cargo.toml` (`readme`), `crates/simplefile-ipc/README.md`, `package.json` (`pack:crates`), `scripts/check-winui-packaging.mjs` (metadata), and the CI Rust quality gate (a "Package the plugin crates (dry run)" step).

- [ ] Step 1: a maintenance test in `scripts/tests/` that reads both manifests and requires the metadata fields (fails first).
- [ ] Step 2: implement; `npm run pack:crates` passes locally.
- [ ] Step 3: spec §6.5.

### Task 4: The signing tool

**Files:** `scripts/sign-plugin.mjs`, `scripts/tests/sign-plugin.test.mjs`, a signed fixture folder `src-winui/SimpleFile.Tests/Fixtures/signed-plugin/{plugin.json, payload.txt, signature.json}` and its test key (public only), `PluginSignatureTests.SignedFixture_FromTheSigningTool_Verifies`, and `package.json` (`sign:plugin`).

The tool:
- `node scripts/sign-plugin.mjs keygen --out <dir>` writes `signing-key.pem` (PKCS#8, mode 0600 where the OS supports it) and prints the raw 32-byte base64 public key for `publisher.publicKey`.
- `node scripts/sign-plugin.mjs sign <plugin folder> --key <pem>` checks that `publisher.publicKey` matches the key. It hashes every file in the folder except `plugin.json` and `signature.json` (forward-slash relative paths, files only, no symlinks, refusing `..` or absolute paths), builds the host's canonical payload byte for byte (`PluginSignatureVerifier.CanonicalPayload`), signs it with Ed25519 and writes `signature.json` (`algorithm`, `files`, `signature`).
- `node scripts/sign-plugin.mjs verify <plugin folder>` re-checks it the way the host does.

The private key never enters the repository. The fixture's key is generated by the test at run time; only the fixture's public key and signature are committed, produced once by the tool.

- [ ] Step 1: failing node tests: keygen, sign and verify round trip; a changed file fails verify; a mismatched public key is refused; the declared UI assembly and backend are covered (the host's rule).
- [ ] Step 2: the C# test verifies the committed fixture with the real verifier: Verified, with the expected fingerprint.
- [ ] Step 3: spec §9 (the tool), PLUGINS.md (Task 6) documents it.

### Task 5: The plugin template `templates/sumafile-plugin`

A copyable starter that builds in this repository and loads in SumaFile.

**Layout:**
- `templates/sumafile-plugin/plugin.json`: `com.example.template`, one command, one declarative setting, a backend with `activateOn: first-call`.
- `ui/Example.Plugin/Example.Plugin.csproj` (`net10.0-windows`, WinUI-free, SDK compile-only), `ExamplePlugin.cs` (`IPlugin`, `ICommandHandler`): the command reads the setting, calls the backend's `example.echo` and shows the result with `IPluginDialogs.Notify`.
- `backend/` (`example-plugin-backend`, `publish = false`) on `sumafile-plugin`: `example.echo { text } -> { text, length }`, with handler tests on `HostClient::fake`.
- `stage.ps1`: builds both and stages a plugin folder under `target\template-plugins\com.example.template`, like `build-fixture-plugin.ps1`.
- `README.md`: build, stage, point `SUMAFILE_PLUGIN_ROOTS` at it, consent, sign with `scripts/sign-plugin.mjs`, package as `.sfplugin` (a zip of the folder), and how to switch the project and crate references to `SumaFile.PluginSdk` and `sumafile-plugin` once published.

**Repository wiring:** the backend joins the Cargo workspace (CI builds and tests it); the C# project joins `SimpleFile.sln`, and `SimpleFile.Tests` references it with `ReferenceOutputAssembly="false"`, as the Hello fixture does. `check-winui-packaging.mjs` requires the template's files and that the template crate depends on neither `simplefile-core` nor `simplefile-service` (`pluginCrateManifests`). The payload never contains it.

Tests:
- `TemplatePlugin_ManifestIsValidAndNeedsConsent` (not in `bundled.json`).
- `TemplatePlugin_LoadsThroughTheRealHostAndRunsItsCommand`: the real `PluginManager` on the template's build output, approved, command run against a fake backend launcher. Skipped with a clear message when not built.
- The Rust handler tests.
- `TemplatePlugin_ProjectsReferenceOnlyTheSdk` (source shape).

- [ ] Steps: failing tests, implement, Linux run, CI.

### Task 6: `docs/PLUGINS.md`, the protocol docs and the README

`docs/PLUGINS.md`, the author guide:
- the folder layout and `plugin.json` reference (every field, limits from Task 1);
- each contribution kind with an example: commands, context menu with `when`, toolbar, sidebar section, panel, row decorations, settings page, archive engine;
- declarative settings and `migrateFrom` (bundled only);
- the SDK interfaces and the `object` hand-off rule;
- threading rules (`ActivateAsync` on the UI thread, `DeactivateAsync` on the thread pool, UI calls on the UI thread, `IPluginPanels` queues);
- the backend: command line (`--pipe-name`, `--data-dir`), `TMP`/`TEMP`, lifetime (exit on disconnect), the handshake, request routing, `cancel`, error codes (-32600 before the handshake, -32601, -32602, -32000), every `host.*` method with param and result shapes, archive engines;
- limits and deadlines (connect 5 s, call timeouts, frame caps, decoration budget 250 ms, startup budget);
- trust, consent and signing (Task 4); packaging a `.sfplugin` and installing it; the template (Task 5); the host-shared assemblies (the table Task 1's test pins).

`ipc/schema/plugin/README.md` gains the param and result shapes and the error codes, linking to PLUGINS.md. The README gains a "Plugins" section: Settings > Plugins, consent, install from file, remove, the plugins folder, and a link to PLUGINS.md.

- [ ] Steps: write; a maintenance test (`scripts/tests/plugins-doc.test.mjs`) requires that PLUGINS.md names every `host.*` method in `ipc/schema/plugin/v1` and every contribution kind the manifest reader accepts, so the guide cannot fall behind.

### Task 7: The UI smoke deletes its run folder after a pass

**Files:** `scripts/smoke-winui-ui.ps1`, `.github/workflows/ci.yml` (the upload path), `scripts/check-github-workflows.mjs` if it pins it, `scripts/tests/` (a static test of the script: the delete runs only on the success path, after the app and every helper process are gone, and only under `test-results\ui-smoke\`, guarded by the `.sumafile-ui-smoke` marker file the script writes).

- [ ] Steps: failing static test, implement, pwsh parse check, CI shows the uploaded `last-passed-report.json` and no leftover folder.

### Task 8: Phase verification and closures

- [ ] The full Linux bench (Rust fmt, clippy and tests, `npm run check`, `npm run test:maintenance`, the C# run against the baseline, `npm run pack:crates`, `dotnet pack`).
- [ ] CI green on windows-2025, the startup budget included.
- [ ] Review notes: the phase 5 closures, each naming its task and tests.
- [ ] A whole-phase review (spec fidelity, robustness, docs accuracy) and one fix wave.

## Verification that only the user's machine can run

`npm run release:build`, the MSI and NSIS smokes and the `-Counts 10000` UI smoke variants are not run by this phase's bench. The final report lists them.
