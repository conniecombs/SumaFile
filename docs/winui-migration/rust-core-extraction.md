# Rust Core Extraction Plan

**Date:** 2026-08-14  
**Status:** Historical extraction plan. Svelte/Tauri UI is retired. Leftover `src-tauri/src` domain now lives in `simplefile-core` and is dispatched by `simplefile-service`.  
**Source tree:** `R:\Repos\SimpleFile-Windows`  
**Constraint at write time:** classify and plan only. Do **not** move files in this step. Do **not** delete Svelte/Tauri.  
**Contract:** [`inventory.md`](inventory.md) (74 commands) and [`architecture.md`](architecture.md) (named-pipe JSON-RPC, `Host` in `simplefile-core` from PR 3).

Inspected: every module under `src-tauri/src/` (`lib.rs`, `main.rs`, and the 23 domain files). Classification is from the current signatures and `use` lines, not from the architecture doc alone.

## Classification key

| Class | Meaning |
| --- | --- |
| **Core** | Pure backend logic. No `AppHandle`, `State`, `Channel`, `Emitter`, or plugin types. Moves into `simplefile-core` as-is (or with a `PathBuf` instead of `AppHandle`). |
| **Wrapper** | `#[tauri::command]` that injects Tauri types then calls Core. Stays in `src-tauri` as a thin adapter after extraction. The IPC service gets a parallel adapter. |
| **Shell** | App-shell integration: window, dialog, opener, updater plugin, `app.path()`, `app.emit`, `app.restart()`. Becomes a `Host` method or stays host-local. |
| **UI-only** | Not Rust domain. WinUI / Svelte owns it. The service either no-ops or returns `HOST_OWNED`. |

A function can be a **Wrapper** that immediately delegates to **Core**, or a **Wrapper+Shell** mix that must be split.

---

## Target layout (no moves yet)

```text
crates/simplefile-core/src/
  host.rs                 # NEW: Host trait + AppAboutInfo (architecture PR 3)
  lib.rs
  models.rs               # from src-tauri/src/models.rs
  state.rs
  utils.rs
  native_accel.rs
  dir_list.rs
  archive.rs
  rar_installer.rs
  fs_ops.rs               # minus select_directory dialog
  progress.rs
  search.rs
  watcher.rs
  cleanup.rs
  preview.rs              # minus opener plugin
  db.rs
  tags.rs
  smart_folders.rs
  drives.rs
  git.rs
  terminal.rs
  checksum.rs
  compare.rs
  metadata.rs
  open_with.rs
  updater.rs              # download/verify later; stub at Gate 3

src-tauri/src/
  lib.rs                  # generate_handler! + plugins + manage()
  main.rs                 # panic log + simplefile::run()
  tauri_host.rs           # NEW: TauriHost only
  commands/*.rs           # thin #[tauri::command] wrappers (or keep names in place)

crates/simplefile-service/src/
  main.rs
  ipc_host.rs             # IpcHost only
  dispatch.rs             # 74 JSON-RPC methods → core
```

`src-tauri` modules stay as `mod` wrappers until each extract PR. After a move, `src-tauri` re-exports so `cargo test -p simplefile` and `lib.rs` tests still compile.

---

## Module coupling summary

