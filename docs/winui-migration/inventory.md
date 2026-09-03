# SimpleFile WinUI 3 Migration Inventory

**Date:** 2026-08-14  
**Status:** Historical inventory. Svelte/Tauri UI and packaging were retired after the parity gate closed.  
**Source tree:** `R:\Repos\SimpleFile-Windows`  
**Goal:** replace the Svelte/Tauri renderer with a native C# WinUI 3 application while keeping the Rust backend as an IPC service.  
**Constraint at write time:** do not delete or retire Svelte/Tauri runtime code until an explicit later retirement step. That retirement step is complete.

Inspected sources:

- `src-tauri/src/lib.rs`
- `src-tauri/src/main.rs`
- `src-tauri/Cargo.toml`
- `src-tauri/tauri.conf.json`
- `src-tauri/tauri.local.conf.json`
- `src-tauri/capabilities/default.json`
- `frontend/src/lib/api.ts`
- `frontend/src/lib/types.ts`
- `frontend/src/lib/tauri.ts`
- `frontend/src/lib/app/core.ts`
- `frontend/src/lib/appState.ts`
- `frontend/src/lib/components/layout-shell/`
- `package.json`
- `frontend/package.json`

Supporting sources used to complete the lists: every `#[tauri::command]` module, emit sites, frontend workflow modules, layout/event bridges, persistence keys, GitHub workflows, and check/release scripts.

---

## 1. Current architecture

The shipping app is a Tauri 2 desktop shell:

| Layer | Location | Role |
| --- | --- | --- |
| WebView UI | `frontend/` (Svelte 5 + Vite) | Dual-pane explorer, tabs, sidebar, overlays, settings |
| IPC adapter | `frontend/src/lib/tauri.ts` + `api.ts` | Typed `invoke` / `listen` / `Channel` / `convertFileSrc` |
| Command contract | `frontend/src/lib/types.ts` `TauriCommandMap` / `TauriEventMap` | Shared argument and result types |
| Workflow/state | `frontend/src/lib/app/*.ts`, `appState.ts`, `vanilla-js/runtime/` | Navigation, transfers, search, settings, persistence |
| Rust backend | `src-tauri/src/` | File ops, search, archives, tags, updater, watchers |
| Host | Tauri 2 + WebView2 | Window, dialogs, opener, updater, drag-drop, custom protocol |

`src-tauri/src/lib.rs` registers 74 commands, three plugins (`dialog`, `opener`, `updater`), SQLite `DbState`, and shared `AppState`. The Svelte app never talks to the OS except through that IPC surface plus browser-only `localStorage`.

The WinUI replacement must preserve that split:

1. Keep Rust as the file-system / tools / updater / metadata service.
2. Replace the Tauri invoke/event/channel/window surface with a native IPC host.
3. Reimplement every frontend workflow and UI surface in WinUI 3.
4. Leave the existing Svelte/Tauri tree in place until retirement.

---

## 2. IPC commands

Tauri converts Rust snake_case parameters to camelCase on the JS side. The WinUI host must keep those names or provide an explicit mapping.

`list_directory` is the only command that also streams: the frontend creates a Tauri `Channel<DirectoryListingChunk>` and passes it as `onChunk`. Chunks arrive before the final `DirectoryListing` return.

### 2.1 Filesystem and listing

| Command | Rust module | JS args | Result | Used by |
| --- | --- | --- | --- | --- |
| `get_home_dir` | `fs_ops` | none | `string` | Startup location, Home navigation |
| `select_directory` | `fs_ops` (Tauri dialog plugin) | `{ defaultPath }` | `string \| null` | Settings start path, extract-to picker |
| `list_drives` | `drives` | none | `DriveInfo[]` | Sidebar “This PC”, drive status/retry |
| `list_directory` | `fs_ops` → `dir_list` | `{ path, onChunk }` | `DirectoryListing` | Primary/secondary listing, progressive chunks |
| `list_subdirectories` | `fs_ops` | `{ path }` | `TreeNode[]` | Sidebar tree expand |
| `create_directory` | `fs_ops` | `{ path, name }` | `string` | New folder, pack-into-folder |
| `create_file` | `fs_ops` | `{ path, name }` | `string` | New text/blank file |
| `create_shortcut` | `fs_ops` | `{ path, name, targetPath, arguments, workingDirectory, iconPath }` | `string` | New shortcut |
| `delete_entry` | `fs_ops` | `{ path }` | `void` | Permanent delete, undo of copy |
| `move_to_trash` | `fs_ops` | `{ paths }` | `void` | Trash delete |
| `rename_entry` | `fs_ops` | `{ path, newName }` | `string` | Rename |
| `batch_rename` | `fs_ops` | `{ entries: RenameRequest[] }` | `string[]` | Advanced rename apply |
| `copy_entry` | `fs_ops` | `{ source, destination }` | `string` | `compatOnly` legacy single copy; use `copy_with_progress` for live UI |
| `move_entry` | `fs_ops` | `{ source, destination }` | `string` | `compatOnly` legacy single move; use `move_with_progress` for live UI |
| `copy_entry_resolved` | `fs_ops` | `{ source, destination, conflictAction }` | `string` | Conflict-aware single copy / undo |
| `move_entry_resolved` | `fs_ops` | `{ source, destination, conflictAction }` | `string` | Conflict-aware single move / undo |
| `get_entry_info` | `fs_ops` | `{ path }` | `FileEntry` | Properties, open-with, preview fallback |
| `copy_with_progress` | `progress` | `{ sources, destination, operationId, conflictAction }` | `TransferResult[]` | Copy / paste / drop / pane copy |
| `move_with_progress` | `progress` | `{ sources, destination, operationId, conflictAction }` | `TransferResult[]` | Cut-paste / drop / pane move |
| `cancel_operation` | `progress` | `{ operationId }` | `void` | Progress cancel |
| `watch_directory` | `watcher` | `{ path }` | `void` | Watch active pane directory |
| `unwatch_directory` | `watcher` | none | `void` | Navigation / shutdown |
| `calculate_folder_size` | `fs_ops` | `{ path }` | `number` | Folder metrics + passive list sizes |
| `count_folder_items` | `fs_ops` | `{ path }` | `number` | Folder metrics (recursive) |
| `cancel_folder_size` | `fs_ops` | none | `void` | Cancel size work on navigation |
| `cancel_folder_item_count` | `fs_ops` | none | `void` | Cancel passive child counts |
| `cancel_count_items` | `fs_ops` | none | `void` | `compatOnly`; use `cancel_folder_item_count` |

