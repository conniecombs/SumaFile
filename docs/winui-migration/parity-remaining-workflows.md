# WinUI Migration — Remaining Workflows Parity

**Status:** Historical parity notes. Svelte/Tauri is no longer the shipping UI.

Tracks feature parity between the Svelte/Tauri frontend and the WinUI 3 native app
for the 9 remaining workflow areas.

## Feature Parity Matrix

| Feature Area | Svelte/Tauri | WinUI 3 | Notes |
|---|---|---|---|
| Archive Listing/Viewer | ✅ `ArchiveViewerModal.svelte` | ✅ `ArchiveViewerDialog.xaml` | Same IPC: `list_archive` |
| Archive Extraction Preflight | ✅ `renderExtractArchivePreflight` | ✅ `ExtractArchiveDialog.xaml` | Unsafe entry warnings preserved |
| Archive Creation | ✅ `CreateArchiveModal.svelte` | ✅ `CreateArchiveDialog.xaml` | Format selection: zip/tar/tar.gz/rar |
| RAR Install Flow | ✅ Settings → Tools tab | ✅ `SettingsDialog.xaml` Tools panel | Full prepare → confirm → install flow |
| Disk Cleanup | ✅ `showDiskCleanupFlow` | ✅ `DiskCleanupDialog.xaml` | Read-only analysis, no deletes |
| Duplicate Checker | ✅ `DuplicateCheckerModal.svelte` | ✅ `DuplicateCheckerDialog.xaml` | Safety invariant preserved |
| Tags / Color Labels | ✅ `showSetColorLabelFlow` | ✅ `TagPickerDialog.xaml` | Default palette seeded from DB |
| Smart Folders | ✅ `SmartFoldersList.svelte` | ✅ Sidebar smart folders section | Save/open/delete via IPC |
| DB-backed Settings | ✅ `SettingsBody.svelte` | ✅ `SettingsDialog.xaml` | 6 categories + search filter |
| App About/Version | ✅ `AboutModal.svelte` | ✅ `AboutDialog.xaml` | Version + OS + GitHub link |
| Update Check + Install | ✅ Settings → Updates tab | ✅ `SettingsDialog.xaml` Updates panel | Check → download → install |
| Open Terminal | ✅ `openTerminal` | ✅ F4 keyboard shortcut | IPC `open_terminal` |

## Tauri Plugin Replacements

| Tauri Plugin | Svelte Usage | WinUI Replacement |
|---|---|---|
| `tauri-plugin-updater` | `check_for_update`, `install_update` | IPC `check_for_update` + `install_update` (Rust handles GitHub endpoint; WinUI calls IPC, no plugin needed) |
| `tauri-plugin-dialog` | `select_directory` | `Windows.Storage.Pickers.FolderPicker` (HOST_OWNED, handled in C#) |
| `tauri-plugin-opener` | `openFile`, `revealInFolder`, `openExternalUrl` | IPC `open_file` / `reveal_in_folder` / `open_external_url` (already wired) |

## Behavioral Contracts Preserved

### Archive Viewer
- Recognizes `.tar.gz`, `.tgz`, `.zip`, `.tar`, `.gz`, `.rar` compound extensions
- Displays format badge, entry count, total/compressed size, compression ratio
- Warning InfoBar for unsafe entries (directory traversal paths)
- Searchable entry list with icon, name, size columns

### Duplicate Checker Safety Invariant
- **At least one file per group must remain unselected** — checkbox disabled when selecting would remove all copies
- "Keep Newest" selects all except most recently modified per group
- "Keep First" selects all except first listed per group
- Post-delete removes entries from UI and recomputes reclaimable bytes

### Tags Default Palette
Seeded on first load if DB tags table is empty:
- Red (#ef4444), Orange (#f97316), Yellow (#eab308), Green (#22c55e), Blue (#3b82f6), Purple (#a855f7)

### RAR Install Flow
1. `check_rar_installed` → status badge
2. `prepare_rar_install` → downloads, validates SHA-256 + Authenticode signature
3. Confirmation dialog showing download source, hash, publisher
4. `install_rar` (or `discard_rar_install` on cancel) → silent install
5. Status badge refreshed

### Smart Folders
- Persisted to `%APPDATA%\com.simplefile.desktop\smart_folders.json`
- Each smart folder stores `SearchOptions` (query, path, filters)
- Sidebar shows 🔍 icon + name + × delete button
- Clicking re-runs the saved search

### Settings Persistence
- All settings stored via IPC `get_db_setting` / `set_db_setting`
- Keys match Svelte frontend: `theme`, `showHidden`, `useTrash`, `confirmDelete`, `startLocation`, `customPath`, `openInNewTab`

## IPC Methods Added

| Method | Return Type | Notes |
|---|---|---|
| `get_all_tags` | `Tag[]` | |
| `create_tag` | `Tag` | |
| `update_tag` | `Tag` | |
| `delete_tag` | void | |
| `get_tags_for_path` | `Tag[]` | |
| `set_tags_for_path` | void | |
| `get_all_file_tags` | `Dictionary<string, Tag>` | |
| `get_files_with_tag` | `string[]` | |
| `load_smart_folders` | `SmartFolder[]` | |
| `save_smart_folder` | `SmartFolder[]` | Returns updated list |
| `delete_smart_folder` | `SmartFolder[]` | Returns updated list |
| `disk_cleanup` | `CleanupResult` | With progress events |
| `cancel_disk_cleanup` | void | |
| `duplicate_check` | `DuplicateCheckResult` | With progress events |
| `cancel_duplicate_check` | void | |
| `check_rar_installed` | `bool` | |
| `prepare_rar_install` | `RarInstallPlan` | |
| `discard_rar_install` | void | |
| `install_rar` | `string` | Returns rar.exe path |
| `get_app_about_info` | `AppAboutInfo` | |
| `check_for_update` | `UpdateInfo?` | |
| `install_update` | void | With update-chunk events |
| `open_terminal` | void | |
| `open_powershell_admin` | void | |
| `get_git_status` | `GitStatus` | |
| `get_git_file_statuses` | `FileEntry[]` | |
| `git_pull` | void | |
| `git_push` | void | |