| File | Tauri types today | Move destination | Blocking coupling |
| --- | --- | --- | --- |
| `models.rs` | none | core PR 4 | none |
| `state.rs` | none (`notify` watcher handle only) | core PR 4 | none |
| `utils.rs` | none | core PR 4 | none |
| `native_accel.rs` | none | core PR 4 | none |
| `dir_list.rs` | `tauri::ipc::Channel` | core PR 3/5 | replace Channel with `FnMut` |
| `archive.rs` | `Option<&AppHandle>` on VFS + `create_archive` | core PR 5 | only to call `resolve_rar_binary` |
| `rar_installer.rs` | `AppHandle` + `Manager::path()` | core PR 5 | `app_data_dir()` for `rar/` |
| `fs_ops.rs` | dialog, Channel, `AppHandle`, `State<Arc<AppState>>`, `spawn_blocking` | core PR 6 | dialog + archive `AppHandle` + cancel state |
| `progress.rs` | `AppHandle`, `Emitter`, `State<Arc<AppState>>` | core PR 6 | emit + cancel map |
| `search.rs` | `AppHandle`, `Emitter` | core PR 6 | emit batches |
| `watcher.rs` | `AppHandle`, `Emitter`, `State<Arc<AppState>>` | core PR 6 | emit + store watcher in `AppState` |
| `cleanup.rs` | `AppHandle`, `Emitter`, `State<Arc<AppState>>` | core PR 6 | emit + cancel flags |
| `preview.rs` | `AppHandle`, `OpenerExt` | core PR 7 | opener; archive temp materialize is Core |
| `db.rs` | `AppHandle`, `Manager`, `State<DbState>` | core PR 8 | `app_data_dir()` + Tauri `State` |
| `tags.rs` | `State<DbState>` | core PR 8 | needs a `Connection`, not Tauri State |
| `smart_folders.rs` | `AppHandle`, `Manager` | core PR 8 | `app_data_dir()` |
| `drives.rs` | `tauri::async_runtime` only | core PR 9 | spawn_blocking is wrapper |
| `git.rs` | `State<DbState>` on pull/push | core PR 9 | token lookup via settings |
| `terminal.rs` | command attr only | core PR 9 | none in helpers |
| `checksum.rs` | command attr only | core PR 9 | none |
| `compare.rs` | command attr only | core PR 9 | none |
| `metadata.rs` | command attr only | core PR 9 | none |
| `open_with.rs` | command attr only | core PR 9 | none |
| `updater.rs` | `AppHandle`, `UpdaterExt`, `Emitter`, `app.restart()` | stub in service PR 10; real client PR 19 | plugin + restart |
| `lib.rs` | Builder, plugins, `manage`, handler table | stay in `src-tauri` | Shell |
| `main.rs` | none (panic log) | stay; service copies hook | Shell-adjacent |

---

## Per-function classification

### `main.rs` — stay in `src-tauri`

| Function | Class | Notes |
| --- | --- | --- |
| `main` | Shell | `windows_subsystem`, calls `simplefile::run()` |
| `install_panic_logger` | Core (copy) | Service `main` installs the same hook |
| `write_panic_log` | Core (copy) | `%LOCALAPPDATA%\SumaFile\startup.log` |

### `lib.rs` — stay in `src-tauri`

| Function | Class | Notes |
| --- | --- | --- |
| `show_main_window` | UI-only / Shell | `get_webview_window("main")`. Service no-op. WinUI `Activate()`. |
| `run` | Shell | plugins, `init_db`, `manage(DbState)`, `manage(Arc<AppState>)`, `generate_handler!` |
| `tests::*` | Core tests | Call `fs_ops` / `utils` directly. After extract, `use simplefile_core::...` or re-exports. |

### `models.rs` — Core, move whole file (PR 4)

No functions. Structs: `FileEntry`, `DirectoryListing`, `DirectoryListingChunk`, `ProgressUpdate`, `FileChangeEvent`, `DriveInfo`, `TreeNode`, `SearchResult`, `SearchOptions`, `SmartFolder`, `DuplicateGroup`, `CleanupResult`, `DuplicateCheckFile`, `DuplicateCheckGroup`, `DuplicateCheckResult`, `GitStatus`, `FilePreview`, `ThumbnailResult`, `ImageMetadata`, `FileMetadata`, `ArchiveEntry`, `ArchiveInfo`. Also `RenameRequest` lives in `fs_ops.rs` (`pub use` from commands).

`AppAboutInfo` / `UpdateInfo` currently live in `updater.rs` and move to core with `Host` (PR 3), not with this file.

### `state.rs` — Core, move whole file (PR 4)

| Item | Class | Notes |
| --- | --- | --- |
| `WatcherState` | Core | Holds `notify::RecommendedWatcher` |
| `AppState` | Core | Cancel flags + watcher. **Not** Tauri `State` itself. |
| `Default` | Core | |
| `begin_folder_size` / `cancel_folder_size` | Core | |
| `begin_item_count` / `cancel_item_count` | Core | |
| `begin_folder_item_count` / `cancel_folder_item_count` | Core | |
| `cancel_disk_cleanup` / `cancel_duplicate_check` | Core | |

**Risk:** Tauri injects `Arc<AppState>` via `app.manage`. After extract, both hosts own an `Arc<AppState>`. Do not put `tauri::State` into core.

### `utils.rs` — Core, move whole file (PR 4)