### 2.2 Preview, open, and inspection

| Command | Rust module | JS args | Result | Used by |
| --- | --- | --- | --- | --- |
| `read_file_preview` | `preview` | `{ path, maxSize? }` | `FilePreview` | Preview pane, Quick Look, properties |
| `generate_thumbnail` | `preview` | `{ path, size }` | `string` (base64) | Grid / photo-folder thumbs |
| `generate_thumbnails` | `preview` | `{ paths, size }` | `ThumbnailResult[]` | Batched visible thumbs |
| `open_file` | `preview` (opener plugin) | `{ path }` | `void` | Open selected / double-click file |
| `reveal_in_folder` | `preview` (opener plugin) | `{ path }` | `void` | Duplicate checker reveal |
| `open_external_url` | `preview` (opener plugin) | `{ url }` | `void` | About → repository (`http`/`https` only) |
| `open_file_with` | `open_with` | `{ path, application }` | `void` | Open With dialog |
| `compare_files` | `compare` | `{ pathA, pathB }` | `FileComparison` | Compare two selected files |
| `compute_checksum` | `checksum` | `{ path }` | `Checksums` | Properties checksums |
| `get_image_metadata` | `metadata` | `{ path }` | `ImageMetadata` | Properties EXIF |
| `get_file_metadata` | `metadata` | `{ path }` | `FileMetadata` | Unified properties panel |

### 2.3 Search, smart folders, and organization

| Command | Rust module | JS args | Result | Used by |
| --- | --- | --- | --- | --- |
| `search_files` | `search` | `{ options: SearchOptions }` | `SearchResult[]` | Toolbar search, advanced search, smart folders |
| `cancel_search` | `search` | `{ searchId }` | `void` | New search / cancel / Escape |
| `load_smart_folders` | `smart_folders` | none | `SmartFolder[]` | Sidebar load |
| `save_smart_folder` | `smart_folders` | `{ folder }` | `SmartFolder[]` | Save current search |
| `delete_smart_folder` | `smart_folders` | `{ id }` | `SmartFolder[]` | Sidebar remove |
| `disk_cleanup` | `cleanup` | `{ directory, sizeThreshold? }` | `CleanupResult` | Analyze cleanup |
| `cancel_disk_cleanup` | `cleanup` | none | `void` | Wrapper exists; progress UI can cancel related work |
| `duplicate_check` | `cleanup` | `{ directory, minSize?, partialHashBytes? }` | `DuplicateCheckResult` | Duplicate checker |
| `cancel_duplicate_check` | `cleanup` | none | `void` | Duplicate checker cancel |

### 2.4 Archives and WinRAR

| Command | Rust module | JS args | Result | Used by |
| --- | --- | --- | --- | --- |
| `list_archive` | `archive` | `{ path }` | `ArchiveInfo` | Archive viewer |
| `extract_archive` | `archive` | `{ archivePath, destination }` | `void` | Extract here / folder / to… |
| `create_archive` | `archive` | `{ paths, archivePath, format }` | `void` | Compress… |
| `check_rar_installed` | `rar_installer` | none | `boolean` | Settings tools status |
| `prepare_rar_install` | `rar_installer` | none | `RarInstallPlan` | Confirm download/install |
| `discard_rar_install` | `rar_installer` | `{ confirmationToken }` | `void` | Cancel staged installer |
| `install_rar` | `rar_installer` | `{ confirmationToken }` | `string` | Settings “Install WinRAR” |

Formats that must remain: `zip`, `tar`, `tar.gz` / `tgz`, `rar`. Archive paths can also be navigated as virtual folders through `list_directory` / create helpers.

### 2.5 Git, terminals, tags, settings, updater

| Command | Rust module | JS args | Result | Used by |
| --- | --- | --- | --- | --- |
| `get_git_status` | `git` | `{ path }` | `GitStatus` | `compatOnly`; live UI uses `get_git_file_statuses` |
| `get_git_file_statuses` | `git` | `{ path }` | `Record<string, string>` | Optional git column when `enableGitIntegration` |
| `git_pull` | `git` | `{ path }` | `string \| void` | Command palette |
| `git_push` | `git` | `{ path }` | `string \| void` | Command palette |
| `open_terminal` | `terminal` | `{ path }` | `void` | F4, context menu, toolbar |
| `open_powershell_admin` | `terminal` | `{ path }` | `void` | Context menu / command workflow |
| `get_all_tags` | `tags` | none | `ColorLabelTag[]` | Color labels |
| `create_tag` | `tags` | `{ name, color }` | `ColorLabelTag` | Seed defaults if DB empty |
| `update_tag` | `tags` | `{ id, name, color }` | `void` | Tag editor |
| `delete_tag` | `tags` | `{ id }` | `void` | Tag editor |
| `set_tags_for_path` | `tags` | `{ path, tagIds }` | `void` | Set color label |
| `get_tags_for_path` | `tags` | `{ path }` | `ColorLabelTag[]` | Properties / label UI |
| `get_all_file_tags` | `tags` | none | `Record<string, ColorLabelTag>` | File list color dots |
| `get_files_with_tag` | `tags` | `{ tagId }` | `string[]` | Filter by label |
| `get_db_setting` | `db` | `{ key }` | `string \| null` | Wrapper only; no live UI caller |
| `set_db_setting` | `db` | `{ key, value }` | `void` | Wrapper only; no live UI caller |
| `get_app_version` | `updater` | none | `string` | Settings updates tab |
| `get_app_about_info` | `updater` | none | `AppAboutInfo` | About dialog |
| `check_for_update` | `updater` | none | `UpdateInfo \| null` | Settings check |
| `install_update` | `updater` | none | `void` | Settings install; then `app.restart()` |
| `show_main_window` | `lib.rs` | none | `void` | `hostOwned`/`compatOnly`; WinUI activates locally |

### 2.6 Conflict actions and transfer semantics

