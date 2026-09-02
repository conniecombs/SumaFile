# WinUI parity gate

**Date:** 2026-08-15  
**Source tree:** `R:\Repos\SimpleFile-Windows`  
**Contract:** [`inventory.md`](inventory.md) (78 commands / emitted events / Svelte workflows)
**Hosts:** WinUI 3 + `simplefile-service` is the shipping app. Svelte/Tauri UI and packaging glue have been retired.

This is the **retirement lock**. Required `OPEN` rows are none. `MANUAL` rows stay as human smoke coverage. Retired `src-tauri/` domain now lives solely in `crates/simplefile-core`.

Inspected for this gate: `src-winui/SimpleFile.Ipc/Protocol.cs`, `crates/simplefile-service/src/dispatch/`, `src-winui/**`, `ipc/schema/v1/`, `package.json`, `.github/workflows/*`.

---

## Status legend

| Status | Meaning |
| --- | --- |
| `PASS` | Implemented in WinUI/IPC and covered by an automated check that still runs. |
| `MANUAL` | Implemented; no automated UI driver. Must be exercised with the smoke plan before retirement. |
| `OPEN` | Missing or only partial vs Svelte. Blocks deleting legacy UI. |
| `WAIVED` | Explicitly not required for WinUI (contract-only, unused event, or host-owned replacement). Reason is in the row. |

Required = every row except those marked `WAIVED`.

---

## How to run the plan

```powershell
# Automated (CI + local)
npm run check                 # ipc-schema, updater, workflows, packaging, parity-gate
npm run check:winui           # xUnit: navigation, IPC, transfers, polish
npm run check:ipc-schema      # 78-command schema vs Rust/C#
npm run check:winui-packaging
cargo test --locked --all-features

# WinUI smokes (after npm run build:winui:release)
npm run smoke:winui
npm run smoke:winui-file-ops # packaged copy/move/conflict/drop-target/progress IPC smoke
npm run smoke:winui-msi       # needs WiX MSI
npm run smoke:winui-installer # needs NSIS setup
npm run smoke:winui-upgrade   # previous NSIS -> new NSIS
```

Manual host: `npm run dev:winui` or `dist\winui\payload\SumaFile.exe`.

---

## 1. Process / IPC host

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `host.service` | UI starts `simplefile-service` via job object | `BackendSession` + `ServiceLocator` | `BackendSessionTests` | Launch exe; service process appears | `PASS` |
| `host.pipe` | Named pipe JSON-RPC length-prefix | `NamedPipeJsonClient` | `FrameCodecTests`, `NamedPipeJsonClientTests` | — | `PASS` |
| `host.handshake` | `ipc.handshake` first | Client + service dispatch | `BackendSessionTests`, service unit tests | — | `PASS` |
| `host.errors` | `-32000` exact `Err(String)`; `CONFLICT:`; `TRASH_UNAVAILABLE:`; `HOST_OWNED:` | `IpcException` + `FileOperationService` | `IpcExceptionTests`, `FileOperationServiceTests` | Conflict / trash fallback dialogs | `PASS` |
| `host.select_directory` | Folder picker is host-owned | `FolderPicker` in Settings / extract-to | Service returns `HOST_OWNED:`; `BackendSessionTests` | Browse custom start path | `PASS` |
| `show_main_window` | Service no-op; UI `Activate()` | IPC method kept | Schema + client method | — | `WAIVED` | Service `Ok(())`; no Svelte live caller |
| `host.convertFileSrc` | Media via filesystem path | Preview uses path / base64 | — | Open image preview | `PASS` |
| `host.browser-dev-fs` | In-memory Tauri DEV FS | Not ported | — | — | `WAIVED` | Inventory §5.5: do not ship |

---

## 2. IPC commands (78)

Each command must appear here. Service registry is `crates/simplefile-service/src/dispatch/`. C# names are `SimpleFile.Ipc.Protocol` + `ISimpleFileIpc`.

