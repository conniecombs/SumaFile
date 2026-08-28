# Navigation parity checklist (WinUI slices 1–2)

**Status:** Historical parity notes. Svelte/Tauri is no longer the shipping UI.

WinUI 3 feature slices so far:

1. Drives, sidebar root, directory listing, breadcrumbs / path entry, primary-pane navigation, open folder in-app.
2. Dual-pane navigation and **pane-local tabs** (this update).

This document originally compared the Svelte paths (`frontend/src/lib/app/core.ts`, `fileNavigationPrimary.ts`, `ContentShell.svelte`, `SidebarShell.svelte`, `coreFileManager.ts`) to `src-winui`.

Sources of truth for this slice:

- `loadDirectory` / `openEntryPath` / `navigateHistory` / `navigateSpecial` / `refreshDrives` in `frontend/src/lib/app/core.ts`
- `pathSegments()` in `frontend/src/lib/components/layout-shell/ContentShell.svelte`
- Quick Access + My PC in `frontend/src/lib/components/layout-shell/SidebarShell.svelte`
- Path helpers + `visibleEntries` in `frontend/src/lib/coreFileManager.ts`
- Startup default `startLocation: 'home'` in `frontend/src/lib/appState.ts`
- Dual pane + tabs: `toggleDualPane`, `loadDirectoryForPane`, `loadSecondaryDirectory`, `openNewTab` / `switchToTab` / `closeTab`, `activatePane` in `frontend/src/lib/app/core.ts`
- Sidebar Left/Right target in `frontend/src/lib/components/layout-shell/SidebarShell.svelte`
- Per-pane chrome in `frontend/src/lib/components/layout-shell/ContentShell.svelte` and `frontend/src/lib/components/tabs/TabsBar.svelte`

## In scope