`ConflictAction` values that must survive: `error`, `skip`, `replace`, `rename`, `keep-both`, `keep_both`.

Progress `status` values: `running`, `completed`, `error`, `cancelled`.

Frontend transfer safety (`transferEntriesWithSafety` / `transferWorkflow.ts`) must keep:

- destination conflict probe
- user conflict prompt
- undo/redo of copy and move
- cancel via `cancel_operation`
- refresh of both panes after transfer
- operation history + retry payloads (`transfer`, `delete`, `create-archive`, `extract-archive`, `advanced-rename`)

---

## 3. IPC events

### 3.1 Backend-emitted events

| Event | Emitter | Payload | Frontend listener | Notes |
| --- | --- | --- | --- | --- |
| `file-change` | `watcher.rs` | `FileChangeEvent { path, kind }` | `onFileChange` → `handleFileChange` in `setup.ts` | Kinds: `create`, `modify`, `remove`, `rename`. Debounced; ignores `tmp`/`part`/`crdownload`, `desktop.ini`, `thumbs.db` |
| `operation-progress` | `progress.rs`, `cleanup.rs` | `ProgressUpdate` | `onOperationProgress` | Used for copy/move, cleanup (`operation_type: "cleanup"`), duplicate check (`"duplicate-check"`) |
| `search-results-batch` | `search.rs` | `SearchResult[]` | `onSearchResultsBatch` in `runSearch` | Incremental results; final set is the command return |
| `search-complete` | `search.rs` | `number` (result count) | `onSearchComplete` wrapper only | Emitted; current UI waits on `search_files` instead |
| `update-chunk` | `updater.rs` | `[bytesDownloaded, totalBytes \| null]` | `onUpdateChunk` wrapper only | Emitted during `install_update` |

### 3.2 Contract events that are not emitted

These exist on `TauriEventMap` / `api.ts` and must be decided during IPC design. They are **not** emitted by current Rust:

| Event | Status |
| --- | --- |
| `operation-complete` | Frontend wrapper only. Completion is a `ProgressUpdate.status` of `completed` plus the command promise. |
| `operation-error` | Frontend wrapper only. Errors use `ProgressUpdate.status === "error"` plus the command rejection. |

### 3.3 Tauri host events (not Rust)

| Event | Source | Payload | Used by |
| --- | --- | --- | --- |
| `tauri://drag-enter` | Tauri window (`dragDropEnabled: true`) | `string[]` or `{ paths?, files?, position? }` | External drop hover overlay |
| `tauri://drag-drop` | Tauri window | same | Copy dropped OS files into destination |
| `tauri://drag-leave` | Tauri window | same | Hide overlay |

WinUI must replace these with `DragEnter` / `Drop` / `DragLeave` on the window or panes and preserve destination resolution (`dropDestinationFromTarget`).

### 3.4 Streaming that is not an event

| Surface | Mechanism | Payload |
| --- | --- | --- |
| `list_directory` chunks | Tauri `Channel<DirectoryListingChunk>` | `{ path, parent, entries, chunk_index, done, is_network }` |

Huge-folder virtualization depends on first-chunk-first rendering. The replacement IPC must stream the same chunk shape.

---

## 4. Frontend workflows that must be reimplemented

Workflows live in `frontend/src/lib/app/` plus host-style modules under `frontend/src/lib/`. The live app is wired by `initApp()` in `setup.ts`; `App.svelte` mounts the layout shell and overlay shell, then calls `initApp()`.

### 4.1 Startup and shell

| Workflow | Source | Behavior to keep |
| --- | --- | --- |
| App bootstrap | `app/setup.ts` `initApp` | Load settings/bookmarks/recents/workspace/tabs; render layout + context menu; load smart folders + tags; register shortcuts, native drop, file-change, progress |
| Startup location | `vanilla-js/runtime/startup-location.ts` | `home` / `last` / `custom` from settings |
| Layout shell | `components/layout-shell/` | Sidebar + resizer + toolbar + dual-pane content + command palette + status bar |
| Overlay shell | `components/OverlayShell.svelte` | Context menu, column menu, all modals, external-drop overlay |
| Theme | `applyTheme` | `dark` / `light` on `data-theme` |
| Status bar | `updateStatusBar` | item count, selection size, active path |

### 4.2 Dual-pane navigation, tabs, sidebar

| Workflow | Source | Behavior to keep |
| --- | --- | --- |
| Primary listing | `loadDirectory` | Progressive `onChunk`, network-path flags, watch, recent locations, history, tab sync |
| Secondary listing | `loadSecondaryDirectory` | Independent path/history/selection/tabs |
| Pane activation | `activatePane` / `switchActivePane` | F6 dual-pane, Tab switch, Alt+1/2, Ctrl+Shift+Left/Right, sidebar Left/Right target |
| History | `navigateHistory` / `navigateSecondaryHistory` | Back/forward per pane |
| Special folders | `navigateSpecial` | Home, Desktop, Documents, Downloads, Pictures |
| Tabs | `openNewTab`, `switchToTab`, `closeTab`, `moveTabFocus` | Ctrl+T/W/Tab; per-pane tab bars |
| Tree | `loadTreeChildren`, `simplefile:tree-node-*` | Lazy children, expand/collapse, drive icons/status |
| Breadcrumb / path bar | `ContentShell.svelte` | Segments, Ctrl+L / Alt+D edit, Enter navigate |
| Watcher refresh | `startDirectoryWatch`, `scheduleFileChangeRefresh` | Debounced reload of affected pane |
| Drive refresh | `refreshDrives` | Offline/stale/unknown badges; retry on click |

`fileNavigation*.ts` is a parallel action facade (`navigateTo`, `toggleDualPane`, selection, preview). The live path is `core.ts` + `setup.ts` custom events; both must stay behavior-equivalent.

### 4.3 Selection, lists, columns, preview

