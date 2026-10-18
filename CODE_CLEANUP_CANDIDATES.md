# Code Cleanup Candidates

Reviewed against the live WinUI 3 + Rust tree on 2026-10-18. The current
maintenance queue comes first; completed splits remain in the decision log.
Original options and obsolete line-count estimates have been retired.

## Current priorities

1. Keep repository, WinUI application build, Rust, and security gates green.
   PR CI compiles the application/XAML as well as running Core/IPC tests.
   Release hashing is checked against the production PowerShell function.
2. Protect large-folder rendering with `npm run smoke:winui-ui`. The real
   Debug host and Rust service record first-row latency, container count,
   memory and dispatcher gaps. Scenarios cover scrolling, selection, resizing,
   view changes, navigation replacement, and active-transfer cancellation.
   See [runtime verification](docs/WINUI_RUNTIME_VERIFICATION.md).
3. Reduce UI adapter duplication when changing the owning workflow.
   `MainWindow.xaml.cs` retains startup/session orchestration and workspace
   synchronization. Focused partials and UserControls own command, transfer,
   sidebar, pane, preview, and dialog surfaces. Extract behavior into Core
   where possible; verify XAML layout through runtime assertions.
4. Review the static `AppServices` lifetime when changing reconnect/session
   ownership. Preserve the shared transfer manager and event cleanup if
   simplifying ViewModel construction.
5. Consider centralizing `com.simplefile.desktop` across Rust settings,
   updater, and IPC boundaries only with migration coverage. Existing database
   and update compatibility identifiers remain intentional.

These are later maintenance candidates, not claims of current defects.
Runtime timings depend on hardware, build configuration and caches.

## Completed structural work

| Area | Current owning surface | Status |
| --- | --- | --- |
| Workspace | `ExplorerWorkspace*.cs`, `WorkspaceControllers/` | Complete; thin facade delegates to focused controllers. |
| Service dispatcher | `simplefile-service/src/dispatch/` | Complete; parameters, handlers, async operations and tests split; generated method constants used. |
| Session runtime | `simplefile-service/src/session/` | Complete; connection I/O and spawned jobs split. |
| Archives | `simplefile-core/src/archive/` | Complete; path/list/extract/mutate/create/tests split. |
| Transfer registry, metrics, metadata | `progress/registry.rs`, `file_ops/folder_metrics.rs`, metadata helpers | Complete; differing conflict semantics remain explicit. |
| Shared helpers | `path_conflict`, `utils` | Complete for matching path/hash helpers; divergent naming policies remain local. |
| Window markup | Pane, sidebar, preview, toolbar and other UserControls | Complete; control bridge preserves window event routing. |
| Preview and file dialogs | `PreviewPresenter.*.cs`, `FileOperationDialogService.*.cs` | Complete; shared scan orchestration extracted. |
| Settings window | `SettingsWindow.*.cs` | Complete; uses workspace settings snapshot. |
| IPC test doubles | `NullIpc`, `ConfigurableIpc`, `FakeExplorerBackend` | Complete; shared fakes replace interface copies. |
| ViewModels and aliases | Search, transfer manager, toolbar, `CommandAliasCatalog` | Complete; UI adaptation remains a maintenance surface. |
| Dead DTO/facade and retired-host cleanup | WinUI + Rust service | Complete; compatibility names and migration history retained deliberately. |

## Verification

```powershell
npm run check
npm run check:winui
npm run check:winui-release-hash
npm run build:winui
npm run smoke:winui-ui
npm run check:release
```

Run packaged startup/file-operation/installer smokes when changing those
contracts. Generated IPC files remain generator-owned: change the schema and
regenerate rather than editing `Protocol.Generated.cs`,
`NamedPipeJsonClient.Generated.cs`, or `protocol_generated.rs` manually.

## Decision log

Historical decisions, reconciled against the live tree on 2026-10-18. These entries describe completed work rather than a new implementation queue.