### 2.1 Filesystem and listing

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `get_home_dir` | Home path | `ExplorerWorkspace.InitializeAsync` | `ExplorerWorkspaceTests` | Starts at home | `PASS` |
| `select_directory` | Host picker | Settings / extract-to | `HOST_OWNED` test | Browse | `PASS` |
| `list_drives` | This PC | Sidebar drive list | `ExplorerWorkspaceTests` | Offline badge | `PASS` |
| `list_directory` | Listing + chunks | `list_directory.chunk` then result | `ExplorerWorkspaceTests` huge-folder / `RESULT_TOO_LARGE` | First paint on large folder | `PASS` |
| `list_subdirectories` | Sidebar tree children | `LoadTreeChildrenAsync` + Folders list | `ParityFeaturesTests` tree flatten | Expand a tree node | `PASS` |
| `create_directory` | New folder | Dialog + IPC | `FileOperationServiceTests` | Ctrl+Shift+N | `PASS` |
| `create_file` | New file | Dialog + IPC | `FileOperationServiceTests` | Ctrl+N | `PASS` |
| `delete_entry` | Permanent delete | Shift+Delete confirm | `FileOperationServiceTests` | Shift+Delete | `PASS` |
| `move_to_trash` | Recycle Bin | Delete / setting | `FileOperationServiceTests` trash prefix | Delete; network `TRASH_UNAVAILABLE:` | `PASS` |
| `restore_recycle_bin` | Restore Recycle Bin items | Context Restore | Core recycle_bin tests | Restore a deleted file | `PASS` |
| `empty_recycle_bin` | Empty Recycle Bin | Command palette | Core recycle_bin tests | Empty Recycle Bin | `PASS` |
| `rename_entry` | Rename | F2 dialog | `FileOperationServiceTests` | F2 | `PASS` |
| `batch_rename` | Advanced rename apply | Prefix/suffix/number dialog | IPC wrapper | Advanced rename on 3 files | `MANUAL` |
| `copy_entry` | Legacy single copy | IPC kept | Schema | — | `PASS` |
| `move_entry` | Legacy single move | IPC kept | Schema | — | `PASS` |
| `copy_entry_resolved` | Conflict-aware copy / undo | Undo stack redo | `UndoStack` tests | Undo a copy | `PASS` |
| `move_entry_resolved` | Conflict-aware move / undo | Undo stack | `UndoStack` tests | Undo a move | `PASS` |
| `get_entry_info` | Properties / type probe | Properties dialog | IPC wrapper | Properties on file | `MANUAL` |
| `copy_with_progress` | Copy + progress | Paste / drop / pane copy | `FileOperationServiceTests` | Copy large folder; cancel | `PASS` |
| `move_with_progress` | Move + progress | Cut-paste / drop | `FileOperationServiceTests` | Move across folders | `PASS` |
| `cancel_operation` | Progress cancel | Progress panel | `FileOperationServiceTests` | Cancel mid-copy | `MANUAL` |
| `watch_directory` | Live refresh | After navigate | Client + MainWindow watch | Create file in Explorer; pane reloads | `MANUAL` |
| `unwatch_directory` | Drop watch | Shutdown / navigate | Client | — | `PASS` |
| `calculate_folder_size` | Folder metrics | Metrics dialog | IPC wrapper | Folder metrics on a folder | `MANUAL` |
| `count_folder_items` | Folder metrics | Metrics dialog | IPC wrapper | Same dialog | `MANUAL` |
| `get_folder_metrics` | Combined folder metrics | Metrics dialog | IPC service | Folder metrics on a folder | `PASS` |
| `cancel_folder_size` | Abort size on nav | Wired on IPC | Schema/client | Navigate during metrics | `MANUAL` |
| `cancel_folder_item_count` | Abort counts | IPC | Schema/client | — | `PASS` |
| `cancel_count_items` | Unused wrapper | IPC kept | Schema | — | `WAIVED` | No live Svelte caller |
| `cancel_folder_metrics` | Abort combined metrics | IPC service | Schema | Navigate during metrics | `PASS` |

### 2.2 Preview, open, inspection

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `read_file_preview` | Preview pane / Quick Look | Preview + Space dialog | `FileOperationServiceTests` | Text + image files | `PASS` |
| `generate_thumbnail` | Single thumb | Preview image fallback | IPC wrapper | Image without inline preview | `MANUAL` |
| `generate_thumbnails` | Batch thumbs | `GenerateThumbnailsAsync` + preview thumbs | FileOps wrapper | Preview image folder | `PASS` |
| `open_file` | Default app / archive materialize | Double-click file | `ExplorerWorkspace` + FileOps | Double-click `.txt` | `PASS` |
| `reveal_in_folder` | Explorer select | Preview Reveal | IPC | Reveal selected | `MANUAL` |
| `open_external_url` | http(s) only | About / GitHub | IPC | About link | `MANUAL` |
| `open_file_with` | Named app | Open With dialog | IPC | Open With notepad | `MANUAL` |
| `compare_files` | Two-file diff | Compare dialog | IPC | Select two files → Compare | `MANUAL` |
| `compute_checksum` | MD5/SHA1/SHA256 | Preview checksums | IPC | Checksums button | `MANUAL` |
| `get_image_metadata` | EXIF | Preview metadata | IPC | JPEG with EXIF | `MANUAL` |
| `get_file_metadata` | Unified metadata | Preview metadata | IPC | PDF / audio | `MANUAL` |

### 2.3 Search, smart folders, organization

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `search_files` | Search + batches | Sidebar search box | Client batch callbacks | Search current folder | `MANUAL` |
| `cancel_search` | Cancel / Escape | Cancel button + Escape | Client | Cancel long search | `MANUAL` |
| `load_smart_folders` | Sidebar list | Initialize load | Workspace init | Sidebar shows saved folders | `MANUAL` |
| `save_smart_folder` | Save current search | Sidebar Save button | Workspace method | Save current query | `PASS` |
| `delete_smart_folder` | Sidebar × | Delete button | Workspace method | Delete a smart folder | `MANUAL` |
| `disk_cleanup` | Large-file analyze | Disk cleanup dialog | IPC + progress | Analyze a folder | `MANUAL` |
| `cancel_disk_cleanup` | Cancel analyze | IPC | Schema/client | — | `PASS` |
| `duplicate_check` | Duplicate groups | Duplicate checker dialog | IPC + progress | Find duplicates | `MANUAL` |
| `cancel_duplicate_check` | Cancel scan | IPC | Schema/client | — | `PASS` |