| Workflow | Source | Behavior to keep |
| --- | --- | --- |
| Selection | `selectPaths`, `selectRangeInActivePane`, `selectAllEntries`, `clearActiveSelection` | Click, Ctrl, Shift, keyboard, type-ahead |
| Marquee | `marqueeSelection.ts` | Drag-rect multi-select in list/grid |
| Sort / filter | `applyEntryFilters`, `simplefile:file-list-sort`, quick filter | Hidden files, name filter, column sort |
| Columns | `fileListColumns.ts`, header menu | Presets, visibility, widths, autofit, photo-folder mode |
| Virtualization | `file-list/`, `fileListLazyData.ts` | Huge folders, lazy thumbs, passive folder size/count |
| Preview pane | `updatePreviewPane` | Text/markdown/media via `read_file_preview` + `convertFileSrc` |
| Quick Look | `showQuickLookFlow` | Space; text/image/folder summary |
| Contextual photo view | `applyContextualFolderView` | Auto grid when folder is mostly images |

### 4.4 File operations and progress

| Workflow | Source | Behavior to keep |
| --- | --- | --- |
| New folder / file | `New` menu templates | Unique default names, rename prompt, selection after refresh, validation, undo |
| Rename | `renameSelectedFlow` | Inline/dialog, invalid-name rules |
| Advanced rename | `advanced_rename.ts` | Preview, filters, numbering, templates, `batch_rename` |
| Delete | `deleteSelectedFlow` | Trash vs permanent, confirm setting, Shift+Delete |
| Copy / cut / paste | `copySelection`, `pasteClipboard` | Internal clipboard + history |
| System clipboard paths | `copySelectedPathsToSystemClipboard` | Ctrl+Shift+C |
| Conflict prompt | `chooseConflictAction` | error/skip/replace/rename/keep-both |
| Transfer with progress | `transferEntriesWithSafety`, `transferRunner.ts` | Progress modal, cancel, undo, retry |
| Copy/move to other pane | `copyOrMoveToOtherPane` | Dual-pane only |
| Pack / unpack folder | `packIntoFolderFlow`, `unpackFolderFlow` | Create dir + move; reverse |
| Undo / redo | `undoLastFlow`, `redoLastFlow` | Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z |
| Operation history | `showOperationHistoryFlow` | Running/completed/failed/cancelled + retry |
| Native OS drop | `onExternalFileDrop*` | Overlay + copy into drop target |
| Internal drag | `draggedItems` / `isDragging` | Intra-app move/copy |

### 4.5 Search, smart folders, tags

| Workflow | Source | Behavior to keep |
| --- | --- | --- |
| Quick search | `runSearch` | Seed local matches, stream batches, cancel previous |
| Advanced search | `openAdvancedSearchFlow` | Case, hidden, types, depth, size, dates, content search |
| Clear / restore | `clearSearch`, `restoreDirectoryEntriesAfterSearch` | Restore pre-search entries |
| Smart folders | `loadSmartFoldersFlow`, `saveCurrentSearchAsSmartFolderFlow`, `openSmartFolderFlow`, `deleteSmartFolderFlow` | Persist via Rust JSON file |
| Color labels | `loadTagsFlow`, `showSetColorLabelFlow` | SQLite tags; seed defaults if empty |
| Properties | `showPropertiesFlow` | Size, dates, checksums, metadata, tags |

### 4.6 Archives, tools, settings, updater

| Workflow | Source | Behavior to keep |
| --- | --- | --- |
| Archive viewer | `showArchiveContentsFlow` | List entries, unsafe-path warnings |
| Create archive | `showCreateArchiveFlow`, `confirmCreateArchiveFlow` | zip/tar/tar.gz/rar |
| Extract | `extractArchiveFlow` | Here, into named folder, or picker |
| Folder metrics | `showFolderMetricsFlow` | Progress + cancel |
| Disk cleanup | `showDiskCleanupFlow` | Large files + duplicates |
| Duplicate checker | `showDuplicateCheckerFlow` | Preview/open/reveal/delete extras |
| Open With | `openWithFlow` | Remembered app names |
| Compare files | `compareSelectedFilesFlow` | Side-by-side diff rows |
| Settings | `openSettingsModal`, `saveSettingsFromControls` | Appearance, file list, navigation, behavior, shortcuts, tools, updates, about |
| Keyboard help | `showKeyboardHelpFlow` | F1 / Ctrl+? |
| About | `showAboutFlow` | Version/platform + repo link |
| Updater | `checkForUpdatesFlow`, `installUpdateFlow` | Passive Windows install, then restart |
| WinRAR tool | `updateToolStatus`, `installRarFlow` | Confirm token, hash, publisher |
| Command palette | `CommandPalette.svelte` | Ctrl+Shift+P; includes Git pull/push |

### 4.7 Settings keys that must persist

From `AppSettings` / `createDefaultSettings()`:

- `theme`, `defaultView`, `defaultIconSize`
- `showHidden`, `useTrash`, `confirmDelete`
- `openInNewTab`, `autoCollapseTree`, `showRecentLocations`, `showFolderSizes`
- `enableGitIntegration`
- `startLocation` (`home` \| `last` \| `custom`), `customPath`
- `shortcutOverrides`
- `columnPreset`, `visibleColumns`, `columnWidths`
- `photoFolderMode`, `photoFolderImageThreshold`, `photoFolderIconSize`

Workspace layout snapshot (tabs, dual-pane, paths, histories, preview, columns, icon size) is a separate persistence surface.

---

## 5. Frontend UI surfaces

### 5.1 Layout shell (`frontend/src/lib/components/layout-shell/`)

| File | Responsibility |
| --- | --- |
| `layout-shell.ts` | Mount/unmount `AppShell` into `.app-container` |
| `AppShell.svelte` | Sidebar + resize handle (150–600px) + main column |
| `SidebarShell.svelte` | Title, settings, dual-pane Left/Right target, smart folders, quick access, drive tree, collapse state |
| `ToolbarShell.svelte` | Search, nav buttons, file actions, view/theme/preview/dual-pane, more-actions, icon size |
| `ContentShell.svelte` | Dual tabs, breadcrumbs, path editors, file lists, pane splitter (20–80%) |
| `FileListHeader.svelte` / `FileListHeaderCells.svelte` | Sortable/resizable columns |
| `CommandPalette.svelte` | Fuzzy command list + git pull/push |

### 5.2 Document custom events (`simplefile:*`)

These are the Svelte-to-workflow bus. WinUI should keep equivalent commands even if they become C# events or view-model methods.