All Core: `hidden_command`, `dirs_home`, `format_system_time`, `format_modified`, `build_file_entry`, `get_file_entry`, `get_file_entry_from_dir_entry`, `is_network_path`, `generate_operation_id`, `validate_existing_path`, `validate_existing_path_no_resolve`, `validate_path_no_follow`, `validate_name`, `is_windows_invalid_name_character`, `is_windows_reserved_device_name`, `symlink_target_classification_path`, `classify_symlink_target`, `recreate_symlink`, `should_cancel`, `count_directory_entries`, `count_items_scoped`, plus unit tests.

### `native_accel.rs` — Core, move whole file (PR 4)

All Core SIMD/portable helpers + tests. Used by `search.rs` (`contains_case_insensitive`) and listing sort (`dirs_first_name_key`).

### `dir_list.rs` — Core after Channel removal (PR 3 then 5)

| Function | Class | Extract |
| --- | --- | --- |
| `list_directory_blocking` | Wrapper+Core | Change `on_chunk: Channel<...>` → `impl FnMut(DirectoryListingChunk) -> Result<(), String>` |
| `list_directory_for_test` | Core | Already Channel-free |
| `enumerate_directory` | Core | |
| `enumerate_directory_std` | Core | |
| `enumerate_directory_windows` | Core | `FindFirstFileExW` |
| `file_entry_from_find_data` | Core | |
| `filetime_to_string` | Core | |

Calls `archive::list_archive_directory` — cannot land in core before archive VFS (PR 5).

### `archive.rs` — Core after dropping `AppHandle` (PR 5)

| Function | Class | Extract |
| --- | --- | --- |
| `list_archive` | Wrapper | Thin command over list_* helpers |
| `extract_archive` | Wrapper | |
| `create_archive` | Wrapper+Shell | `AppHandle` only for `rar_installer::resolve_rar_binary(&app)` |
| `split_archive_path`, `is_archive_virtual_path`, `list_archive_directory` | Core | Used by `fs_ops` / `dir_list` / `preview` |
| `list_zip/tar/rar_archive`, extract/create zip/tar/rar, path sanitizers, remap, unique paths | Core | |
| `should_handle_transfer` | Core | |
| `copy_entry_resolved` / `move_entry_resolved` / `delete_archive_entry` / `create_archive_{directory,file}` / `rename_archive_entry` | Core+Shell | Last arg `Option<&AppHandle>` is only forwarded to `mutate_archive` → `resolve_rar_binary` |
| `materialize_archive_entry_to_temp` | Core | Used by `preview::open_file` / `read_file_preview` |
| `mutate_archive`, `rebuild_archive_from_directory` | Core+Shell | Same `AppHandle` thread |

**Extract rule:** replace `Option<&AppHandle>` with `Option<&impl Host>` or `&Path` from `Host::app_data_dir()`. Zip/tar paths never need it.

### `rar_installer.rs` — Core after `app_data_dir` (PR 5)

| Function | Class | Extract |
| --- | --- | --- |
| `rar_install_dir` | Shell→Core | `app.path().app_data_dir().join("rar")` → `Host::app_data_dir()?.join("rar")` |
| `local_rar_binary` | Shell→Core | same |
| `resolve_rar_binary` | Shell→Core | PATH / local / `C:\Program Files\WinRAR` |
| `rar_in_path`, `winrar_system_binary` | Core | |
| `check_rar_installed` | Wrapper | |
| `prepare_rar_install` | Wrapper | no AppHandle; downloads to temp |
| `install_rar` | Wrapper+Shell | needs install dir from Host |
| `discard_rar_install` | Wrapper | process-local pending map |
| `prepare_rar_install_inner`, pending map, Authenticode, download, sha256 | Core | pending tokens are **process-local statics** — a service restart drops them (same as today’s process lifetime) |

### `fs_ops.rs` — split (PR 6)