### 2.4 Archives and WinRAR

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `list_archive` | Archive viewer | `ArchiveViewerDialog` | `ArchivePaths` tests | Open a zip | `MANUAL` |
| `extract_archive` | Extract here / folder / to | Context extract + dialog | IPC | Extract zip | `MANUAL` |
| `create_archive` | zip/tar/tar.gz/rar | Create archive dialog | IPC | Compress selection | `MANUAL` |
| `check_rar_installed` | Tools badge | Settings → Tools | Settings load | Tools tab | `MANUAL` |
| `prepare_rar_install` | Stage installer | Settings install flow | IPC | Install RAR (optional) | `MANUAL` |
| `discard_rar_install` | Cancel staged | Settings cancel | IPC | Cancel confirm | `MANUAL` |
| `install_rar` | Silent install | Settings confirm | IPC | — | `MANUAL` |

### 2.5 Git, terminals, tags, settings, updater

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `get_git_status` | Repo status | IPC only | Schema/client | — | `WAIVED` | Typed; no live Svelte caller |
| `get_git_file_statuses` | Git column | `ApplyGitStatusesAsync` + `FileRow.GitText` | Workspace + FileRow | Enable Git; open a repo | `PASS` |
| `git_pull` | Palette Git pull | Command palette | Catalog test | Git pull in a repo | `MANUAL` |
| `git_push` | Palette Git push | Command palette | Catalog test | Git push | `MANUAL` |
| `open_terminal` | F4 / context | IPC | — | F4 | `MANUAL` |
| `open_powershell_admin` | Context | IPC | Context menu ID | Elevate PS | `MANUAL` |
| `get_all_tags` | Color labels | Tag picker | Workspace seed | Set label | `MANUAL` |
| `create_tag` | Seed defaults | Empty-DB seed | Workspace `DefaultTags` | Fresh profile | `MANUAL` |
| `update_tag` | Tag editor | `UpdateTagAsync` | Workspace method | Rename a label | `MANUAL` |
| `delete_tag` | Tag editor | `DeleteTagDefinitionAsync` | Workspace method | Delete a label | `MANUAL` |
| `get_tags_for_path` | Per-file tags | IPC | Schema/client | Properties | `MANUAL` |
| `set_tags_for_path` | Apply label | Tag picker | Workspace `SetColorLabelAsync` | Set / clear label | `MANUAL` |
| `get_files_with_tag` | Filter by label | `SetTagFilter` / `FilesWithTag` | Workspace filter | Click a tag | `PASS` |
| `get_all_file_tags` | Color dots | `FileRow.TagColor` | ToFileRow maps tags | Labeled files show color | `PASS` |
| `get_db_setting` | Settings KV | Settings dialog | Workspace restore test | Change theme; relaunch | `PASS` |
| `set_db_setting` | Persist settings | Settings save | Workspace save | Same | `PASS` |
| `get_app_version` | Updates tab | Settings | Settings load | Settings → Updates | `MANUAL` |
| `get_app_about_info` | About | Settings About + dialog | IPC | About panel | `MANUAL` |
| `check_for_update` | Check updates | Settings Updates | Rust signed-metadata tests + schema | Check for updates | `PASS` |
| `install_update` | Install + restart handshake | Settings install | Rust verify path + FileOperationService progress tests | Signed build smoke | `PASS` |

---

## 3. Events

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `file-change` | Watcher refresh | MainWindow subscription | Client `On<T>` | External create/delete | `MANUAL` |
| `operation-progress` | Copy/move/cleanup/dup | Progress panel | `FileOperationServiceTests` | Watch bar + cancel | `PASS` |
| `search-results-batch` | Incremental search | Search box batches | Client | Search streams rows | `MANUAL` |
| `search-complete` | Count notification | Status text | Client complete callback | Search finishes | `PASS` |
| `update-chunk` | Updater download | Settings install progress | FileOperationService progress subscription test | Signed update smoke | `PASS` |
| `list_directory.chunk` | First-chunk paint | Workspace progressive list | `ExplorerWorkspaceTests` | Huge folder | `PASS` |
| `operation-complete` | Unused typed event | Must **not** invent | Schema `typedNotEmitted` | — | `WAIVED` |
| `operation-error` | Unused typed event | Must **not** invent | Schema `typedNotEmitted` | — | `WAIVED` |
| `tauri://drag-*` | OS drag | WinUI `DragOver`/`Drop` | `DropDestination` tests | Drop files from Explorer | `PASS` |

---