**Navigation / lists:** `file-list-item-open`, `file-list-item-click`, `file-list-sort`, `tree-node-open`, `tree-node-toggle`, `tree-node-focus-move`, `tree-node-focus-parent`, `breadcrumb-navigate`, `breadcrumb-focus`, `activate-pane`, `pane-command`, `refresh-drives`

**Toolbar / view:** `toolbar-command`, `toolbar-icon-size`, `open-settings`, `toast`

**Tabs:** `tab-new`, `tab-switch`, `tab-close`, `tab-focus-move`

**Search / smart folders:** `search-submit`, `search-clear`, `search-results-clear`, `search-cancel`, `search-open-advanced`, `search-results-save`, `focus-search`, `quick-filter-input`, `quick-filter-clear`, `smart-folder-open`, `smart-folder-delete`, `smart-folders-changed`

**Inspection / tools:** `properties`, `quick-look`, `quick-look-open`, `quick-look-close`, `preview-close`, `create-archive`, `archive-extract`, `create-archive-confirm`, `advanced-rename`, `advanced-rename-close`, `advanced-rename-confirm`, `advanced-rename-input`, `keyboard-help`, `operation-history`, `set-color-label`, `folder-metrics`, `disk-cleanup`, `duplicate-checker`, `duplicate-checker-close`, `duplicate-checker-delete`, `duplicate-checker-open`, `duplicate-checker-preview`, `duplicate-checker-reveal`, `column-header-menu`, `column-autofit`, `tags-updated`

**Toolbar command ids:** `back`, `forward`, `up`, `refresh`, `new`, `rename`, `copy`, `cut`, `paste`, `delete`, `undo`, `redo`, `clipboard-history`, `operation-history`, `color-label`, `folder-metrics`, `disk-cleanup`, `duplicate-checker`, `view-toggle`, `preview-toggle`, `theme-toggle`, `dual-pane`, `terminal`, `navigateHome`, `navigateDesktop`, `navigateDocuments`, `navigateDownloads`, `navigatePictures`

**Context menu ids:** `ctx-open`, `ctx-open-with`, `ctx-preview`, `ctx-compare`, `ctx-terminal`, `ctx-powershell-admin`, `ctx-color-label`, `ctx-folder-metrics`, `ctx-cleanup`, `ctx-duplicates`, `ctx-rename`, `ctx-advanced-rename`, `ctx-copy`, `ctx-cut`, `ctx-paste`, `ctx-copy-to-pane`, `ctx-move-to-pane`, `ctx-pack`, `ctx-unpack`, `ctx-compress`, `ctx-extract`, `ctx-extract-folder`, `ctx-extract-to`, `ctx-delete`, `ctx-info`

### 5.3 Keyboard shortcuts to preserve

Registered in `setup.ts` (`registerAppShortcuts`). Settings can remap via `shortcutOverrides`.

| Id | Default |
| --- | --- |
| `path.submit` | Enter (path inputs only) |
| `path.focus` | Ctrl+L |
| `path.focus.alt` | Alt+D |
| `nav.parent` | Alt+Up |
| `nav.parent.backspace` | Backspace |
| `nav.back` | Alt+Left |
| `nav.forward` | Alt+Right |
| `directory.refresh` | F5 |
| `selection.up/down/left/right` | arrows |
| `selection.*.extend` | Shift+arrows / Shift+Home / Shift+End |
| `selection.first/last` | Home / End |
| `file.open` | Enter |
| `file.rename` | F2 |
| `file.delete.trash` | Delete |
| `file.delete.permanent` | Shift+Delete |
| `file.copy` / `file.cut` / `file.paste` | Ctrl+C/X/V |
| `file.copyPath` | Ctrl+Shift+C |
| `selection.all` | Ctrl+A |
| `file.newFile` | Ctrl+N |
| `file.newFolder` | Ctrl+Shift+N |
| `tabs.new/close/next/previous` | Ctrl+T / Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab |
| `quickLook.toggle` | Space |
| `search.focus` | Ctrl+F |
| `help.keyboard` | F1 |
| `help.keyboard.ctrl` | Ctrl+? |
| `escape` | Escape (overlay stack, then search, then filter, then selection) |
| `commandPalette.open` | Ctrl+Shift+P |
| `history.undo` | Ctrl+Z |
| `history.redo` | Ctrl+Y |
| `history.redo.shift` | Ctrl+Shift+Z |
| `clipboard.history` | Ctrl+Shift+V |
| `terminal.open` | F4 |
| `pane.toggleDual` | F6 |
| `pane.switch` | Tab (dual-pane only) |
| `pane.focusPrimary` | Alt+1 |
| `pane.focusSecondary` | Alt+2 |
| `pane.focusLeft/Right` | Ctrl+Shift+Left/Right |
| `pane.copyToOther` | Ctrl+Alt+C |
| `pane.moveToOther` | Ctrl+Alt+M |

Escape order: Quick Look → duplicate checker → archive viewer → create archive → advanced rename → keyboard help → about → progress dismiss (no backend cancel) → settings/generic modal → context menu → command palette → search mode → path editor → quick filter → clear selection.

### 5.4 Other frontend modules that must be replaced

| Module | Role |
| --- | --- |
| `appState.ts` | Canonical state + settings/tab/bookmark/operation-log types |
| `vanilla-js/runtime/state.svelte.ts` | Live reactive state + `localStorage` persistence |
| `coreFileManager.ts` | Path join/parent, hidden filter, formatting, name validation |
| `vfs.ts` | `LocalFileSystem` wrapper over `api.ts` |
| `runtime.ts` | Optional action registry / store bridge |
| `keyboardShortcuts.ts` | Combo normalize/validate/dispatch |
| `fileListColumns.ts` / `fileListLazyData.ts` | Columns + lazy thumbs/metrics |
| `fileNavigation*.ts` | Navigation action split |
| `localCommand*.ts` | Command-host implementations (delete, rename, properties, tags, terminal, open with) |
| `searchDialog.ts` / `searchOptions.ts` / `searchStorage.ts` / `searchWorkflow.ts` | Search UI + option mapping |
| `transferClipboard.ts` / `transferPathUtils.ts` / `transferRunner.ts` / `transferUndo.ts` / `transferWorkflow.ts` | Transfer pipeline |
| `viewWorkflow.ts` | Theme, hidden files, filter, icon size |
| `marqueeSelection.ts` | Rubber-band selection |
| `markdownPreviewSecurity.mjs` / `modalHtmlSecurity.mjs` | HTML sanitization for markdown + modal HTML |
| `components/file-list/` | Virtualized list/grid + skeleton |
| `components/places/` | Bookmarks, quick access, recents, smart folders |
| `components/preview-pane/` | Preview body + info; `convertFileSource` for media |
| `components/tabs/`, `breadcrumb/`, `tree-view/`, `status-bar/`, `toasts/` | Chrome |
| `components/settings-body/` | Settings sections including updater + RAR + git |
| Overlay modals | About, advanced rename, archive viewer/create, duplicate checker, generic, keyboard help, progress, Quick Look |