| Function | Class | Extract |
| --- | --- | --- |
| `get_home_dir` | Wrapper | calls `utils::dirs_home` |
| `select_directory` | UI-only / Shell | `tauri_plugin_dialog`. **Does not move to core.** Tauri wrapper keeps the dialog. Service returns `-32001` `HOST_OWNED: select_directory`. |
| `list_directory` | Wrapper | `Channel` + `spawn_blocking` → `dir_list::list_directory_blocking` |
| `create_directory` / `create_file` | Wrapper+Core | archive VFS branch, then local create |
| `delete_entry` | Wrapper+Shell | `AppHandle` only for archive VFS delete |
| `move_to_trash` | Wrapper+Shell | same; local path uses `trash` + `TRASH_UNAVAILABLE:` |
| `rename_entry` | Wrapper+Core | archive branch, no AppHandle |
| `batch_rename` | Wrapper+Core | |
| rename helpers (`path_collision_key`, temp rename, rollback) | Core | |
| `cancel_count_items` | Wrapper | `state.cancel_item_count()` |
| `get_entry_info` | Wrapper | `spawn_blocking` + `get_file_entry` |
| `copy_entry` / `move_entry` | Wrapper+Core | |
| `copy_dir_iterative`, metadata/DACL preserve, exclusive copy | Core | |
| `list_subdirectories` | Wrapper+Core | |
| `cancel_folder_size` / `cancel_folder_item_count` | Wrapper | `AppState` methods |
| `count_folder_items` / `calculate_folder_size` | Wrapper | `State<Arc<AppState>>` + `spawn_blocking` + scoped Core walk |
| `calculate_size_recursive_scoped` | Core | |
| `copy_entry_resolved` / `move_entry_resolved` | Wrapper+Shell | `AppHandle` only when `archive::should_handle_transfer` |
| `resolve_destination`, keep-both, exclusive create | Core | |

`tauri::async_runtime::spawn_blocking` stays in the wrapper (or becomes `tokio::task::spawn_blocking` in the service). Core functions remain blocking.

### `progress.rs` — Core after `Host::emit` (PR 6)

| Function | Class | Extract |
| --- | --- | --- |
| `copy_with_progress` / `move_with_progress` | Wrapper | `AppHandle` + `State<Arc<AppState>>` + `spawn_blocking` |
| `cancel_operation` | Wrapper | `cancelled_operations` map; error `"Operation not found"` |
| `transfer_with_progress_blocking` | Core+Shell | takes `AppHandle` today for `ProgressContext::emit` |
| `ProgressContext::emit` | Shell | `app.emit("operation-progress", ProgressUpdate { ... })` |
| plan/copy/move/estimate/conflict helpers | Core | `CONFLICT:` strings, keep-both, network retries |
| `clear/is/check_cancelled` | Core | `Arc<AppState>` (the struct, not Tauri State) |

**Extract rule:** `ProgressContext` holds `&impl Host` (or an `Fn` emit sink), not `&AppHandle`.

### `search.rs` — Core after `Host::emit` (PR 6)

| Function | Class | Extract |
| --- | --- | --- |
| `search_files` | Wrapper+Shell | `AppHandle` for emit; `spawn_blocking` walk |
| `cancel_search` | Wrapper | process-local `SEARCH_CANCEL_FLAGS` |
| `search_files_bfs` | Core+Shell | `app.emit("search-results-batch")` |
| parse/filter/content match helpers | Core | uses `native_accel` |
| `search-complete` emit after walk | Shell | keep in wrapper or `Host::emit` |

`SEARCH_CANCEL_FLAGS` is a module static, not `AppState`. Move the static with the module. A respawned service has an empty map (same as process restart today).

### `watcher.rs` — Core after `Host::emit` (PR 6)

| Function | Class | Extract |
| --- | --- | --- |
| `watch_directory` | Wrapper+Shell | builds `notify` watcher, `app.emit("file-change")`, stores in `AppState.watcher_state` |
| `unwatch_directory` | Wrapper | drops watcher |
| `is_ignored_watcher_path` / `debounce_eviction_cutoff` | Core | tests already exist |

Need an emit sink in the watcher callback. `Host` must be `Clone + 'static` (or wrap `Arc<dyn Host>`).

### `cleanup.rs` — Core after `Host::emit` (PR 6)

| Function | Class | Extract |
| --- | --- | --- |
| `disk_cleanup` / `duplicate_check` | Wrapper+Shell | `AppHandle` + `State<Arc<AppState>>` |
| `cancel_disk_cleanup` / `cancel_duplicate_check` | Wrapper | |
| `emit_cleanup_progress` / `emit_duplicate_progress` | Shell | `operation-progress` with fixed operation ids |
| `scan_disk_cleanup` / `scan_duplicate_check` + hash helpers | Core | take `&AtomicBool` + emit callback |

### `preview.rs` — Core + opener Shell (PR 7)