## 4. Navigation, tabs, sidebar

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `nav.home-start` | Start at home | `ResolveStartPath` | `ExplorerWorkspaceTests` | Cold start | `PASS` |
| `nav.start-last` | `startLocation=last` | Settings + `LastPath` | `ResolveStartPath` unit | Set Last; relaunch | `MANUAL` |
| `nav.start-custom` | Custom start path | Settings + picker | `ResolveStartPath` unit | Set custom; relaunch | `MANUAL` |
| `nav.quick-access` | Home/Desktop/Downloads/Documents/Pictures | Sidebar list | Workspace constants | Click each | `PASS` |
| `nav.drives` | My PC + refresh | Drive list + ↻ | `RefreshDrives` tests | Refresh; offline retry | `PASS` |
| `nav.tree` | Expandable folder tree | Sidebar Folders list | `FolderTree` tests | Expand/open | `PASS` |
| `nav.breadcrumbs` | Click segments | `BreadcrumbBuilder` | `BreadcrumbBuilderTests` | Click crumb | `PASS` |
| `nav.path-edit` | Ctrl+L / Alt+D / Enter / Escape | Path box | — | Edit path | `MANUAL` |
| `nav.history` | Back/forward per pane | History stack | `ExplorerWorkspaceTests` | Alt+Left/Right | `PASS` |
| `nav.up` | Parent; no-op on root | `GoUpAsync` | `GoUp` test | Alt+Up at `C:\` | `PASS` |
| `nav.open-folder` | Double-click / Enter folder | `OpenEntryAsync` | Workspace tests | Open folder | `PASS` |
| `nav.open-archive-folder` | Zip as folder | `IsSupportedArchivePath` | `OpenArchiveFile_NavigatesIntoArchive` | Open zip | `PASS` |
| `nav.dual-pane` | F6; first enable copies path | `ToggleDualPaneAsync` | `DualPaneAndTabsTests` | F6 twice | `PASS` |
| `nav.pane-activate` | Click / Alt+1/2 / Ctrl+Shift+Left/Right | `ActivatePane` | Dual-pane tests | Switch panes | `PASS` |
| `nav.pane-tab` | Tab switches panes when not in text | `OnRootPreviewKeyDown` | — | Tab in dual | `MANUAL` |
| `nav.pane-resize` | 20–80% divider | Divider handlers | — | Drag divider | `MANUAL` |
| `nav.sidebar-target` | Left/Right follows active | `SidebarTarget` | Dual-pane tests | Dual + Desktop on right | `PASS` |
| `nav.tabs` | Per-pane tabs Ctrl+T/W/Tab | `FileTab` | `DualPaneAndTabsTests` | New/close/cycle | `PASS` |
| `nav.tabs-middle` | Middle-click close | Pointer handler | — | Middle-click tab | `MANUAL` |
| `nav.tabs-arrows` | Arrow wrap on tab | `OnTabKeyDown` | — | Focus tab; Left/Right | `MANUAL` |
| `nav.tabs-persist` | Restore workspace | `workspace-layout` IPC | `Initialize_RestoresSavedWorkspaceLayoutFromIpcSettings` | Relaunch after tabs | `PASS` |
| `nav.profiles` | Named workspace profiles | `workspace-profiles` IPC + Profiles toolbar | `WorkspaceProfiles_SaveApplyDuplicateExportResetAndDelete` | Apply each built-in profile | `PASS` |
| `nav.folder-view-settings` | Per-folder view defaults | `folder-view-settings` IPC + View options | `FolderViewSettings` tests | Save folder/default scopes; revisit folders | `PASS` |
| `nav.sidebar-collapse` | Persist Quick Access / My PC | Settings keys | Save/load settings | Collapse; relaunch | `MANUAL` |
| `nav.bookmarks` | Bookmark list | Sidebar Bookmarks | `PlacesStore` tests | Pin current folder | `PASS` |
| `nav.recents` | Recent locations | Sidebar Recent | `PlacesStore` recents cap | Navigate; see recents | `PASS` |
| `nav.network-retry` | Offline drive dialog | `PendingReconnect` | Workspace test | Offline mapped drive | `MANUAL` |

---

## 5. Lists, selection, columns, preview

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `list.hide-dot` | Hide `.` files by default | `ShowHiddenFiles` | `EntryPresentationTests` | Toggle show hidden | `PASS` |
| `list.sort` | Dirs-first; name/size/date/type | Header clicks | `EntryPresentationTests` | Click headers | `PASS` |
| `list.columns-default` | Name, Size, Modified, Type | Header + `FileRowView` | `ColumnLayout` tests | — | `PASS` |
| `list.columns-resize` | Drag header thumbs | `ColumnLayout.Resize` | Clamp/preset tests | Drag thumbs | `PASS` |
| `list.columns-presets` | details/media/developer/photo | `ColumnLayout.ApplyPreset` | `ColumnLayout` tests | Settings column preset | `PASS` |
| `list.extra-columns` | items/git/extension/path/parent/symlink | FileRow git/extension/tag | ColumnLayout + FileRow | Enable git column | `PASS` |
| `list.multi-select` | Ctrl/Shift multi | `ListView` Multiple | — | Ctrl-click range | `MANUAL` |
| `list.marquee` | Rubber-band | `MarqueeSelection` + ListView multi-select | `ParityFeaturesTests` | Drag-select rows | `PASS` |
| `list.typeahead` | Type-to-select | `MatchTypeAhead` on list keys | `ParityFeaturesTests` | Type letters in a list | `PASS` |
| `list.quick-filter` | Filter box | `SetFilterQuery` | Presentation + workspace tests | Type in filter | `PASS` |
| `list.cut-dim` | Cut items dim | `FileRow.IsCut` | — | Cut; see opacity | `MANUAL` |
| `list.virtualize` | Huge folders | WinUI `ListView` default | — | Folder with 20k files | `MANUAL` |
| `list.thumbs` | Grid/list thumbs | `generate_thumbnail(s)` + preview | FileOps | Open image folder | `PASS` |
| `list.folder-sizes` | Passive sizes/counts | `FillFolderSizesAsync` | Workspace | Enable show folder sizes | `PASS` |
| `list.grid-photo` | Auto grid for photo folders | `PhotoFolderActive` | `ParityFeaturesTests` | Open a photo folder | `PASS` |
| `preview.pane` | Side preview | Preview column | — | Select file | `MANUAL` |
| `preview.toggle` | Hide/show preview | Preview button | — | Toggle | `MANUAL` |
| `preview.quicklook` | Space | `ShowQuickLookAsync` | — | Space | `MANUAL` |
| `preview.markdown-html` | Sanitized markdown HTML | WinUI shows text/image, not HTML | Svelte `check:markdown-preview-safety` remains | — | `WAIVED` | Do not render unsanitized HTML; if HTML preview is added, this becomes `OPEN` |
| `preview.modal-html` | Modal HTML sinks | Native XAML dialogs | Svelte `check:html-sink-safety` remains | — | `WAIVED` | No `innerHTML` in WinUI |

---

## 6. File operations and progress

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `ops.new-folder` | Name prompt + validation | Dialog | FileOps tests | Ctrl+Shift+N | `PASS` |
| `ops.new-file` | Name prompt | Dialog | FileOps tests | Ctrl+N | `PASS` |
| `ops.rename` | F2 | Dialog | FileOps tests | F2 | `PASS` |
| `ops.advanced-rename` | Full templates/filters/numbering | `AdvancedRename` find/replace/number | `ParityFeaturesTests` | Advanced rename | `PASS` |
| `ops.delete-confirm` | Confirm setting | Settings + dialog | — | Toggle confirm | `MANUAL` |
| `ops.clipboard` | Copy/cut/paste | `ClipboardState` | `ClipboardStateTests` | Ctrl+C/X/V | `PASS` |
| `ops.copy-path` | Ctrl+Shift+C | System clipboard | — | Paste path in Notepad | `MANUAL` |
| `ops.conflict` | Probe + Skip/Replace/Keep Both | `ConflictDialog` + `DropDestination` | `DropDestination` tests + packaged file-op smoke | Paste onto existing name | `PASS` |
| `ops.progress` | Modal + cancel | `ProgressPanel` | FileOps progress + packaged file-op smoke | Large copy | `PASS` |
| `ops.escape-progress` | Escape hides UI, no cancel | Escape stack | — | Escape during copy | `MANUAL` |
| `ops.copy-to-pane` | Ctrl+Alt+C | `CopyOrMoveToOtherPaneAsync` | Context ID test | Dual-pane copy | `MANUAL` |
| `ops.move-to-pane` | Ctrl+Alt+M | Same | Context ID test | Dual-pane move | `MANUAL` |
| `ops.pack` | Pack into folder | `PackIntoFolderAsync` | — | Pack selection | `MANUAL` |
| `ops.unpack` | Unpack folder | `UnpackFolderAsync` | — | Unpack a folder | `MANUAL` |
| `ops.undo` | Ctrl+Z copy/move | `UndoStack` | `DesktopPolishTests` | Undo paste | `PASS` |
| `ops.redo` | Ctrl+Y / Ctrl+Shift+Z | `UndoStack` | Same | Redo | `PASS` |
| `ops.op-history` | Full retry log | `OperationLog` + `RetryOperationAsync` | Workspace methods | Palette → history | `PASS` |
| `ops.clipboard-history` | Ctrl+Shift+V | `ClipboardHistory` | `ParityFeaturesTests` | Palette → clipboard history | `PASS` |
| `ops.drop-internal` | Intra-app move/copy | Drag handlers | `DropDestination` + packaged drop-target smoke | Drag between panes | `PASS` |
| `ops.drop-external` | OS drop copies in | `StorageItems` | `DropDestination` + packaged drop-target smoke | Drop from Explorer | `PASS` |
| `ops.archive-aware-io` | Copy/move inside archive VFS | Service/core | Core tests via Rust | Copy inside zip | `MANUAL` |

---

## 7. Command palette, menus, shortcuts, chrome

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `ui.command-palette` | Ctrl+Shift+P | Overlay + `AppCommandCatalog` | `DesktopPolishTests` | Open; run Refresh | `PASS` |
| `go-home` | Palette Go Home | Catalog + handler | Catalog test | — | `PASS` |
| `go-recycle-bin` | Palette Recycle Bin | Catalog + handler | Catalog + workspace tests | Open Recycle Bin | `PASS` |
| `restore-selected` | Restore Recycle Bin selection | Catalog + handler | Catalog test | Restore from Bin | `PASS` |
| `empty-recycle-bin` | Empty Recycle Bin | Catalog + handler | Catalog test | Empty Bin | `PASS` |
| `go-back` `go-forward` `go-up` | Palette history navigation | Catalog + handler | Catalog test | Alt+Left/Right/Up | `PASS` |
| `focus-path` | Focus path bar | Catalog + handler | Catalog test | Ctrl+L / Alt+D | `PASS` |
| `refresh` | Palette/F5 | Handler | Catalog | F5 | `PASS` |
| `copy` `cut` `paste` | Palette clipboard | Handlers | Catalog + clipboard tests | — | `PASS` |
| `clipboard-history` | Palette | `ClipboardHistory` | Catalog + tests | — | `PASS` |
| `operation-history` | Palette | `OperationLog` retry | Catalog + workspace | — | `PASS` |
| `clear-recent-history` | Palette clears recents | `ClearRecentHistoryAsync` | Catalog test | — | `PASS` |
| `undo` `redo` | Palette | Undo stack | Tests | — | `PASS` |
| `delete` `delete-permanent` `rename` `new-folder` `new-file` | Palette | Dialogs | Catalog | — | `PASS` |
| `advanced-rename` | Palette | Find/replace/number | Catalog + rename tests | — | `PASS` |
| `create-archive` | Palette | Dialog | Catalog | — | `MANUAL` |
| `terminal` | Palette / F4 | IPC | Catalog | F4 | `MANUAL` |
| `preview` | Toggle preview | Handler | Catalog | — | `MANUAL` |
| `toggle-hidden` | Show or hide hidden files | Handler + workspace setting | Catalog test | Ctrl+H | `PASS` |
| `toggle-side-menu` | Toggle sidebar | Handler | Catalog test | — | `PASS` |
| `dual-pane` | Toggle | Handler | Dual-pane tests | F6 | `PASS` |
| `switch-pane` | Switch active pane | Catalog + shortcut handler | Catalog test | Tab in dual pane | `PASS` |
| `close-left-pane` | Close left pane | Palette + handler | — | Dual pane | `PASS` |
| `profile-manage` | Manage workspace profiles | Profiles dialog | Profile workspace tests | Open manager | `PASS` |
| `profile-save` | Save current workspace profile | Save dialog | Profile workspace tests | Save profile | `PASS` |
| `profile-standard` `profile-developer` `profile-photos` `profile-transfer` `profile-minimal` | Apply built-in profiles | Profile command handlers | Profile workspace tests | Apply each built-in | `PASS` |
| `view-details` `view-list` `view-tiles` `view-content` | Palette display style commands | Handler applies file-list presentation | Catalog test | Switch each view | `MANUAL` |
| `icon-size-small` `icon-size-medium` `icon-size-large` `icon-size-extra-large` `icon-size-jumbo` `icon-size-huge` `icon-size-maximum` | Palette icon size commands | Handler updates file-list icon size | Catalog test | Change each icon size | `MANUAL` |
| `search` | Focus search | Handler | Catalog | Ctrl+F | `MANUAL` |
| `filter` | Focus filter | Handler | Catalog | Overflow filter | `MANUAL` |
| `quick-look` | Space | Handler | Catalog | Space | `MANUAL` |
| `open-selected-tab` `open-other-pane` `reopen-closed-tab` | Tab and pane open commands | Catalog + handlers | Tab workspace tests | Ctrl+Enter / reopen tab | `PASS` |
| `properties` | Properties | Dialog | Catalog | — | `MANUAL` |
| `color-label` | Tag picker | Dialog | Catalog | — | `MANUAL` |
| `bookmark-folder` | Bookmark current folder | Workspace places | Catalog + places tests | Ctrl+B | `PASS` |
| `folder-metrics` | Metrics | Dialog | Catalog | — | `MANUAL` |
| `disk-cleanup` | Cleanup | Dialog | Catalog | — | `MANUAL` |
| `duplicate-checker` | Duplicates | Dialog | Catalog | — | `MANUAL` |
| `settings` | Settings | Dialog | Catalog | Ctrl+Shift+S | `MANUAL` |
| `command-palette` | Open command palette | Handler | Catalog test | Ctrl+Shift+P | `PASS` |
| `keyboard-help` | F1 | Dialog | Catalog + shortcut map | F1 | `PASS` |
| `git-pull` `git-push` | Palette | IPC | Catalog | — | `MANUAL` |
| `ctx-open` | Context Open | `ContextMenuBuilder` | `DesktopPolishTests` | Right-click | `PASS` |
| `ctx-open-tab` `ctx-open-other-pane` | Context folder navigation | `ContextMenuBuilder` + handler | Context menu tests | Right-click folder | `PASS` |
| `ctx-open-with` `ctx-open-with-app-` `ctx-open-with-choose` | Open With | Builder | Same | — | `PASS` |
| `ctx-preview` | Quick Look | Builder | Same | — | `PASS` |
| `ctx-compare` | Compare | Builder | Same | Two files | `PASS` |
| `ctx-view-archive` | View archive contents | Builder + handler | `DesktopPolishTests` | Right-click archive | `PASS` |
| `ctx-terminal` | Terminal | Builder | Same | — | `PASS` |
| `ctx-powershell-admin` | Admin PS | Builder | Same | — | `PASS` |
| `ctx-color-label` | Color label | Builder | Same | — | `PASS` |
| `ctx-folder-metrics` | Metrics | Builder | Same | — | `PASS` |
| `ctx-cleanup` | Cleanup | Builder | Same | — | `PASS` |
| `ctx-duplicates` | Duplicates | Builder | Same | — | `PASS` |
| `ctx-rename` | Rename | Builder | Same | — | `PASS` |
| `ctx-advanced-rename` | Advanced rename | Builder | Same | — | `PASS` |
| `ctx-copy` `ctx-cut` `ctx-paste` | Clipboard | Builder | Same | — | `PASS` |
| `ctx-copy-path` | Copy full path | Builder + handler | Context menu tests | Paste path in Notepad | `PASS` |
| `ctx-bookmark` | Bookmark selected folder | Builder + handler | Context menu tests | Right-click folder | `PASS` |
| `ctx-copy-to-pane` `ctx-move-to-pane` | Other pane | Builder | Same | Dual pane | `PASS` |
| `ctx-close-dual-pane` | Close right pane | Builder + handler | `DesktopPolishTests` | F6 or pane menu | `PASS` |
| `ctx-close-left-pane` | Close left pane | Builder + handler | `DesktopPolishTests` | Dual pane | `PASS` |
| `ctx-pack` `ctx-unpack` | Pack/unpack | Builder | Same | — | `PASS` |
| `ctx-compress` | Compress | Builder | Same | — | `PASS` |
| `ctx-extract-menu` `ctx-extract` `ctx-extract-folder` `ctx-extract-to` | Extract menu | Builder | Same | Archive | `PASS` |
| `ctx-delete` `ctx-delete-menu` `ctx-delete-recycle` `ctx-delete-permanent` | Delete menu | Builder | `DesktopPolishTests` | Delete / Shift+Delete | `PASS` |
| `ctx-info` | Properties | Builder | Same | — | `PASS` |
| `ctx-restore` | Restore Recycle Bin item | Recycle context menu | Context menu tests | Restore | `PASS` |
| `ctx-empty-recycle-bin` | Empty Recycle Bin | Recycle context / more menu | Context menu tests | Empty Bin | `PASS` |
| `keys.path.focus` | Ctrl+L / Alt+D | Accelerators | `KeyboardShortcutMap` | Focus path | `PASS` |
| `keys.nav` | Alt+arrows, Backspace, F5 | Accelerators | Shortcut map | — | `PASS` |
| `keys.file` | F2 Del Shift+Del Ctrl+C/X/V/N | Accelerators | Shortcut map | — | `PASS` |
| `keys.tabs` | Ctrl+T/W/Tab | Accelerators | Dual-pane tests | — | `PASS` |
| `keys.panes` | F6 Alt+1/2 Ctrl+Alt+C/M | Accelerators | Dual-pane tests | — | `PASS` |
| `keys.escape-order` | Full overlay stack | Partial (palette, path, progress hide, search, filter, selection) | — | Escape through each overlay | `MANUAL` |
| `keys.help.ctrl` | Ctrl+? | Ctrl+Divide accelerator | Shortcut map | Ctrl+/ | `PASS` |
| `keys.shortcut-overrides` | Settings remaps | `ApplyOverrides` + persisted map | Shortcut tests | — | `PASS` |
| `ui.theme` | Dark/light | ThemeDictionaries + settings | NormalizeTheme test | Switch theme | `MANUAL` |
| `ui.status` | Count / selection size / path | `StatusBarFormatter` | Formatter tests | Select files | `PASS` |
| `ui.empty-loading` | Empty / loading overlays | `UpdateEmptyStates` | — | Empty folder | `MANUAL` |
| `ui.a11y` | Automation names | XAML + tab/crumb names | — | Inspect with Accessibility Insights | `MANUAL` |
| `ui.window` | 1200×800 title | `MainWindow` ctor | Smoke title | — | `PASS` |

---

## 8. Settings, persistence, updater, packaging

| ID | Feature | WinUI verification | Automated | Manual | Status |
| --- | --- | --- | --- | --- | --- |
| `set.theme` | Theme | Settings Appearance | Save/load | — | `MANUAL` |
| `set.showHidden` | Hidden files | Toggle | Workspace `SetShowHidden` | — | `PASS` |
| `set.useTrash` `set.confirmDelete` | Delete behavior | Settings Behavior | Settings apply | — | `MANUAL` |
| `set.keepFoldersOnTop` | Folders on top vs mixed sort | Settings Behavior | `EntryPresentationTests` | Toggle; sort by name | `PASS` |
| `set.startLocation` | home/last/custom | Settings Navigation | `ResolveStartPath` | — | `PASS` |
| `set.openInNewTab` | Open in tab | `OpenPathAsync` opens a tab | Workspace | Toggle; open folder | `PASS` |
| `set.enableGit` | Git integration | `ApplyGitStatusesAsync` | Workspace | Enable Git in a repo | `PASS` |
| `set.showFolderSizes` | Folder sizes | `FillFolderSizesAsync` | Workspace | Enable folder sizes | `PASS` |
| `set.columnPreset` | Column preset | `ApplyPreset` | `ColumnLayout` | Change preset | `PASS` |
| `persist.workspace` | Tabs/dual/sort | `workspace-layout` | Restore test | Relaunch | `PASS` |
| `persist.appdata` | `%APPDATA%\com.simplefile.desktop` | Service `Host::app_data_dir` | Rust/service | Tags survive | `PASS` |
| `persist.startup-log` | `%LOCALAPPDATA%\SumaFile\startup.log` | App crash log + service panic | Crash path exists | Force a parse error | `PASS` |
| `upd.latest-json` | Retired Tauri `latest.json` | Replaced by `latest-winui.json` | — | — | `WAIVED` | Shipping updater is WinUI |
| `upd.latest-winui` | `latest-winui.json` | `write-latest-winui.mjs` | `check:updater` / packaging | — | `PASS` |
| `pkg.tauri` | Retired Tauri NSIS/MSI/`latest.json` | Replaced by WinUI packagers | — | — | `WAIVED` | Tauri packagers removed |
| `pkg.winui-portable` | `x64-winui-portable.zip` | `build-winui-release.ps1` | Packaging check + `smoke:winui` | Unzip and launch | `PASS` |
| `pkg.winui-nsis` | `x64-winui-setup.exe` | NSIS script | Packaging check + `smoke:winui-installer` on CI | — | `PASS` |
| `pkg.winui-msi` | `x64-winui.msi` | WiX `Product.wxs` | Packaging check + `smoke:winui-msi` on CI | — | `PASS` |
| `pkg.winui-upgrade` | Previous NSIS to new NSIS | `smoke-winui-upgrade.ps1` | `smoke:winui-upgrade` on CI | — | `PASS` |
| `pkg.legacy-keep` | Retired Svelte/Tauri packagers | Removed after gate close | Retirement lock | — | `WAIVED` | Retirement completed |

---

## 9. Automated check matrix

| Check | What it gates |
| --- | --- |
| `npm run check:winui-parity-gate` | This file lists every handler, ctx id, palette id, and a status |
| `npm run check:ipc-schema` | 78 commands + events vs Rust/C# |
| `npm run check:winui` | xUnit: workspace, dual-pane, IPC, file ops, polish |
| `npm run check:winui-packaging` | NSIS/WiX/scripts/workflows |
| `npm run check:updater` / `check:workflows` | WinUI updater + installer artifacts |
| `npm run check:rust` | Core/ipc/service tests + clippy |
| `npm run smoke:winui` | Payload exe title + service process |
| `npm run smoke:winui-file-ops` | Packaged service copy/move/conflict/drop-target/progress |
| `npm run smoke:winui-msi` / `smoke:winui-installer` / `smoke:winui-upgrade` | Installer extract/install/upgrade (CI) |

---

## 10. Manual smoke script (required before retirement)

Use a clean folder with mixed files (txt, png, zip), a git repo, and a large folder if possible.

1. Launch `dist\winui\payload\SumaFile.exe`. Title is **SumaFile**. Home lists.
2. Quick Access: Desktop, Downloads, Documents, Pictures, Home.
3. Breadcrumb click; path edit Enter/Escape; Up at drive root is a no-op.
4. F6 dual pane; Alt+2; sidebar Desktop opens on the **right** only.
5. Ctrl+T / Ctrl+Tab / Ctrl+W / middle-click tab; relaunch restores tabs.
6. Multi-select, copy, paste, conflict Skip/Replace/Keep Both, Undo/Redo. Packaged contract: `npm run smoke:winui-file-ops`.
7. Delete (trash) and Shift+Delete; confirm setting off/on.
8. Drag between panes (move) and Ctrl-drag (copy); drop from Explorer (copy).
9. Search current folder; cancel; Escape clears search.
10. Right-click: Open, Open With, Quick Look, Compress, Extract, Pack, tags.
11. Preview pane: text, image, checksums, compare two files.
12. Settings: theme light/dark, show hidden, start last/custom, RAR status, check updates (expect stub or GitHub result).
13. Smart folder: save current search, open it, delete it.
14. F4 terminal; F1 help; Ctrl+Shift+P command palette.
15. Watcher: create a file in Explorer; list refreshes.
16. Close app; confirm `simplefile-service` exits.

---

## Retirement lock

Required `OPEN` rows: **none**. Remaining `MANUAL` rows are implemented and listed above for human smoke.

**Retirement completed** 2026-08-15. Removed `frontend/` and unused Tauri packaging glue. Keep `crates/simplefile-core`, `crates/simplefile-ipc`, and `crates/simplefile-service`. Keep leftover `src-tauri/src` domain until those modules live solely in `simplefile-core`. Keep this file.

Gate check: `npm run check:winui-parity-gate`.