| Behavior | Svelte | WinUI | Status |
| --- | --- | --- | --- |
| Start IPC service, handshake, `get_home_dir`, `list_drives` | Tauri invoke | `BackendSession` + `ExplorerWorkspace.InitializeAsync` | Done |
| Start at home | `resolveStartupLocation` mode `home` | Navigate to `get_home_dir` | Done |
| Start at last/custom location | `resolveStartupLocation` modes `last` / `custom` | Settings IPC `startLocation` / `customPath`; `last` restores `workspace-layout` | Done |
| Sidebar Quick Access: Home / Desktop / Downloads / Documents / Pictures | `navigateSpecial` + `joinPath(home, name)` | Same commands and join rule | Done |
| Sidebar My PC drive roots | `list_drives`, fallback drive, status badge/description | `DrivePresentation` + drive list | Done |
| Refresh drives | `simplefile:refresh-drives` | ↻ button | Done |
| Collapse Quick Access / My PC | `localStorage` `simplefile-sidebar-collapse-state` | Settings IPC `sidebar.*Collapsed` | Done |
| Expandable folder tree | `listSubdirectories` / `loadTreeChildren` | Sidebar Folders list | Done |
| Bookmarks, recents, smart folders | Sidebar below Quick Access | Sidebar sections + settings-backed places / smart folder IPC | Done |
| Directory listing via `list_directory` | Channel chunks then full listing | `list_directory.chunk` then result | Done |
| First chunk paints before enumeration finishes | `primaryListingInProgress` + progressive concat | Same token + progressive list | Done |
| `RESULT_TOO_LARGE` keeps streamed chunks | Architecture / Gate 5 | Workspace treats as success + status | Done |
| `watch_directory` live refresh | Watcher after `loadDirectory` | MainWindow watch subscription + in-place refresh | Done |
| Hide `.` files by default | `showHiddenFiles: false` | Same | Done |
| Sort dirs-first then name (default) | `visibleEntries` / `sortEntries` | `EntryPresentation` | Done |
| Quick filter bar | `filterQuery` | Pane-aware filter box + `SetFilterQuery` | Done |
| Header sort name / size / date / type | Click header toggles direction | Same | Done |
| Columns Name, Size, Modified, Type | Default visible columns | Same four, resizable | Done |
| Git status column | `getGitFileStatuses` after listing | Optional enrichment when Git integration is enabled | Done |
| Breadcrumb segments | `path.split(/[/\\]/)` + `C:\` for drive | `BreadcrumbBuilder` | Done |
| Click breadcrumb navigates | `loadDirectoryForPane` | `NavigateToAsync` | Done |
| Path edit (✎), Enter navigates, Escape cancels | `ContentShell` path bar | Same | Done |
| Back / Forward history | `recordHistory` + `navigateHistory` with mode `none` | Same | Done |
| Up uses `getParentPath`; no-op on drive root | `if (parent) loadDirectoryForPane` | Same | Done |
| Open folder in-app (double-click / Enter) | `openEntryPath` → `loadDirectory` | Same | Done |
| Open file in default app | `open_file` / `openEntryPath` | `OpenEntryAsync` probes unknown paths, then calls `open_file` | Done |
| Click file selects (does not open) | `file-list-item-click` | List click selects | Done |
| Multi-select, range, type-ahead | `fileNavigationSelection.ts` | `ListView` Extended + `MatchTypeAhead` | Done |
| Network drive offline → retry dialog | `offerNetworkDriveReconnect` | `ContentDialog` Retry/Cancel | Done (simplified copy) |
| F5 refresh, Alt+Left/Right/Up | Keyboard map (subset) | Keyboard accelerators | Done (subset) |
| Status: path / item count / errors | Status bar | Bottom bar + InfoBar | Done |
| Dual pane toggle | F6 / toolbar; first enable copies primary path to secondary with `replace-current` and stays on primary | Dual button + F6; same first-load rule | Done |
| Disable dual pane | Hide secondary, activate primary, keep secondary path | Same | Done |
| Activate pane | Click pane, Alt+1 / Alt+2, Ctrl+Shift+Left/Right; Alt+2 enables dual | Same | Done |
| Sidebar Left/Right target | `sidebarTargetPane` = active pane when dual | Same; Quick Access / drives navigate that pane | Done |
| Pane-local history / breadcrumbs / path edit | Each pane has its own | Each pane has nav + breadcrumbs + ✎ | Done |
| Pane-local tabs | `tabs` / `secondaryTabs`, `activeTabId` / `secondaryActiveTabId` | `ExplorerPane.Tabs` + `ActiveTabId` | Done |
| First navigation creates a tab | `syncActiveTab` | Same | Done |
| New tab (Ctrl+T, +) | Fresh history `[path]` then `replace-current` load | Same | Done |
| Switch tab | Restore that tab’s history, load `none` | Same | Done |
| Close tab | Neighbor if active; last tab → new tab at home | Same | Done |
| Ctrl+Tab / Ctrl+Shift+Tab | Cycle tabs on the **active** pane | Same | Done |
| Ctrl+W | Close active pane’s active tab | Same | Done |
| Middle-click tab | Close | Same | Done |
| Pane resize 20–80% | ContentShell divider | Drag divider | Done |
| Persist tabs / `saveTabs` | `localStorage` | Settings IPC `workspace-layout` when Start Location is Last | Done |
| Tab keyboard focus wrap (ArrowLeft/Right on tab) | `moveTabFocus` | ArrowLeft/Right on the focused tab |
| Tab key switches panes | `pane.switch` when dual | Tab switches panes when focus is not in a text box |

## Gaps (remaining)

These are intentional backlog items for later WinUI polishing.

| Gap | Svelte today | Why WinUI does not match |
| --- | --- | --- |
| Thumbnails | Lazy thumbs | Out of slice |
| Grid / photo folder view | `isGridView`, `applyContextualFolderView` | List only |
| Rubber-band marquee | `fileNavigationSelection.ts` | Core helper only; no visible drag-selection surface |
| Theme toggle / light theme | `data-theme` | Settings theme + ThemeDictionaries |
| UNC breadcrumb first segment | Svelte accumulates `server` not `\\server` | **Matched on purpose** (same quirk) |
| Breadcrumb path after drive | `C:\` + `\Users` → `C:\\Users` | **Matched on purpose** (Win32 still opens the folder) |
| Modified-date exact `Intl` string | `DateTimeFormat` locale options | `DateTimeOffset.ToString("g")` — same instant, locale format may differ slightly |
| Column resize / presets / extra columns | `fileListColumns.ts` | Dynamic columns with persisted widths and Settings presets |
| Virtualized huge lists | `FileList.svelte` windowing | `ListView` default virtualization only |

## Blocked on later IPC methods

No navigation gaps are currently blocked on later IPC methods.

## How to verify

```powershell
cargo build -p simplefile-service
dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug
dotnet build src-winui/SimpleFile.sln -c Debug
npm run dev:winui
```

### Slice 1 (single pane)

Start at home, open a folder, breadcrumb back, path-bar Enter, drive click, Quick Access Desktop, Up at `C:\` does nothing, double-click file opens the default app.

### Slice 2 (dual pane + tabs)

1. Press **F6** or Dual. Right pane lists the same folder as the left; left stays active. Status shows `Left pane`.
2. Click the right pane (or **Alt+2**). Sidebar Left/Right highlight follows. Quick Access Desktop opens on the **right** only. Left path/history/tabs stay put.
3. On the right, go up, then Back. Left history is unchanged.
4. **Ctrl+T** on the left: new left tab at the current left path. Right tabs unchanged. Switch tabs; each tab restores its own history.
5. Close a non-last tab: neighbor becomes active. Close the last tab on a pane: a new home tab opens on that pane only.
6. **Ctrl+Tab** / **Ctrl+W** affect the active pane only.
7. Drag the divider; panes stay between 20% and 80%.
8. F6 again: right pane hides; left stays. F6 once more: right pane returns to the folder it had.