CSS modules under `frontend/src/css/modules/` encode the visual contract (dual-pane, tabs, sidebar, preview, overlays). They do not have to be reused, but layout behavior must match.

### 5.5 Browser-dev fallback (do not ship in WinUI)

`frontend/src/lib/tauri.ts` contains a full in-memory FS used when `import.meta.env.DEV && !window.__TAURI_INTERNALS__.invoke`. WinUI does not need this. The real IPC service must implement the same command names against the Rust backend.

---

## 6. Rust modules

Keep these as the IPC service. Only the Tauri glue (`#[tauri::command]`, `AppHandle::emit`, plugins, `generate_handler!`, window APIs) needs a new host.

| Module | Path | Responsibility | Tauri coupling |
| --- | --- | --- | --- |
| `lib.rs` | `src-tauri/src/lib.rs` | Plugin init, DB setup, handler table, `show_main_window` | Heavy |
| `main.rs` | `src-tauri/src/main.rs` | Windows subsystem, panic log to `%LOCALAPPDATA%\SumaFile\startup.log` | Light |
| `models.rs` | shared serde types | FileEntry, listings, progress, search, archives, git, previews, metadata | None beyond serde |
| `state.rs` | watcher + cancel tokens | Folder size/count, cleanup, duplicate-check, transfer cancel map | None |
| `fs_ops.rs` | create/rename/copy/move/trash/list/size | Uses `tauri_plugin_dialog` for `select_directory`; archive VFS hooks | Medium |
| `dir_list.rs` | Fast listing + chunk channel | `tauri::ipc::Channel` | Medium |
| `drives.rs` | Volume enumeration + network probe timeout | WinAPI only | None |
| `progress.rs` | Copy/move with retries, cancel, emit | `app.emit("operation-progress")` | Medium |
| `watcher.rs` | `notify` watcher + debounce | `app.emit("file-change")` | Medium |
| `preview.rs` | Preview, thumbs, open, reveal, URL | `tauri_plugin_opener` | Medium |
| `search.rs` | Name/glob/content search + cancel registry | `search-results-batch`, `search-complete` | Medium |
| `archive.rs` | zip/tar/tgz/rar list/create/extract + in-archive VFS | Uses `rar_installer` | Light |
| `rar_installer.rs` | Detect/download/verify/install WinRAR | `reqwest` + confirmation token | Light |
| `git.rs` | status / file statuses / pull / push | `CREATE_NO_WINDOW` on Windows | None |
| `terminal.rs` | PowerShell / elevated PowerShell | Process spawn | None |
| `checksum.rs` | MD5/SHA1/SHA256 | None | None |
| `compare.rs` | Line-oriented file diff | None | None |
| `cleanup.rs` | Large files + duplicate groups | Emits `operation-progress` | Medium |
| `metadata.rs` | Image EXIF, PDF, audio, video, office | None | None |
| `open_with.rs` | Launch named application | Process / association | Light |
| `smart_folders.rs` | `{app_data_dir}/smart_folders.json` | `app.path().app_data_dir()` | Medium |
| `db.rs` | `metadata.db` tags + settings KV | `app.path().app_data_dir()` | Medium |
| `tags.rs` | Tag CRUD + file_tags | Through `DbState` | Light |
| `updater.rs` | version/about/check/install | `tauri_plugin_updater`, `update-chunk`, `app.restart()` | Heavy |
| `utils.rs` | Path validation, counts, name rules | None | None |
| `native_accel.rs` | SIMD case-fold / content search helpers | None | None |

`lib.rs` also contains Rust unit tests for path validation, copy/move conflict refusal, batch rename, and create/rename/copy/move smoke. Those tests must keep passing after the host swap.

---

## 7. Persistence and app-data paths

| Store | Key / path | Owner | Contents |
| --- | --- | --- | --- |
| `localStorage` | `simplefile-settings` | Frontend | `AppSettings` JSON |
| `localStorage` | `simplefile-theme` | Frontend | Theme name |
| `localStorage` | `simplefile-workspace-layout` | Frontend | Tabs, dual-pane, paths, histories, columns, preview, icon size |
| `localStorage` | `simplefile-bookmarks` | Frontend | Bookmark list |
| `localStorage` | `simplefile-recent` | Frontend | Recent locations |
| `localStorage` | `simplefile-sidebar-collapse-state` | Sidebar | `{ myPc, quickAccess }` |
| `localStorage` | `simplefile-recent-searches` | Search | Recent queries |
| `localStorage` | `simplefile-open-with-apps` | Open With | Remembered apps |
| `localStorage` | `simplefile-tags` | Legacy frontend write | Do not treat as source of truth; tags live in SQLite |
| `localStorage` | `simplefile-tabs`, `simplefile-active-tab` | Legacy | Migrated into workspace layout then cleared |
| SQLite | `{app_data_dir}/metadata.db` | `db.rs` / `tags.rs` | `tags`, `file_tags`, unused `settings` table |
| JSON | `{app_data_dir}/smart_folders.json` | `smart_folders.rs` | Saved searches |
| Log | `%LOCALAPPDATA%\SumaFile\startup.log` | `main.rs` | Panic hook |

WinUI must keep these stores or migrate them on first launch. Do not silently drop workspace tabs, bookmarks, or color labels.

Backend seed tags (if `tags` is empty): Important, Work, Personal, To Do, Later. Frontend `ensureColorLabelsAvailable()` only creates Red/Orange/Yellow/Green/Blue/Purple when the loaded list is empty.

---

## 8. Tauri-specific dependencies and host config