| Function | Class | Extract |
| --- | --- | --- |
| `read_file_preview` | Wrapper+Core | 10 MB text / `max_size*5` image / 20 MB PDF. Archive paths via `materialize_archive_entry_to_temp` |
| `generate_thumbnail` / `generate_thumbnails` | Wrapper+Core | |
| `open_external_url` | Shell | `reqwest::Url` scheme check + `OpenerExt::open_url`. Core: scheme check. Host: open http(s) only. |
| `open_file` | Core+Shell | archive materialize is Core; `opener().open_path` is Shell |
| `reveal_in_folder` | Shell | `opener().reveal_item_in_dir` |
| `resolve_readable_path` | Core | |

**Extract rule:** Core `open_file_path` / `reveal` / `open_url` call `Host::open_path` / `reveal_in_folder` / `open_url`. Do not take `tauri-plugin-opener` into core. Keep archive VFS materialize in core so in-archive Open still works.

### `db.rs` — Core after dropping Tauri State (PR 8)

| Item | Class | Extract |
| --- | --- | --- |
| `DbState` | Shell wrapper | `Mutex<Option<Connection>>` for `tauri::State`. Core should own `Mutex<Connection>` (or `Arc<Mutex<Connection>>`) created in service/`run` setup. |
| `init_db` | Shell→Core | takes `&AppHandle` only for `app.path().app_data_dir()`. Change to `init_db(app_data_dir: &Path)`. Seeds Important/Work/Personal/To Do/Later. Path: `{app_data_dir}/metadata.db`. |
| `get_db_setting` / `set_db_setting` | Wrapper | take `&Connection` (or core `Db`) not `tauri::State` |

Used by `tags.rs` and `git.rs` (`github_token`).

### `tags.rs` — Core (PR 8)

All eight commands (`get_all_tags`, `create_tag`, `update_tag`, `delete_tag`, `get_tags_for_path`, `set_tags_for_path`, `get_files_with_tag`, `get_all_file_tags`) are **Wrappers** around rusqlite. `Tag` struct is Core.

**Extract rule:** `fn get_all_tags(db: &Db) -> Result<Vec<Tag>, String>` etc. Tauri wrappers lock `DbState` and call core. Error `"Database not initialized"` stays if setup skipped — service `main` must call `init_db` before accept (architecture).

### `smart_folders.rs` — Core (PR 8)

| Function | Class | Extract |
| --- | --- | --- |
| `get_smart_folders_path` | Shell→Core | `app.path().app_data_dir().join("smart_folders.json")` |
| `load/save/delete_smart_folder` | Wrapper | pass `PathBuf` from Host |

### `drives.rs` — Core (PR 9)

| Function | Class | Extract |
| --- | --- | --- |
| `list_drives` | Wrapper | only `tauri::async_runtime::spawn_blocking` |
| `list_drives_blocking` + WinAPI helpers + tests | Core | |

### `git.rs` — Core (PR 9)

| Function | Class | Extract |
| --- | --- | --- |
| `get_git_status` / `get_git_file_statuses` | Wrapper+Core | no Tauri types; `CREATE_NO_WINDOW` |
| `git_pull` / `git_push` | Wrapper+Shell | `State<DbState>` only to read `github_token` |
| `get_git_credentials` | Shell | `get_db_setting(..., "github_token")` |
| `git_remote_args` / `run_git_remote_command` | Core | |

**Extract rule:** `git_pull(path, token: Option<String>)`. Wrapper loads the token from core db.

### `terminal.rs` — Core (PR 9)

`validate_terminal_directory`, `spawn_detached`, `powershell_encoded_command` are Core. `open_terminal` / `open_powershell_admin` are Wrappers with no Tauri types beyond the attribute.

### `checksum.rs` — Core (PR 9)

`compute_checksums`, `hex_encode`, tests: Core. `compute_checksum` is a Wrapper (`#[tauri::command]`).

### `compare.rs` — Core (PR 9)

`compare_files` Wrapper; `read_text_file`, `split_lines`, `build_diff_ops`, `build_rows`, tests: Core.

### `metadata.rs` — Core (PR 9)

Both commands are Wrappers around Core extractors (`extract_image_metadata`, PDF/audio/video/office helpers). The `"Channels"` string in audio fields is a label, not Tauri `Channel`.

### `open_with.rs` — Core (PR 9)

Allow-list resolvers are Core. `open_file_with` is a Wrapper that `Command::new` the resolved exe (no opener plugin).

### `updater.rs` — Shell; stub at Gate 3, real client PR 19