| Finding | Chosen option | Notes |
| --- | --- | --- |
| 1 MainWindow | C + A | Completed: extracted preview rendering/actions into split `PreviewPresenter.*.cs` partials, dialog-driven file operations into `FileOperationDialogService`, current search event/result glue into `MainWindow.Search.cs`, and app-command routing into `MainWindow.CommandRouting.cs`; `MainWindow` now delegates those workflows while preserving XAML event names. |
| 2 Unused ViewModels | B | Completed: finished the ViewModel cutover so `SearchViewModel` owns live search state/results/cancellation, `TransferViewModel` owns transfer operation identity/progress/cancellation, and `ToolbarViewModel` owns toolbar/status snapshots; removed the app-side `SearchHost` duplicate. |
| 3 ExplorerWorkspace | B + A | Completed: decomposed `ExplorerWorkspace` into a ~160-line core stub (`ExplorerWorkspace.cs`) plus 1-line delegation partials backed by 13 focused controllers in `src-winui/SimpleFile.Core/WorkspaceControllers/` (`PlacesController`, `WorkspaceTagController`, `FolderTreeController`, `PaneEnrichmentController`, `FolderViewController`, `WorkspaceLayoutController`, `DriveController`, `ClosedTabController`, `OperationLogController`, `DualPaneTabController`, `WorkspaceNavigationController`, `WorkspaceFileOpsController`, `WorkspaceSelectionController`). |
| 4 dispatch.rs | B | Completed: split the service dispatcher into `dispatch/{mod,params,handlers,async_ops,tests}.rs`, moved async arm construction behind `async_ops`, kept `dispatch()` re-exported from the module, replaced domain match arms with generated `METHOD_*` constants, and updated schema/parity checks for the split module. |
| 5 archive.rs | A | Completed: split archive handling into `archive/{mod,path,list,extract,mutate,create,tests}.rs` with public functions re-exported from `archive/mod.rs`; kept `resolve_rar_binary` on the archive API and did not move RAR installer behavior in this pass. |
| 6 Copy/conflict engines | B | Completed for matching helpers: added `simplefile-core::path_conflict` for no-follow existence, collision keys, same-entry checks, keep-both aliases, and exclusive directory creation; left divergent unique-name loops local. |
| 7 session.rs | B | Completed: split the session runtime into `session/{mod,jobs,io,tests}.rs`; `serve_connection` now stays in `mod.rs`, pipe framing/outbound batching lives in `io.rs`, spawned service jobs live in `jobs.rs`, session tests moved to `tests.rs`, and shared spawned-job response helpers reduce repeated scheduler/result handling. |
| 8 Dead AppState | A | Completed: removed dead core `state.rs`, unused helpers/deps, stale `src-tauri` service lookup, and live-code Tauri-era comments. |
| 9 ISimpleFileIpc / tests | A | Completed: added shared `NullIpc` / `ConfigurableIpc` test doubles, removed the per-file `WorkspaceSettingsIpc` and `StubIpc` full-interface stubs, and moved `FakeExplorerBackend` into its own helper file. |
| 10 Command routers | A | Completed: added `CommandAliasCatalog`, routed app/context/overflow aliases through `RunAppCommandAsync`, and added tests for shared alias normalization. |
| 11 Other splits | A | Completed for the transfer/file-op pair and preview presenter: split `progress::OperationRegistry` into `progress/registry.rs`, split folder metrics plus metadata preservation out of `file_ops.rs`, and split preview text/image/media/video/metadata/comparison rendering into focused `PreviewPresenter.*.cs` partials while preserving public re-exports and caller behavior. |
| 12 Tests / dialogs | A + D + B | Completed the test-file part: split `DesktopPolishTests.cs` and `ParityFeaturesTests.cs` into type-focused test classes, reused the shared IPC fake from Finding 9, replaced reachable job-object flag source checks with compiled assertions, and extracted the duplicate-checker/disk-cleanup scan skeleton into `FileOperationDialogService.Scans.cs`. |
| 13 Small copy-pastes | A, partial | Completed: readable-path and hex helpers live in `simplefile-core::utils`; create-item prompts use the template workflow. Stable application identifiers still occur in settings/updater compatibility boundaries; consolidate only with explicit migration coverage. |
| 14 IPC generated mix | A + B | Completed: generated IPC files remain generator-owned and dispatch uses generated method constants. Core retains the domain-sized `ISettingsBackend`; redundant tag/smart-folder/file-operation facade interfaces were removed. Error-code generation is an optional future change, not an unfinished migration. |
| 15 SettingsWindow | A + B | Completed: split settings state binding from RAR/update tools and load the dialog from the workspace `UiSettings` snapshot instead of re-reading settings through IPC. |