### 8.1 Must be replaced (Tauri host)

**Rust (`src-tauri/Cargo.toml`):**

- `tauri` 2
- `tauri-build` 2
- `tauri-plugin-dialog` 2 (`select_directory`)
- `tauri-plugin-opener` 2 (`open_file`, `reveal_in_folder`, `open_external_url`)
- `tauri-plugin-updater` 2 (`check_for_update`, `install_update`, `update-chunk`)
- feature `custom-protocol` / `tauri/custom-protocol`

**JS (`frontend/package.json`):**

- `@tauri-apps/api` (`invoke`, `listen`, `Channel`, `convertFileSrc`)
- Svelte / Vite stack (`svelte`, `@sveltejs/vite-plugin-svelte`, `vite`, `svelte-check`, `@tsconfig/svelte`)

**Svelte-only preview libraries (reimplement or keep in a preview helper):**

- `highlight.js`
- `marked`
- `sanitize-html`

### 8.2 Keep with the Rust service

`serde`, `serde_json`, `chrono`, `notify`, `trash`, `md-5`, `sha1`, `sha2`, `tokio`, `parking_lot`, `base64`, `image`, `zip`, `flate2`, `tar`, `unrar`, `walkdir`, `filetime`, `futures`, `log`, `glob`, `once_cell`, `getrandom`, `reqwest` (rustls), `kamadak-exif`, `lopdf`, `lofty`, `rusqlite` (bundled), `winapi` (Windows).

### 8.3 Window / security / bundle that WinUI packaging must match

From `src-tauri/tauri.conf.json`:

| Item | Current value |
| --- | --- |
| Product | SimpleFile |
| Version | 1.0.0 |
| Identifier | `com.simplefile.desktop` |
| Window label | `main` |
| Title | `SimpleFile - File Explorer` |
| Size | 1200×800, min 800×600, resizable, centered, not fullscreen |
| Drag-drop | enabled |
| `withGlobalTauri` | false |
| Bundle targets | `nsis` (currentUser) + `msi` |
| Updater artifacts | `createUpdaterArtifacts: true` |
| Updater endpoint | `https://github.com/conniecombs/SimpleFile-Windows/releases/latest/download/latest.json` |
| Updater Windows mode | `passive` |
| Icons | `src-tauri/icons/*.png` + `icon.ico` |

`tauri.local.conf.json` only disables updater artifacts for local/smoke builds.

Capabilities (`src-tauri/capabilities/default.json`): window `main`, `core:default`, `opener:default`. The WinUI host must not widen this: no arbitrary URL open, no extra filesystem outside the Rust commands.

`convertFileSrc` is used by `PreviewContent.svelte` for `<video>` / `<audio>` `src`. WinUI can use the path directly; do not drop media preview.

---

## 9. Packaging, updater, and release checks

### 9.1 Root npm scripts (`package.json`)

| Script | What it does | Replacement needed |
| --- | --- | --- |
| `dev` | `tauri dev` | WinUI + Rust service dev launch |
| `build` | frontend Vite build | WinUI build |
| `check` | frontend + JS + invokes + Tauri surface + updater + workflows + provider + Windows assets | Split: keep Rust/provider checks; replace Tauri/Svelte checks |
| `check:frontend` | all Svelte stage checks + svelte-check + settings smoke + Vite build | Replace with WinUI UI checks |
| `check:js` | `scripts/check-js-syntax.mjs` | Retire with JS, or keep while Svelte remains |
| `check:invokes` | `scripts/check-tauri-invokes.mjs` | Replace with WinUI ↔ Rust command parity |
| `check:tauri-surface` | renderer may only import `@tauri-apps/api` via `tauri.ts` | Replace with “UI may only talk to IPC client” |
| `check:updater` | pubkey, endpoint, `latest.json`, `.sig`, signing secret | Port to new updater packaging |
| `check:workflows` | CI/release/installer snippets | Update after workflow rewrite |
| `check:provider-surface` | banned retired-provider leftovers | Keep |
| `check:windows-assets` | no icns/android/ios/linux schemas | Keep / adapt |
| `check:rust` | rustfmt + test + clippy `-D warnings` | Keep |
| `check:security` | `scripts/cargo-audit-release.mjs` | Keep |
| `check:release` | `check` + rust + security | Keep, with new UI checks |
| `build:tauri:local` | `cargo tauri build --ci --config tauri.local.conf.json` | Replace with WinUI + Rust packager |
| `smoke:settings` / `smoke:settings-ui` | settings DOM smoke | Replace |
| `smoke:release` / `smoke:msi` / `smoke:installer` | exe/MSI/NSIS smokes | Port to new artifacts |
| `release:build` | `scripts/build-release.ps1` | Port |
| `release:local` | full local release | Port |

### 9.2 Frontend check scripts (`frontend/package.json`)

All of these encode required behavior. They must be rewritten against the WinUI/C# sources (or an IPC contract file) rather than deleted without replacement.

| Script | File | Guards |
| --- | --- | --- |
| `check:migration` | `check-migration-complete.mjs` | Svelte migration completeness |
| `check:api-parity` | `check-api-parity.mjs` | Every Rust handler has `invokeCommand` + `TauriCommandMap` entry |
| `check:behavior-bridges` | `check-behavior-bridges.mjs` | `simplefile:*` event names on chrome |
| `check:core-file-manager` | `check-core-file-manager.mjs` | Path/list helpers |
| `check:stage3-command-surfaces` | `check-stage3-command-surfaces.mjs` | Toolbar/settings/search commands |
| `check:stage4-settings-tools` | `check-stage4-settings-tools.mjs` | Settings + column menu |
| `check:stage5-menu-overlays` | `check-stage5-menu-overlays.mjs` | Context menus + overlays |
| `check:stage7-search-smart-folders` | `check-stage7-search-smart-folders.mjs` | Search + smart folders |
| `check:stage8-file-inspection` | `check-stage8-file-inspection.mjs` | Preview / Quick Look / properties |
| `check:markdown-preview-safety` | `check-markdown-preview-safety.mjs` | Markdown sanitization |
| `check:html-sink-safety` | `check-html-sink-safety.mjs` | Modal HTML sinks |
| `check:huge-folder` | `check-huge-folder-virtualization.mjs` | Virtualized listing |
| `check:marquee-selection` | `check-marquee-selection.mjs` | Marquee select |
| `check:fast-listing` | `check-fast-listing.mjs` | Progressive `onChunk` listing |
| `check:stage9-organization-cleanup` | `check-stage9-organization-cleanup.mjs` | Tags, metrics, cleanup, duplicates |
| `check:stage10-transfer-safety` | `check-stage10-transfer-safety.mjs` | Progress/cancel/conflicts/undo |
| `check:stage11-navigation-dual-pane` | `check-stage11-navigation-dual-pane.mjs` | Dual-pane + tabs + watcher |
| `check:svelte` | `svelte-check` | Typecheck |
| `smoke:settings-ui` | `smoke-settings-ui.mjs` | Settings + dual-pane/preview toggles |