| Function | Class | Extract |
| --- | --- | --- |
| `get_app_version` | Shell | `app.package_info().version` → `Host::app_version()` |
| `get_app_about_info` | Shell | hardcoded + `package_info`. `AppAboutInfo` type moves to core PR 3. Framework/runtime strings become host-specific. |
| `check_for_update` | Shell | `tauri_plugin_updater` |
| `install_update` | Shell | download + `emit("update-chunk")` + **`app.restart()`** |
| `build_configured_updater` | Shell | empty-endpoints → `"App updates are not configured for this build."` |

Until PR 19, the IPC service returns that exact empty-endpoint string for check/install. Tauri wrappers stay on the plugin.

---

## Exact file / module moves (ordered, still not performed)

Matches architecture PRs 3–10. Each row is one future PR’s filesystem work.

| PR | From `src-tauri/src/` | To | `src-tauri` leftover |
| --- | --- | --- | --- |
| 3 | *(new)* `host.rs` created in core; `list_directory_blocking` signature change in place | `crates/simplefile-core/src/host.rs` | `tauri_host.rs` (new). `dir_list` still here until PR 5, but Channel-free |
| 4 | `models.rs`, `state.rs`, `utils.rs`, `native_accel.rs` | `crates/simplefile-core/src/` | `pub use simplefile_core::{...}` so tests compile |
| 5 | `dir_list.rs`, `archive.rs`, `rar_installer.rs` | core | wrappers: `list_archive`, `extract_archive`, `create_archive`, RAR commands |
| 6 | `fs_ops.rs` (except `select_directory`), `progress.rs`, `search.rs`, `watcher.rs`, `cleanup.rs` | core | wrappers + `select_directory` dialog stays |
| 7 | `preview.rs` domain | core | wrappers that call `Host` / opener |
| 8 | `db.rs`, `tags.rs`, `smart_folders.rs` | core | `DbState` Tauri adapter + `init_db` in `run()` |
| 9 | `drives.rs`, `git.rs`, `terminal.rs`, `checksum.rs`, `compare.rs`, `metadata.rs`, `open_with.rs` | core | thin commands |
| 10 | none (new crate) | `crates/simplefile-service` | Tauri still ships |
| 19 | updater download/minisign logic | core `updater.rs` | Tauri plugin wrappers until Gate 7 |

Do **not** move `lib.rs`, `main.rs`, `tauri.conf.json`, `capabilities/`, or `frontend/`.

Internal `crate::` edges that must stay in one crate after each PR:

```text
dir_list → archive::list_archive_directory
fs_ops   → archive::{split, create/delete/rename/copy/move VFS}
fs_ops   → dir_list::list_directory_blocking
fs_ops   → state::AppState, utils::*
preview  → archive::{is_archive_virtual_path, materialize_archive_entry_to_temp}
archive  → rar_installer::resolve_rar_binary
progress → state::AppState, utils::generate_operation_id, archive? (local only today)
search   → native_accel, utils::validate_existing_path_no_resolve
watcher  → state::AppState, models::FileChangeEvent
cleanup  → state::AppState
tags     → db connection
git      → db::get_db_setting("github_token")
smart_folders / db / rar_installer → Host::app_data_dir()
```

---

## Risks (named types)

### `AppHandle`

Used for: `path().app_data_dir()`, `emit`, `opener()`, `updater()`, `dialog()`, `package_info()`, `restart()`, `get_webview_window`, and as a token into archive/RAR.

**Risk:** leaking `AppHandle` into core creates a `simplefile-core` → `tauri` dependency and blocks the IPC binary.

**Mitigation:** `Host` trait in core from PR 3. Only `TauriHost` / `IpcHost` see `AppHandle`. Archive/RAR take `&impl Host` or a `PathBuf`.

### `tauri::State<T>` / `State<'_, DbState>` / `State<'_, Arc<AppState>>`

**Risk:** `State` is a Tauri extractor. Core cannot name it. Tags/git/db commands would fail to compile after the move.

**Mitigation:** Core APIs take `&AppState`, `&Connection` / `Arc<Mutex<Connection>>`. Wrappers lock Tauri State and pass references. Service holds the same `Arc`s in `IpcHost`.

### `Emitter` / `app.emit`

Sites: `progress.rs`, `search.rs`, `watcher.rs`, `cleanup.rs`, `updater.rs`.

**Risk:** event names or payloads drift if each host reimplements emit. Watcher callback needs `'static` + `Send`.

