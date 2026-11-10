## Phase 3: FTP/SFTP plugin (`com.sumafile.remote`)

Spec §11.2 and §14 phase 3. Required outcomes:
- The whole remote stack moves into one plugin: profiles, the session registry, the Credential Manager secret store, the FTP/FTPS and SFTP providers and the remote transfer engine move from `crates/simplefile-core/src/remote` and `crates/simplefile-service/src/remote` into a Rust plugin backend; transfer progress goes through `host.progress_*` and cancellation through `cancel`, so remote transfers still show in the Transfers window with Cancel.
- The UI moves into the plugin's C# assembly, built in code (compiled XAML cannot load from plugin folders, spec §5.3): the Remote Manager window and view model, the sidebar profiles section as an `ISidebarSectionProvider`, the remote parts of the transfer options dialog, the `ftp-sftp-manager` command, and a custom settings page via `ISettingsPageProvider`.
- The host gains the sidebar-section slot, plugin-owned secondary windows and the custom settings page slot (spec §4.4, §7, §8.2), as SDK contracts with the pinned surface snapshot updated deliberately.
- Core loses the 13 remote IPC methods, the `RemoteProviderFactory` trait, the session registry and the secret store; schema counts, generated bindings, the parity gate, docs and CI follow. Without the plugin there is no remote UI and no Remote profiles sidebar section.
- Existing saved profiles and credentials keep working after the move (migrate or read them in place).
- Packaging stages `payload\plugins\com.sumafile.remote`, pre-approved like the 7-Zip plugin. Tests and UI smoke assertions cover the plugin path end to end (a local SFTP/FTP test server or the existing provider fakes), plus a no-plugin smoke.

## Phase 4: Git plugin (`com.sumafile.git`)

Spec §11.3 and §14 phase 4. Required outcomes:
- `crates/simplefile-core/src/git.rs` (and its status cache) becomes the plugin's Rust backend.
- The Git workbench view, view model, detachable window and the logic in `MainWindow.Git.cs` move into the plugin's C# assembly, rebuilt as code-built UI (or `XamlReader.Load` strings), because compiled XAML from plugin folders fails (spec §5.3).
- The host gains the side-panel slot (`IPanelProvider`, one visible panel, host-or-window toggle, resize grip, replacing the inline `GitPanel` in `MainWindow.xaml` and `GitWorkbenchWindow` with a generic `PluginPanelHost`/`PluginPanelWindow`) and the row-decoration slot (`IRowDecorationProvider`, `Row.DecorationText`/`DecorationBrushKey` replacing `Row.GitText`, batched per folder, 250 ms budget, populating `ContextMenuRequest.FolderFlags` and `SelectionDecorationPlugins` so `when` expressions like `folderFlag(inRepository)` and `hasDecoration` work).
- Contributions: ten commands, a context-menu group with `when` expressions, a toolbar toggle, the panel, and a startup-activated row decoration provider. Setting `enabled` replaces `EnableGitIntegration`, migrated on first run.
- Core loses the 11 Git IPC methods, `GitText`, the Git menu, commands, inline panel and toolbar button; schema counts, bindings, parity gate, docs and CI follow.
- Packaging stages and pre-approves `payload\plugins\com.sumafile.git`; tests and UI smoke assertions cover the plugin path end to end, plus a no-plugin smoke.

## Phase 5: Third-party readiness

Spec §12 and §14 phase 5. Required outcomes, all local (nothing published):
- A plugin template repository as a folder in this repo (for example `templates/sumafile-plugin`): a C# UI project on the SDK, a Rust backend on `sumafile-plugin`, a manifest, a staging script and a README, that builds and loads in SumaFile.
- `docs/PLUGINS.md`: the author guide (manifest reference, every contribution kind, settings, the pipe protocol with param and result shapes and error codes, host methods, archive engines, trust and signing, packaging `.sfplugin`, threading rules such as `DeactivateAsync` on the thread pool, limits and deadlines).
- Packaging for publication, dry runs only: `dotnet pack` for `SimpleFile.PluginSdk` as `SumaFile.PluginSdk` (offline) and `cargo package --dry-run` (or `cargo publish --dry-run`) for `sumafile-plugin`, with metadata (license, repository, description, readme).
- A signing tool or script that produces `signature.json` for a plugin folder, documented.
- The phase 1 deferred docs items (protocol docs too thin for third-party authors; README section on Settings > Plugins).