### 9.3 Root scripts and GitHub workflows

| Path | Role | Replacement |
| --- | --- | --- |
| `scripts/check-tauri-invokes.mjs` | Handler ↔ invoke parity | New IPC parity check |
| `scripts/check-tauri-renderer-surface.mjs` | No raw `@tauri-apps/api` outside `tauri.ts` | New IPC client boundary check |
| `scripts/check-updater-config.mjs` | Updater pubkey/endpoint/signing | New updater config check |
| `scripts/check-github-workflows.mjs` | Required CI/release snippets | Update after workflow rewrite |
| `scripts/check-provider-surface.mjs` | Provider-surface leftover ban | Keep |
| `scripts/check-windows-assets.mjs` | Windows-only assets | Keep |
| `scripts/check-js-syntax.mjs` | JS syntax | Keep until JS retired |
| `scripts/cargo-audit-release.mjs` | cargo-audit | Keep |
| `scripts/release.mjs` | Release helper | Port |
| `scripts/build-release.ps1` | Signed Windows build | Port |
| `scripts/smoke-release-startup.ps1` | Launch smoke | Port |
| `scripts/smoke-msi-artifact.ps1` | MSI smoke | Port |
| `scripts/smoke-nsis-install.ps1` | NSIS install/launch/uninstall | Port |
| `scripts/smoke-settings-startup.mjs` | Settings startup | Replace |
| `.github/workflows/ci.yml` | rustfmt/clippy/test + frontend `npm run check` + cargo-audit + Windows cargo build | Swap frontend job; keep Rust jobs |
| `.github/workflows/release.yml` | Version lock vs `tauri.conf.json`/`Cargo.toml`; `cargo tauri build --bundles nsis,msi`; stage setup.exe, MSI, portable zip, `latest.json`, `.sig` | Replace packager; keep artifact set |
| `.github/workflows/release-build.yml` | On-demand unsigned/signed build + smokes | Port |
| `.github/workflows/installer-smoke.yml` | Nightly `tauri.local.conf.json` NSIS/MSI smoke | Port |
| `.github/workflows/dependabot-automerge.yml` | Dependabot auto-merge | Keep |
| `.github/RELEASE.md` + `docs/UPDATER_RELEASE.md` | Human release/updater docs | Update in a later step |

Required release artifacts today:

- `SimpleFile_*_x64-setup.exe` (NSIS, per-user)
- `SimpleFile_*_x64_en-US.msi`
- `SimpleFile_*_x64-portable.zip` (`simplefile.exe`)
- `latest.json` + `.sig` updater files

Secrets: `TAURI_SIGNING_PRIVATE_KEY`, `TAURI_SIGNING_PRIVATE_KEY_PASSWORD`. Any new updater must keep signed update checks or an explicit replacement.

---

## 10. Contract gaps the WinUI host must resolve

These are real current-code facts, not extra features:

1. `show_main_window`, `get_git_status`, `cancel_count_items`, `copy_entry`, `move_entry`, `onOperationComplete`, and `onOperationError` are part of the compatibility contract and are marked in schema as host-owned, legacy, or typed-not-emitted where applicable. Keep them unless a later protocol version removes them.
2. `search-complete` and `update-chunk` **are** emitted. Wire them in WinUI even if the Svelte UI currently ignores the wrappers.
3. `operation-complete` / `operation-error` are **not** emitted. Do not invent them unless both sides agree.
4. `list_directory` streaming is a Channel, not an event.
5. Settings live in `localStorage`, tags in SQLite, smart folders in JSON. Three stores.
6. `select_directory`, opener, and updater are Tauri plugins, not plain Rust. WinUI must supply folder picker, shell-open, and an updater replacement that still talks to the same GitHub `latest.json` contract unless that contract is changed in a later step.
7. Default tag seeds differ between Rust (`Important` / `Work` / …) and frontend fallback (`Red` / `Orange` / …). Preserve both rules: backend seeds on empty DB; frontend only seeds if `get_all_tags` returns empty.

---

## 11. Suggested replacement map (no code yet)

| Current surface | WinUI-era replacement |
| --- | --- |
| `@tauri-apps/api` `invoke` | C# IPC client with the 74 command names and camelCase args |
| `listen` / `emit` | Named event stream from the Rust service |
| `Channel` `onChunk` | Streaming listing API |
| `convertFileSrc` | Direct path / `StorageFile` for media |
| `tauri://drag-*` | WinUI drag-and-drop |
| `tauri-plugin-dialog` | `FolderPicker` / `FileOpenPicker` |
| `tauri-plugin-opener` | `Windows.System.Launcher` or keep Rust opener commands |
| `tauri-plugin-updater` | Keep Rust updater behind IPC, or MSIX/AppInstaller with the same endpoint |
| Svelte layout-shell | WinUI `NavigationView` / custom dual-pane `Grid` |
| `simplefile:*` events | View-model commands |
| Frontend check scripts | C# / IPC contract tests covering the same behaviors |
| `cargo tauri build` | `dotnet publish` + existing NSIS/MSI/portable/updater pipeline |

---

## 12. Out of scope for this inventory step

- No runtime code was changed.
- No Svelte or Tauri files were removed.
- No WinUI project was added.
- No IPC protocol (named pipe, gRPC, JSON-RPC) was chosen.

Next implementation steps should add the WinUI host and an IPC adapter that implements this inventory without deleting `frontend/` or `src-tauri/` Tauri glue until an explicit retirement PR.