**Mitigation:** `Host::emit(&self, event: &str, payload: &Value)`. `Host` is `Clone` via `Arc`. Keep the five real event names. Do not add `operation-complete` / `operation-error`.

### Opener (`tauri_plugin_opener`)

`open_file`, `reveal_in_folder`, `open_external_url`.

**Risk:** core cannot depend on the plugin. Archive Open must still materialize a temp file **before** the host opens it.

**Mitigation:** Core resolves/materializes the path; `Host::open_path` / `reveal_in_folder` / `open_url` perform the shell action. URL scheme check (`http`/`https` only) stays in core.

### Updater (`tauri_plugin_updater`)

**Risk:** `install_update` calls `app.restart()` and never returns. A naive port would kill the service mid-install or double-launch (architecture: UI exits first, NSIS relaunches).

**Mitigation:** Gate 3 service stubs with `"App updates are not configured for this build."` Tauri keeps the plugin. PR 19 implements minisign + `update-chunk` + `request_restart` (notification only).

### Dialog (`tauri_plugin_dialog`)

Only `select_directory`.

**Risk:** putting a folder picker in the service has no window parent and breaks WinUI `FolderPicker`.

**Mitigation:** do not extract. Tauri wrapper keeps `DialogExt`. Service method stays registered and returns `HOST_OWNED`.

### `DbState`

```rust
pub struct DbState {
    pub conn: Mutex<Option<Connection>>,
}
```

**Risk:** `Option` exists because Tauri `setup` runs after `manage`. Core `init_db` must run before any tag/git command. Two processes (two SimpleFile instances) already contend on `%APPDATA%\com.simplefile.desktop\metadata.db` — SQLite file lock, no extra mutex in this extraction.

**Mitigation:** `init_db(&app_data_dir)` returns `Connection`. Hosts store it. Preserve seed tags and path. `github_token` stays a settings row.

### `Arc<AppState>`

**Risk:** watcher + cancel maps are process-local. Sharing one `AppState` across Tauri and the IPC service is impossible (two processes). Cloning the struct without `Arc` would split cancel flags from the walk.

**Mitigation:** each process has its own `Arc<AppState>`. Cancel commands in that process flip that Arc. Do not serialize `AppState` over the pipe. Folder-size generations stay in-process, matching today’s single-process model.

### Other extract risks

| Risk | Severity | Mitigation |
| --- | --- | --- |
| `dir_list` ↔ `archive` cycle if listing moves first | major | PR 5 moves both (and RAR) together |
| `pub(crate)` visibility after crate split | major | make needed items `pub` on the core crate; keep wrappers thin |
| `crate::` paths in tests (`lib.rs` filesystem smoke) | major | re-export from `src-tauri` or change tests to `simplefile_core` in the same PR as the move |
| Root workspace lockfile vs `cd src-tauri && cargo test --locked` | major | PR 1 already specified: move `Cargo.lock` to repo root, update CI |
| Pending RAR tokens / search cancel map are statics | minor | document process-local lifetime; service restart clears them |
| `tauri::async_runtime::spawn_blocking` in commands | minor | wrappers keep it; core stays sync |
| Frontend-only `itemCount` on `FileEntry` | nit | not a Rust field; do not add it to `models.rs` |

---

## Frontend-only concerns (do not extract into Rust)

These are not `src-tauri/src` functions. They must not gain a Rust home during extraction:

- WebView `localStorage` keys (settings, workspace, bookmarks, recents)
- `convertFileSrc` media URLs
- Markdown / modal HTML sanitizers
- Dual-pane, tabs, sidebar, shortcuts, command palette, marquee
- `tauri://drag-*`
- `show_main_window` behavior
- `select_directory` picker UI
- Browser-dev fallback in `frontend/src/lib/tauri.ts`

---

## Verification for this step

No runtime files were moved or edited. After a future extract PR, run from repo root (once PR 1 exists):

```text
cargo test -p simplefile --locked --all-features
cargo test -p simplefile-core --locked --all-features
cargo clippy --locked --all-targets --all-features -- -D warnings
npm run check:invokes
npm --prefix frontend run check:api-parity
```

Until files move, the existing `cd src-tauri && cargo test` / `npm run check:rust` remain the gate. This document is not a retirement of Svelte/Tauri.

---

## Out of scope

- No file moves, no new crates, no `Host` trait implementation in this step.
- No command rename, no event rename, no schema change.
- Updater minisign client waits for architecture PR 19.
