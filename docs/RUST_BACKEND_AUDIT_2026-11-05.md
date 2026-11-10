# SumaFile Rust Backend Audit — 2026-11-05

Scope: the three Rust crates (`crates/simplefile-core` ~14k lines,
`crates/simplefile-service` ~22k lines, `crates/simplefile-ipc` ~0.7k lines) on
`main` at commit `61b493a`. The WinUI host was read only where a backend
behaviour depends on how it is called.

Method: every non-generated `.rs` file was read in full, split across seven
reviewers by subsystem (IPC/session/dispatch, transfer execution, transfer
planning/tuning/journal, remote FTP/SFTP, core filesystem, metadata/preview/
archive, git/settings/updater/misc). Each reviewer verified claims against the
code and against the locked dependency sources where a library's behaviour
mattered (tokio, russh, tar, zip, image, ureq, suppaftp). The highest-impact
findings were then re-verified independently, and the most consequential one
(C1) was reproduced end to end through `serve_connection`.

| Check | Result |
| --- | --- |
| `cargo clippy --workspace --all-targets` | 0 warnings, 0 errors |
| `cargo test --workspace` | 313 passed, 0 failed (core 146 + 5, ipc 2 + 6, service 147 + 1 + 3 + 3) |
| C1 reproduction (temporary test through the real session loop, since reverted) | Panic at `session/io.rs:53` "Cannot block the current thread from within a runtime"; no response to the `duplicate_check` request within 5 s |

Severity scale: **Critical** = data loss / crash / security / feature cannot
work; **High** = clearly wrong or badly slow for common use; **Medium** = wrong
in a realistic case or a real performance cost; **Low** = hygiene, minor perf,
edge cases. Confidence is "confirmed" (read in the code, or reproduced) unless
marked (P) = plausible, mechanism confirmed but the trigger not exercised.

Line numbers refer to the files at commit `61b493a`.

---

## Status

**Round 1 (2026-11-05, branch `claude/rust-backend-criticals`):** all six
Critical findings fixed, each guarded by a test that failed before the change:

| Finding | Change | Test |
| --- | --- | --- |
| C1 | Queued progress for duplicate/cleanup scans goes through an async emit (`EventSink::emit_progress_async`) instead of a blocking channel send on a runtime worker. | `session::tests::{duplicate_check,disk_cleanup}_responds_through_session_loop` |
| C2 | `header_value` compares the header prefix on bytes instead of slicing at a byte offset. | `metadata::tests::header_value_tolerates_multibyte_prefix_boundary`, `email_metadata_survives_multibyte_header_lines` |
| C3 | The elevated terminal command is built in two steps so each PowerShell quoting level gets its own escaping. | `terminal::tests::admin_command_escapes_quotes_for_both_nesting_levels` |
| C4 | `resolve_file_conflict(Replace)` refuses a destination that is a real directory with the same type-conflict error the other sites use. | `progress::tests::replace_refuses_to_put_a_file_over_an_existing_directory` |
| C5 | Every move root gets a `DiskManifest`, so files staged under new subfolders of a merged root are deleted after publication; a root whose source deletion fails is reported once, as failed. | `progress::tests::move_merge_removes_sources_under_new_subfolders` |
| C6 | A malformed inbound frame gets a JSON-RPC `-32700` reply (`ERR_PARSE`, added to the schema) and the session continues. | `session::tests::malformed_json_frame_gets_parse_error_and_session_continues` |

**Round 2 (2026-11-05, same branch):** the High findings, each with a test
that failed before the change:

| Finding | Change | Test |
| --- | --- | --- |
| H1 | New `Dispatch::Blocking` runs handler bodies on the `BlockingScheduler` (general or transfer lane) and replies when done. Converted: remote test/connect/disconnect/list/create/rename/delete/download/upload, check_for_update, list_drives, delete/trash/restore/empty recycle bin, copy/move (+resolved), all git methods, list/extract/create archive, preview, checksum, image/file metadata, compare. `RemoteSessionRegistry` is now cloneable with interior locking so connects run off the loop. | `session::tests::slow_remote_test_profile_does_not_block_other_requests` |
| H2 | `run_foreground` counts itself only once it holds a permit; background waits are capped (500 ms queued, 250 ms per yield). | `scheduler::starvation_tests::background_work_starts_while_a_long_foreground_job_is_running` |
| H3 | The SFTP provider's runtime is a one-worker multi-thread runtime, so russh keepalives and the inactivity timer run between RPCs. | `remote::sftp::runtime_tests::spawned_tasks_progress_while_no_call_is_blocking` |
| H4 | Git commands run with a 60 s timeout (10 min for fetch/pull/push) and are killed on expiry; read-only queries pass `-c core.fsmonitor=false -c core.hooksPath=<missing>`, mutating commands disable fsmonitor only. The 7-spawn status query and per-navigation caching are still open. Phase 4 moved Git into the `com.sumafile.git` plugin: the evidence is now in `sumafile-git-backend` (`plugins/git/backend`), where each git process also runs in its own kill-on-close job, so a timeout or cancel ends its children too. | `sumafile-git-backend`: `run::tests::{hung_git_process_is_killed_after_the_timeout, spawn_in_job_assigns_the_job_before_the_process_runs}`, `git::tests::{read_only_queries_disable_repo_controlled_executables, mutating_commands_only_disable_fsmonitor, read_only_queries_point_hooks_at_a_missing_folder_inside_the_data_dir}`, `tests/repo.rs::cancelled_fetch_kills_git_and_its_children` |
| H5 | `recycle_bin_roots` uses light drive enumeration (no network probes); `move_to_trash` continues past failed items and reports them all, using `TRASH_UNAVAILABLE` only for trash API failures. | `file_ops::tests::move_to_trash_reports_every_failed_path_instead_of_stopping_at_the_first` |
| H6 | Plain tar listing and tar metadata use `entries_with_seek`, skipping entry contents. The double parse per archive listing is still open. | `archive::list::seek_tests::listing_a_tar_seeks_past_entry_contents` |
| H7 | 7z extraction runs one `7z x` into a staging folder (an include list for selective extracts) and moves validated entries into place. | `archive::tests::seven_zip_extraction_uses_a_bounded_number_of_processes_when_available` |
| H8 | Smart folders: an unreadable store is an error, never an empty list; saves write temp + rename. | `smart_folders::tests::{corrupt_store_is_reported_not_treated_as_empty, save_round_trips_and_leaves_no_temp_file}` |
| H10 | One shared SQLite connection per database path (`MetadataDb` guard); default tags are seeded only when the table is first created. | `settings_store::tests::{default_tags_are_seeded_once_not_resurrected, repeated_opens_reuse_one_connection_per_path}` |
| H11 | `Policy::budgeted_streams` caps multistream stripes by the memory budget and is used for both reservation and copying. | `progress::tuning::budget_tests::*` |
| H12 | A source that cannot be inspected is reported for that item only; a journal record that cannot be recovered is retained and logged instead of failing every transfer into the folder. | `progress::plan::batch_tests::missing_source_is_reported_per_item_not_as_a_batch_failure`, `transfer_journal::tests::recovery_retains_replaced_artifact_without_blocking_the_directory` |
| H13 | `delete_entry` routes to the recycle-bin deleter only for `<drive>:\$Recycle.Bin\S-1-…\$R…` paths. | `file_ops::tests::delete_entry_outside_a_real_recycle_root_never_touches_siblings`, `recycle_bin::tests::recycle_item_path_requires_bin_sid_and_data_prefix` |
| H14 | Updater HTTP calls have connect/response/idle timeouts; downloads stop at the declared size, are no longer mirrored in memory, and the signature is verified against the file on disk; prerelease/build suffixes never outrank the release. | `updater::tests::{stalled_server_times_out_instead_of_hanging, download_stops_once_the_body_exceeds_the_declared_size, signature_is_verified_against_the_file_on_disk, prerelease_and_build_suffixes_are_not_newer_than_the_release}` |

**Round 3 (2026-11-05, same branch):** Medium findings, each with a test
that failed before the change unless noted:

| Finding | Change | Test |
| --- | --- | --- |
| M1 | Skipped sources count as "explained" when a moved source directory cannot be removed, so a merge-move with Skip succeeds. | `progress::tests::move_merge_with_skip_keeps_skipped_sources_without_failing` |
| M3 | Planning-time skips are returned from `prepare_transfer_inputs_cached` and reported in the outcome's `skipped` list. | `progress::plan::batch_tests::planning_time_skips_are_reported` |
| M4 | `StorageFull` is classified permanent, not transient. | `transfer_reliability::classification_tests::disk_full_is_permanent_not_transient` |
| M5 | A file metadata-preservation failure after a verified copy is logged, matching directories, instead of discarding the file. | no unit test (needs a volume that rejects `SetFileInformationByHandle`) |
| M6 | `promote_leased_with_policy` returns `Failure`, keeping Busy/Retryable kinds for the high-level retry. | covered by existing promotion tests |
| M10 | Cancel tokens for search, duplicate and cleanup scans are registered before the job is spawned; `OperationRegistry::remove_token` removes an id only while it still maps to the finishing job's token; searches without a client id get a generated registry id so disconnect cancels them. | `progress::registry::tests::finishing_an_older_job_keeps_the_newer_token_cancellable` |
| M11 | The parent-liveness monitor opens one `SYNCHRONIZE` handle before waiting for the client and waits on it, so PID reuse and access errors cannot fool it. | `main.rs tests::{exited_parent_is_detected_through_its_handle, live_parent_is_not_reported_exited}` |
| M12 | Post-transfer cache invalidation no longer walks every transferred item. | no test (performance only) |
| M14 | The 500 ms per-path watcher debounce is gone; the coalescer already keeps the latest event per path. | `watcher::live_tests::remove_shortly_after_create_is_not_dropped` |
| M16 | Backslashes are ordinary characters in remote paths. | `remote::path::tests::backslashes_are_ordinary_characters_in_remote_names` |
| M17 | Credential targets must start with `SumaFile.Remote.`. | `tests/remote_profiles.rs::credential_targets_outside_the_app_namespace_are_rejected` |
| M18 | `transfer_single` runs recovery on the destination's folder, so downloading over an existing file works. | `remote::transfer::single_download_tests::single_download_over_an_existing_file_succeeds` |
| M20 | SFTP delete uses `symlink_metadata`, so a link to a directory is removed as a link. | no unit test (needs an SFTP server) |
| M21 | Listing skips lines the FTP parser rejects. Not reproducible: suppaftp 12 parses every line tried, including banners and empty lines. | `remote::ftp::listing_tests::dot_entries_are_filtered_from_listings` |
| M22 | The portable fallback runs only when `FindFirstFileExW` itself fails, never after entries were streamed. | `dir_list::enumeration_tests::a_failure_after_entries_were_streamed_does_not_restart_from_scratch` |
| M23 | Only symlink and mount-point reparse tags count as symlinks; cloud placeholders are not `read_link`'d. | `dir_list::enumeration_tests::directory_junction_is_a_symlink_but_plain_entries_are_not` |
| M24 | Sorting precomputes one key per entry and applies the permutation in place; name filtering uses the SIMD case-insensitive search. | `dir_list::sort_tests::*` |
| M25 | Recording a differing byte is O(1) after the row cap; the LCS table uses `u16` cells. | `compare::binary_scan_tests::recording_past_the_row_cap_is_constant_time` |
| M27 | Zip listing and metadata read raw central-directory entries; extraction refuses password-protected entries before writing anything. | `archive::tests::encrypted_zip_entries_list_but_do_not_extract` |
| M28 | Skipped MP4 atoms no longer consume the probe budget. | `metadata::tests::video_metadata_survives_a_large_mdat_before_moov` |
| M29 | Thumbnail writes use a per-call temp name; eviction is throttled to one pass per minute; eviction and clearing refuse a folder without the cache marker. | `thumbnail_cache::safety_tests::*` (written after the change) |
| M30 | Text previews honour UTF-8/UTF-16 byte-order marks and decode lossily instead of failing. | `preview::tests::{read_file_preview_decodes_utf16_text_with_bom, read_file_preview_is_lossy_for_invalid_utf8}` (written after the change) |
| M31 | Policy adds `pythonw`, `py`, `pyw`, `java`, `javaw`, `conhost`, `wsl` and the `.py*`/`.psm1`/`.psd1` extensions (regenerated into Rust and C#); versioned interpreter names and NTFS stream/trailing-dot spellings are recognised. | `open_with::tests::{denied_executables_cover_windowless_and_versioned_interpreters, denied_target_extensions_see_through_ntfs_spellings}` |
| M32 | Disk cleanup ignores zero-byte files; exclusion matching never truncates the path under test. | `cleanup::tests::{scan_disk_cleanup_ignores_zero_byte_files, exclusions_match_arbitrarily_deep_paths}` |
| M33 | One remote-like file-system vocabulary shared by `drives` and `utils`. | `utils` remote-filesystem test extended with `GoogleDriveFS` |

**Round 4 (2026-11-05, same branch):** the remaining items.

| Finding | Change | Test |
| --- | --- | --- |
| H4 (rest) | One `git status --porcelain=v1 -z --branch` run supplies branch, upstream and ahead/behind; repository discovery uses one `rev-parse --show-toplevel`; a 1.5 s status cache per root serves the host's back-to-back file-status and panel-status calls and is dropped by every mutating command. Seven spawns per navigation became three, shared by both calls. In the phase 4 plugin backend the cache holds up to 16 roots, and a folder below no `.git` marker spawns no git at all. | `sumafile-git-backend`: `status::tests::{branch_header_carries_branch_upstream_and_counts, status_with_branch_separates_header_from_records, status_cache_keeps_two_roots_apart, status_cache_entry_expires_after_the_ttl}`, `tests/repo.rs::{mutating_command_refreshes_the_cached_status, decorations_outside_any_repository_spawn_no_git}` |
| H6 (rest) | Listing routing and cacheability use `archive::is_archive_path` (a path-component check) instead of parsing the archive. | no test (performance only) |
| H9 | `get_all_file_tags` returns every tag per file (`Dictionary<string, Tag[]>` on the host; IPC bindings regenerated; controller, row badge and test doubles updated). The row badge shows the first tag. | `tags::tests::tags_round_trip_through_metadata_db` (extended), WinUI build and tests |
| M7 | A lease whose first record write fails removes its record and staging file; recovery compares directory paths by collision key so spelling differences do not skip records. | `transfer_journal::tests::recovery_ignores_path_spelling_differences` |
| M8 | Each rejected upward probe doubles the next cooldown (capped at 32 samples); an accepted upward probe resets it. | `progress::tuning::backoff_tests::rejected_probes_lengthen_the_cooldown` |
| M13 | Cached directory listings expire after 10 s regardless of the directory stamp. | no test (timing) |
| M26 | Zip entries carry the source file's modification time and extraction restores it, so archive edits keep timestamps. | `archive::tests::zip_round_trip_preserves_file_modification_times` |
| L1 | Frames are limited to 64 KiB until the handshake completes. | `session::tests::oversized_frame_before_handshake_closes_the_session` |
| L3 | A repeated handshake is refused; five bad tokens close the session. | `dispatch::tests::{handshake_cannot_be_repeated_once_it_succeeded, repeated_bad_tokens_close_the_session}` |
| L4 | The unused `shared_memory` module is removed. | compile |
| L5 | Renamed directories add no bytes to progress. | existing progress tests |
| L6 | Fingerprint comparison lowercases only the algorithm prefix; child names in `join_remote_path` cannot contain `..`. | `remote::sftp::fingerprint_tests::*`, `remote::path::tests::child_names_cannot_escape_their_parent` |
| L7 | A type filter excludes directories; date bounds exclude entries without a known time; content search strips wildcards and skips queries with interior wildcards. | `search::filter_tests::*` |
| L9 | Recycle-bin `is_hidden` uses the original file name; restore refuses to replace a file that appeared at the chosen destination. | existing recycle-bin tests |
| L10 | The folder tree shows junctions and directory symlinks. | existing tree tests |
| L11 | Read-only git queries run with `--literal-pathspecs`; reported paths use native separators. | `sumafile-git-backend`: `git::tests::git_file_command_enables_literal_pathspecs`, `status::tests::{status_root_and_paths_use_native_separators, untracked_directory_loses_its_trailing_separator}`, `tests/repo.rs::{file_commands_treat_names_literally, decorations_mark_changed_children_with_native_paths}` |
| L12 | The `7z` PATH probe runs once per process; `.tar.gz` gets tar inspection; size totals use saturating adds. | existing archive tests |

**Not changed, with reasons:** M9 (the same-volume swap would need a journal
record whose staged path lies outside the destination directory, which the
recovery format forbids; a redesign of the journal, not a fix); M15 (the host
reads the final result array, so dropping it would need a protocol version);
M19 (fewer FTP round trips needs MLST-aware planning across the transfer
engine); L2, L8 and the remaining redundancy inventory (cleanups without
behaviour change).

**Round 2 deferrals (H9, the git status spawn count, the archive double parse)
were closed in Round 4.**

The "Suggested order" section at the end proposes how to sequence the rest.

---

## Critical

### C1. `duplicate_check` and `disk_cleanup` panic before doing any work; the client waits forever (reproduced)

`session/jobs.rs:897-914` and `:974-991`. Inside `tokio::spawn(async move { ... })`
the "queued" progress event is emitted with `events.emit_progress(...)`. Both
branches of `emit_progress` (`jobs.rs:119-127`) and `emit` (`jobs.rs:109-116`)
end in `OutboundSink::send_payload_blocking` → `mpsc::Sender::blocking_send`
(`session/io.rs:51-53`), which panics unconditionally when called from a runtime
worker. The panic is swallowed by the detached `JoinHandle`, so no response is
written, the scan never starts, and the cancel token registered at `:900` is
never removed. The WinUI duplicate checker dialog (`DuplicateCheckerDialog.xaml.cs`)
calls this method, so the feature cannot work in the shipped app. No test
exercises either method through `serve_connection` (`dispatch/tests.rs` only
tests `dispatch()`).

Fix: `enqueue_payload(...).await` for emits from async context, or move the
queued emit inside the blocking closure.

### C2. A crafted `.eml` header crashes the whole service

`metadata.rs:1570` (`header_value`): `line[..prefix.len()]` slices a header line
at a byte offset that need not be a UTF-8 char boundary. The lines come from
`from_utf8_lossy`, so multibyte characters are present. A header line beginning
`a€…` tested against `"To:"` (3 bytes) panics inside `dispatch()`, which runs on
the session task with no `catch_unwind`; `main.rs:95` propagates and the process
exits. Selecting such a file with the properties pane open is enough.

Use `line.get(..prefix.len())` or compare on bytes with
`eq_ignore_ascii_case`.

### C3. Command injection into an elevated PowerShell via directory name

`terminal.rs:46-52` (`powershell_encoded_command`): `escaped_path` doubles `'`
once, then is embedded inside an outer single-quoted PowerShell string
(`'Set-Location -LiteralPath ''{escaped_path}'''`), which collapses `''` back to
`'`. A folder named `x';calc;'y` yields, in the elevated shell,
`Set-Location -LiteralPath 'x';calc;'y'` → `calc` runs as administrator after
the UAC prompt the user expected anyway. Any `'` in a folder name also breaks the
admin terminal. The non-admin path (`terminal.rs:7-13`) escapes correctly. Needs
quadrupled quotes, or build the inner command and base64-encode it separately.

### C4. Replace copies a file over a same-named destination *directory* and deletes the directory tree

`progress/execute.rs:883`: `resolve_file_conflict(Replace)` returns
`(dst, true)` with no type check. `copy_file_with_progress_versioned` promotes
with `replace=true` (`:1042`) → `transfer_staging.rs` `promote_attempt_leased` →
`move_existing_to_backup_leased` renames the directory aside → file renamed into
place → `remove_path(backup)` runs `fs::remove_dir_all`
(`transfer_staging.rs:626-646`). The same path exists for symlinks
(`execute.rs:1141`). Every other conflict site refuses this: `plan.rs`
`resolve_destination`, `execute.rs:1201-1207` (`move_by_rename`), the checker in
`parallel.rs` ~420 (dir over file). Scenario: copy folder `A` (containing file
`docs`) onto an existing `A\` where `docs` is a directory, action Replace →
`A\docs\**` is silently deleted.

### C5. Move merged into an existing destination folder leaves the sources of *new* subfolders behind and reports the move as failed (and committed)

`progress/parallel.rs:304` creates a `DiskManifest` only `if is_move && owned`.
A root whose destination already exists is seeded `owned=false`, so its manifest
is `None`. A child directory with no counterpart at the destination is staged
privately and sets `owned_tree = true`; its files get
`delete_source: delete_source && !owned_tree` = false (`:493`); the completion
sink (`:555`) calls `root_manifest_for` (`:709`) which returns `None`, so nothing
is recorded. `progress.rs:612` then calls `delete_verified_source_tree` with an
empty manifest; `transfer_staging.rs:552-555` fails with "manifest is missing
the source root", so the root is pushed to `failed` *and* (`progress.rs:626`,
unconditional) to `transferred`. Result: `D:\Proj\new\*` is copied but never
removed, status "failed", and the UI cannot tell this from a clean commit.

### C6. One malformed JSON frame terminates the service

`session/mod.rs:53-54`: a `serde_json` parse error returns `Err` from
`serve_connection` → `main.rs:6-8` `exit(1)`. JSON-RPC expects a `-32700` reply
and a continuing session. Same-user DACL limits exposure, but a single client
bug takes down every open pane's backend.

---

## High

### H1. Long blocking work runs inline on the single session-loop task

`session/mod.rs:57` calls `dispatch()` synchronously between frame reads, so
every handler that returns `Dispatch::Reply` blocks the pipe for its full
duration: no other request is read, including `cancel_operation` for a running
transfer. Handlers in `dispatch/handlers.rs` that do unbounded work inline:

| Method(s) | Lines | Work |
| --- | --- | --- |
| `remote_connect`, `remote_test_profile` | 184-249 | DNS + TCP connect (15 s timeout) + login |
| `remote_list_directory`, create/rename/delete | 260-297 | network round trips |
| `remote_download_file`, `remote_upload_file` | 298-347 | an entire file transfer |
| `copy_entry`, `move_entry`, `*_resolved` | 492-571 | a full copy via `transfer_single_compat` |
| `move_to_trash`, `empty_recycle_bin` | 434+ | see H5 |
| `git_fetch`, `git_pull`, `git_push`, and all `git_*` | 764-805 | process spawn, no timeout (H4) |
| `list_archive`, `extract_archive`, `create_archive` | 806-851 | full archive IO (H6, H7) |
| `read_file_preview`, `get_file_metadata`, `get_image_metadata` | 852-865 | file parsing |
| `compute_checksum` | 866 | hashes the whole file |
| `check_for_update` | 59 | HTTP with no timeout (H14) |

FTP uses raw blocking `TcpStream` on a tokio worker; SFTP uses `block_in_place`,
which frees the worker but still stalls the reader. `session.rs:710` has a
comment "Never block the IPC reader behind a transfer", which the above
contradicts. Only listing, thumbnails, transfers, search, metrics, update
install and `remote_transfer_entries` go through `BlockingScheduler`.

### H2. `BlockingScheduler` starves background work and can wedge

`scheduler.rs:69-73, 85-113, 134-170`.
- `run_foreground` increments `foreground_in_flight` *before* acquiring a general
  permit and holds it for the job's whole duration. `search_files` is foreground
  (`jobs.rs:826`), so during a multi-minute search every `run_background` job
  (folder metrics, thumbnails, duplicate/cleanup scans) waits at
  `wait_for_foreground_idle`, and running ones sleep-spin in
  `yield_if_foreground_active_blocking` (8 ms polls) while holding their permits.
- No fairness or ageing: holding an arrow key through folders or incremental
  search per keystroke keeps `foreground_in_flight > 0` continuously.
- Wedge: `install_update` (`run_general`, holds one general permit for the whole
  download) + background thumbnail jobs holding the remaining general permits
  and spinning because a `run_foreground` has incremented the counter but is
  blocked in `acquire_general`. Background waits for foreground to reach 0;
  foreground waits for permits background holds. (P for the exact combination;
  each mechanism is confirmed.)
- `run_background` calls `wait_for_foreground_idle` twice (`:110`, `:112`); the
  first is redundant.

### H3. SFTP sessions die after idling for more than 15 s (racy)

`remote/sftp.rs:38-51, 232-237, 523-532`. Each provider owns a
`current_thread` runtime that is only driven inside `block_on`. russh's session
task is configured with `inactivity_timeout = 15 s` (`REMOTE_CONNECT_TIMEOUT`,
`sftp.rs:24`) and `keepalive_interval = 10 s`, but nothing polls it between
RPCs. After the user idles >15 s, the next operation polls with the inactivity
`Sleep` already expired; russh 0.63.3 (`client/mod.rs:1313-1358`) uses an
unbiased `select!`, so roughly half the time the timeout branch wins and the
session is torn down with "connection lost", and a keepalive win does not reset
the timer. Server-side `ClientAlive` probes are also unanswered while idle.
(P for frequency; mechanism confirmed in the russh source.) Fix: run the
runtime on its own thread and use `Handle::block_on`, or drop
`inactivity_timeout` and wrap only the connect in `tokio::time::timeout`.

### H4. Git integration: no timeouts, 14 process spawns per navigation, and repo-controlled config is executed

- `git.rs:443-447, 486-495, 655-664`: `command.output()` with no timeout, inline
  on the session loop (H1). A repository on a disconnected share, or a
  push/fetch that stalls at TCP, freezes every request on the pipe.
- `git.rs:24-73`: `get_git_repository_status` spawns seven processes
  (is-inside-work-tree, show-toplevel, branch, rev-parse HEAD, upstream,
  rev-list, status). `MainWindow.Git.cs:698-704` calls `get_git_file_statuses`
  and then `get_git_repository_status` on every listing while the panel is open:
  ~14 spawns and two full `git status` walks per folder change, no caching.
  `git_discard_paths` (`:124`) and `git_diff_path` (`:211`) re-run the full
  status to classify one path. A single
  `git status --porcelain=v2 --branch -z` gives branch, upstream, ahead/behind
  and changes in one spawn.
- `git.rs:30-57, 310-314`: `git -C <user path>` honours that repo's
  `.git/config` (`core.fsmonitor`, `core.hooksPath`, `core.sshCommand`,
  `credential.helper`). A repository from a zip, USB stick or share (owned by
  the current user, so `safe.directory` does not trigger) runs arbitrary
  commands the moment the pane lists it with git integration on
  (`PaneEnrichmentController.cs:36`). Mitigate with
  `-c core.fsmonitor= -c core.hooksPath=<empty> -c core.sshCommand=…` or
  `GIT_CONFIG_PARAMETERS`.

### H5. Every trash operation enumerates all drives with 3 s network probes, twice

`recycle_bin.rs:276-295` (`recycle_bin_roots`) calls `drives::list_drives()`
(full mode), which spawns a detached probe thread per mapped network drive with
a 3 s timeout (`drives.rs:184-209, 344-372`) and calls `WNetGetConnectionW` for
each, then *skips* network and optical drives anyway. `file_ops.rs:247-270`
(`move_to_trash`) calls `recycle_bin_data_path_set()` before and
`recycle_bin_paths_for_originals()` after, each of which enumerates drives and
reads and parses every `$I` file in every SID folder on every drive. Deleting a
single file with one disconnected mapped drive costs up to ~6 s and two full
recycle-bin scans, inline on the session loop (H1); each hung probe leaks a
thread. `list_drives_light()` already exists.

Also `file_ops.rs:251-260`: `trash::delete` is called per path (a fresh
`IFileOperation` each); the first failure returns `Err` and discards the `$R`
paths of items already trashed, so undo is lost for them, and every error (not
found, access denied, in use) is labelled `TRASH_UNAVAILABLE`, the prefix the
host uses for "no recycle bin on this volume".

### H6. Listing a `.tar` reads the entire file; archives are parsed two or three times per listing

`archive/list.rs:110-111` and `metadata.rs:920-933` use
`tar::Archive::new(file).entries()`; tar 0.4 only seeks past entry data with
`entries_with_seek()`, otherwise it reads every byte. A 20 GB `.tar` is read in
full to list names, inline on the session loop, with no size cap for
`MetadataKind::Archive` (`metadata.rs:81`). Independently, `dir_list.rs:67` then
`:261`, and `:274` then `:186`, call `list_archive_directory` twice per request
(once to decide routing or cacheability, once to list), and
`archive/path.rs:59-76` (`split_archive_path`) stats every path component and
is called 2-3 times per listing and on every file op.

### H7. 7z extraction spawns one `7z e -so` process per entry

`archive/extract.rs:148-187, 478-488`. Each invocation re-opens the archive and,
for solid archives, re-decompresses the solid block from the start: N process
launches and O(N²) decompression. A 10k-file 7z = 10k spawns. `mutate_archive`
on a 7z pays this for a single rename plus a full `7z a` rebuild.

### H8. Smart folders: a corrupt file silently wipes all smart folders

`smart_folders.rs:15` maps any parse error to `Vec::new()`;
`save_smart_folder`/`delete_smart_folder` then rewrite the file from that empty
list. `fs::write` (`:54`) truncates in place with no temp+rename, so a crash
mid-write produces exactly the truncated JSON that triggers the wipe. Also
read-modify-write with no lock (`:21-28`).

### H9. Tags: `get_all_file_tags` keeps one tag per file; tags orphan on rename

`tags.rs:141-171`: the query returns every (path, tag) row but
`map.insert(path, tag)` keeps only the last. A file tagged Work+Important shows
only one badge. `file_tags` is referenced only in `tags.rs` and
`settings_store.rs`: no rename/move/delete path updates it.

### H10. A fresh SQLite connection and full schema init on every settings read

`settings_store.rs:148-164, 197-217`: each `get`/`set` does `create_dir_all`,
`Connection::open`, `PRAGMA`, three `CREATE TABLE IF NOT EXISTS`, and
`SELECT COUNT(*) FROM tags`. Hot callers: `preview.rs:117`
(`thumbnail_cache::is_enabled`) and `thumbnail_cache.rs:83/146/198`
(`cache_root`) → 4-5 opens per thumbnail; `progress/tuning.rs` `Policy` load/
recall/remember per transfer. `seed_default_tags` runs whenever the count is 0,
so a user who deletes all five default tags gets them back on the next settings
read. Keep one `Mutex<Connection>` and initialise the schema once.

### H11. Buffer reservation can structurally exceed the memory budget, so every large file fails

`resources.rs:116-123` returns `RETRYABLE: Transfer buffer budget changed`
whenever `bytes > limit`. The reservation is
`min(buffer, size) × buffer_count + 128K` (`parallel.rs:1062-1075`), and
`buffer_count` returns the multistream stripe count, up to 16
(`tuning.rs:174-189`), while `buffer_bytes` only guarantees that *three* buffers
fit (`tuning.rs:159, 170-172`). With 4 streams and the 16 MiB minimum budget, or
16 streams and any budget under ~64 MiB, each file at or above the multistream
cutoff fails, is deferred, retried `high_level_retries` times, and reported
failed with a misleading message. Nothing validates the combination.

### H12. One stale journal record, or one un-stat-able source, aborts the whole operation

- `transfer_journal.rs:298`: `recover_directory` propagates the first
  `recover()` error with `?`, and planning aborts on it (`plan.rs:233`,
  `progress.rs:267,296`, `parallel.rs:271,385`). A leftover `.state` whose
  staged file was replaced ("Recovery staging identity changed; retained …")
  blocks every subsequent copy or move into that folder until the user deletes a
  hidden file. Docs say conflicts are "retained and reported".
- `plan.rs:240-242, 267`: `validate_path_no_follow` and
  `ensure_dir_not_inside_itself` return `Err` for the whole batch, unlike
  conflicts, which are collected per item (`:255-259`). One of 500 selected
  files deleted between selection and transfer → nothing is copied. This
  contradicts `TRANSPORT_ENGINE_REVIEW.md:189`.

### H13. `delete_entry` recycle-bin heuristic bypasses validation and can delete an unrelated sibling

`file_ops.rs:91-100`: `in_recycle_bin = path.contains("$Recycle.Bin")` and
`recycle_data = second byte of file name == 'r'/'R'`. Any path containing that
substring (`D:\Backup\$Recycle.Bin-old\Archive.zip`) with such a name is routed
to `recycle_bin::delete_recycle_item` (`recycle_bin.rs:74-88`), which skips
`validate_path_no_follow`, uses following `is_dir()`, and also deletes
`paired_info_path` (`:146-155`), i.e. the sibling with the second character
replaced by `I`: deleting `Archive.zip` also deletes `AIchive.zip`. A
non-existent path "deletes" successfully. Require
`<drive>:\$Recycle.Bin\S-1-…\` and a `$R` prefix.

### H14. Updater: no HTTP timeout, no size cap, whole installer held in RAM, verifies the wrong copy

`updater.rs:335-343` and `:345-389`: ureq 3 default config has no timeouts
(verified), so `check_for_update` can freeze the session loop (H1).
`download_file` streams to EOF regardless of `package.size`, appending to
`bytes` as well as the file; the size check happens afterwards (`:192`). An
oversized response consumes unbounded memory; a normal install doubles it. The
signature (sha256 + ed25519, `:209-239`) is checked before launch, which is
correct, but on the in-memory copy, not the on-disk file that is executed.
`:295-320` treats prerelease/build suffixes as numeric components
(`1.1.0-beta.2` → `[1,1,0,2]` > `1.1.0`).

---

## Medium

### Transfer engine

- **M1** `progress.rs:593-597`: `skipped` is not included in `failed_paths`, so a
  merge-move with action Skip reports "Source directory retained" for every
  directory holding a skipped file, and the root lands in `failed`.
- **M2** `progress.rs:608-631`: when source-tree deletion fails the root is
  pushed to both `failed` and `transferred` (see C5).
- **M3** `plan.rs:162/182/184` return `Ok(None)` and
  `prepare_transfer_inputs_cached` (`:262`) `continue`s, so planning-time Skip
  and same-entry Replace vanish from the outcome: copy 3 files with Skip where 2
  exist → `committed=1, skipped=[]`.
- **M4** `transfer_reliability.rs:164` classifies `StorageFull` as transient: a
  full destination gets 10 attempts with 1+2+4+8+16×5 s backoff (~95 s per
  file, concurrently) then 3 high-level passes, and each attempt increments
  `retry_count` (`execute.rs:1003-1012`) which halves concurrency
  (`tuning.rs:441-450`).
- **M5** `execute.rs:323`: any `apply_basic_metadata_to_handle` error (e.g.
  `SetFileInformationByHandle` refused on some SMB/FAT targets) is a Permanent
  failure after all bytes were written; the staged file is discarded.
  `parallel.rs:630` only warns for the same class of error on directories.
- **M6** `transfer_staging.rs:755`: `promote_leased_with_policy` uses
  `retry.failed(error)?` which returns `String`; callers rebuild via
  `Failure::from(String)` → Permanent unless the text carries `CONFLICT:`. A
  sharing violation on promotion is never deferred to the high-level retry and
  the fully copied staging file is aborted.
- **M7** `transfer_journal.rs:65-89, 124, 275-297`: if `persist()` fails after
  the record and staging file are created, both remain forever; recovery
  `continue`s past unparseable records without deleting them. `:286-288`
  compares paths byte-wise, so a crash during a transfer planned as `C:\Dest`
  is never recovered when revisiting `c:\dest` or the UNC spelling.
- **M8** `tuning.rs:494-509`: after a rejected upward probe and the one-time
  lower probe, the controller re-probes 1.5× concurrency every ~40 s forever,
  spending ~25 % of the time at a setting already shown slower.
- **M9** `execute.rs:1245` `swap_source_into_place` is an unjournaled two-rename
  replace; a crash between the two renames leaves the destination missing and a
  `.sumafile-replace-backup` that recovery never restores.

### IPC, session, cancellation

- **M10** Cancellation races: `jobs.rs:818, 900, 977` register the cancel token
  inside the spawned task, so a `cancel_search` arriving right after the start
  (type-ahead) can be dispatched before registration; `CopyWithProgress`
  (`mod.rs:98`) does it right. `registry.rs:12-19` silently overwrites an
  existing id and the earlier job's `remove` deletes the later job's token.
  `async_ops.rs:266-352` share one "latest request" `AtomicBool` for folder
  size/count/metrics, so only the most recent request is cancellable and
  `mod.rs:309-313` never sets them on disconnect. `jobs.rs:817-821`: a search
  without `search_id` is unregistered and uncancellable; since dropping the
  runtime waits for `spawn_blocking`, an orphaned search keeps the process
  alive after disconnect.
- **M11** `main.rs:84-88, 107-129` parent-liveness monitor: started only after
  `connect()`, so a host that dies before connecting leaves the service waiting
  forever; `OpenProcess(PROCESS_SYNCHRONIZE)` succeeds on a zombie or a reused
  PID (orphan never exits) and fails with ACCESS_DENIED for an elevated parent
  (spurious exit). Open the handle once and `WaitForSingleObject`.
- **M12** `jobs.rs:787-796` + `session_cache.rs:628-664, 208-236`: after a
  transfer, two `invalidate_path` calls per item each scan all seven caches and
  re-sort on removal, on the async task. A 100k-file transfer stalls the runtime
  for seconds; the root invalidation at `:787-790` already covers the subtrees.
- **M13** `session_cache.rs:60-76`: `PathStamp` uses the directory's own
  size/mtime. NTFS does not bump a directory's LastWriteTime when a child's
  content changes in place, so a cached listing of a non-watched folder serves
  stale sizes until something is created or deleted there. The stamp is also
  taken after enumeration (`jobs.rs:402-406`), absorbing mid-enumeration
  changes.
- **M14** `watcher.rs:65-79`: the 500 ms per-path debounce drops the *second*
  event. create→remove (or write-temp-then-rename-over) within 500 ms emits only
  the create: a ghost entry and no cache invalidation. The coalescer
  (`:189-209`) already keeps latest-per-path, so the debounce is redundant and
  lossy. `:167-186`: flush only on a 150 ms gap or 512 pending, so a steady
  trickle (a build writing files) defers all notifications.
- **M15** `jobs.rs:827-829` and `:848-866`: every search hit is streamed in
  `search-results-batch` notifications and then the whole set is sent again in
  the final response (`search.rs:301-302, 315-318` clones each hit into both).
  Doubles pipe bandwidth and can trip `RESULT_TOO_LARGE` after all batches were
  delivered. `max_results` is client-controlled and uncapped.

### Remote

- **M16** `remote/path.rs:5, 19`: backslashes in remote names are converted to
  `/`. POSIX names may contain `\`; `report\final.txt` becomes `final.txt` at
  `/dir/final.txt`, so delete/rename/download act on the wrong file.
- **M17** `handlers.rs:116-183` + `profiles.rs:293-296` + `secrets.rs:460-467`:
  the client-chosen `credential_target` is written and deleted verbatim in
  Windows Credential Manager with no `SumaFile.Remote.` prefix enforcement, so
  a pipe client can overwrite or delete `git:https://github.com`. Changing a
  profile's target also orphans the old credential.
- **M18** `transfer.rs:1345-1347`: `remote_download_file` calls
  `recover_directory` with the destination *file* path; `read_dir(file)` on
  Windows returns ERROR_DIRECTORY, so the RPC fails whenever the local target
  exists. No view model calls this RPC today, so it is effectively dead API.
- **M19** `ftp.rs:128-159, 192-202` + `transfer.rs:753-754, 813-827, 894-901,
  970`: FTP `stat` falls back to a full `LIST parent` when MLST is unsupported;
  per uploaded file that is 3-5 directory listings, and 6-9 control round trips
  even with MLST. `transfer.rs:280-303, 862-870, 955-968`: checksum mode
  re-streams every file from the remote to verify it, doubling network time.
- **M20** `sftp.rs:127-138`: `delete_entries` uses `metadata` (follows links), so
  a symlink to a directory is classified as a dir, `remove_dir(symlink)` fails
  with ENOTDIR, and the entry can never be deleted from the UI.
- **M21** `ftp.rs:169-174`: one unparseable LIST line aborts the whole listing
  (servers emitting a `total N` header). (P, server dependent.)

### Core filesystem, metadata, archives

- **M22** `dir_list.rs:404-415`: if the Win32 enumeration fails *after* some
  entries were streamed (`FindNextFileW` error on a network drop or a directory
  deleted mid-listing), the std fallback re-emits every entry: duplicates in
  the client view. Fall back only when `FindFirstFileExW` itself failed.
- **M23** `dir_list.rs:785, 802-808, 814`: every reparse point is reported
  `is_symlink=true` and `read_link`'d. OneDrive Files-On-Demand and Dropbox
  placeholders carry the reparse attribute, so a 10k-file OneDrive folder pays
  10k failing `read_link` syscalls and shows every file as a symlink.
  `WIN32_FIND_DATAW.dwReserved0` already holds the tag; compare against
  `IO_REPARSE_TAG_SYMLINK`/`MOUNT_POINT`. The std fallback (`utils.rs:149`)
  disagrees.
- **M24** `dir_list.rs:578-608`: the `sort_entries` comparator allocates 2-6
  Strings per comparison (`case_fold_for_sort`) and, for descending sorts,
  computes the ordering twice. 100k entries ≈ 1.7M comparisons × allocations
  on every re-sort, per chunk in streaming mode. `entry_matches_options`
  (`:563`) does `to_lowercase()` per entry instead of
  `native_accel::contains_case_insensitive`. The non-options path also clones
  the full listing 3-4 times (`dir_list.rs:102, 266, 328`, `jobs.rs:401`).
- **M25** `compare.rs:397-410`: `record_binary_difference` is called per
  differing byte; once 128 rows exist every call falls through to
  `row_offsets.contains` (128 compares) before the truncation check. Two 2 GB
  files differing from byte 3000 → ~2×10⁹ × 128 compares on a foreground
  request, no cancel, no byte cap. `build_diff_ops` (`:167`) allocates
  `(n+1)(m+1)` `usize` (32 MB for two 2000-line files) where `u16` suffices.
- **M26** `archive/mutate.rs:404-431`: any create/rename/delete inside an
  archive does a full, unlimited extraction to `%TEMP%` then a rebuild; tar.gz
  is streamed once more first (`:444-481`). The rebuild loses every entry's
  mtime (`extract.rs:63-64`, `create.rs:72-73` `SimpleFileOptions::default()`),
  compression method, permissions and comments, and persists `x (1)` renames of
  duplicate entries.
- **M27** Encrypted zip entries: zip 9 `by_index` returns `PASSWORD_REQUIRED`
  without a password. `list.rs:36` `?` → a zip with one encrypted entry cannot
  be listed; `extract.rs:43` fails mid-way leaving a partial extraction;
  `metadata.rs:884` silently drops them so counts are wrong. Use
  `by_index_raw`.
- **M28** `metadata.rs:614-620`: a skipped MP4 atom's full `content_size` is
  charged against `bytes_read_limit` even though it is `seek`ed past. Phone and
  camera recordings put `mdat` before `moov`; any `mdat` ≥ 12 MB zeroes the
  budget and the result is "No container metadata found".
- **M29** Thumbnails: `preview.rs:142-145` spawns an `evict_default` thread per
  thumbnail, and `thumbnail_cache.rs:145-154` first walks the whole cache root
  (50-100k files for a 500 MB cache); a folder of 200 images launches 200
  walker threads. `thumbnail_cache.rs:106-116` uses a deterministic
  `<key>.tmp`, so two generators for the same file (two panes, or single +
  batch) truncate each other and rename a half-written JPEG into place; `get`
  (`:94`) returns it unvalidated. `:54-59, 179-200`: a user-configured cache
  root is treated as disposable (`evict` deletes the oldest arbitrary files,
  `clear()` is `remove_dir_all`). `:63-79`: key uses mtime in seconds and the
  raw request path string.
- **M30** `preview.rs:67`: `fs::read_to_string` hard-fails on UTF-16 (`.txt`,
  `.csv`, `.reg`, `.log` with BOM) and Latin-1 `.ini`, while the oversize path
  (`:55-61`) is lossy; no BOM stripping, and `:57-60` uses a single `read()`
  that need not fill the buffer. `preview.rs:127`: `image::open` with default
  limits (512 MiB) and no file-size guard.
- **M31** `open_with.rs:15-18` + `open_with_policy_generated.rs`: `pythonw` is
  not in the denied executables (only `python`, `python3`), `.py` is not a
  denied extension, and `%LOCALAPPDATA%\Programs` (where python.org installs,
  and user-writable) is a trusted root, so the allowlist's stated purpose is
  defeated. Stem matching also misses `python3.12`, `javaw`, `conhost`.
  `:20-24`: extension deny via `Path::extension` is defeated by `::$DATA` or a
  trailing dot.
- **M32** `cleanup.rs:338, 369`: `scan_disk_cleanup` buckets every file by size
  with no minimum and no partial hash, then fully SHA-256s every member of every
  same-size bucket, so 200k zero-byte files are all hashed and reported as one
  duplicate group. `:446-454`: `normalize_exclude_pattern` truncates the path
  being tested to 256 chars, so slash-containing exclude patterns stop matching
  below deep paths.
- **M33** `utils.rs:294-299` vs `drives.rs:250-263`: two different token lists
  for "remote-like filesystem" (`network`, `remote`, `googledrive` only in
  drives). A GoogleDriveFS volume is deferred in the drive list but
  `is_network_path` says local, so it is cached in the listing cache and
  `is_local_fixed_volume` (`execute.rs:234`) enables multi-stream copy on it.

---

## Low

- **L1** `io.rs:388` allocates `vec![0; length]` up to 80 MiB from the length
  header before the handshake; `io.rs:469-488` re-parses an oversize JSON just
  to extract `id`.
- **L2** Blocking syscalls on async context: `jobs.rs:242, 287, 295, 337, 381,
  520` (`symlink_metadata`); `mod.rs:157` `watch_directory` spawns a thread and
  sets up `ReadDirectoryChangesW`, and `unwatch_directory` joins the coalescer
  thread (up to ~150 ms) on the runtime.
- **L3** `handlers.rs:1119-1165`: handshake can be re-sent after success, a
  failed re-handshake leaves `handshake_done = true`, and no disconnect on token
  mismatch. `main.rs:40-69`: DACL applied after `CreateNamedPipeW` (brief
  default-DACL window). `params.rs:416-419`: `request.params.clone()` deep-clones
  possibly large params on every parse.
- **L4** `shared_memory.rs` is referenced by nothing in the repo (dead module
  with unsafe code); its `INVALID_HANDLE_VALUE` check is wrong for
  `CreateFileMappingW`, which returns NULL on failure.
- **L5** `progress.rs:495`: `completed_bytes += 1` per renamed directory while
  directories contribute 0 to the estimate (current > total mid-run).
  `execute.rs:1124-1125`: staged symlink created before the lease is taken.
  `plan.rs:76-78`: `same_volume` compares the path prefix, so `\\?\C:\` vs
  `C:\` triggers copy+delete instead of rename. `execute.rs:234-235`:
  `is_local_fixed_volume` per file, uncached, unlike `VolumeKindCache`.
  `progress.rs:636-638`: learned tuning discarded if any file failed.
  `progress.rs:267, 289-297`: destination root scanned twice.
- **L6** `path.rs:173-185` (`join_remote_path`) does not collapse `..` or inner
  `/` in `child`; `root_path` is metadata only, never enforced.
  `sftp.rs:534-536` lowercases a base64 fingerprint before comparing (base64 is
  case-sensitive). `ftp.rs:60, 77`: PASV connects to whatever IP the `227` reply
  advertises (FTP bounce). `transfer.rs:1237-1241`: canonicalised `\\?\`
  destination propagates into results and journal while cache invalidation uses
  the raw path. `RemoteSecret` and the secret params derive `Debug`.
- **L7** `search.rs:88-98`: type filter applies only to files, so a filtered
  search still returns every name-matching directory; `:133-147`: content
  search with a glob query uses the raw `*foo*` text as a literal substring;
  `:112-128`: entries with unreadable mtime bypass date filters.
- **L8** `shell.rs:407-437`: for archive entries, `MaterializedSource` is
  dropped (cleanup) right after `opener::open`/`explorer.exe` *spawn* returns,
  racing the launched app against temp deletion. (P)
- **L9** Recycle bin: `recycle_bin.rs:355` `is_hidden` tests the full original
  path (always false); `:215-238` `unique_destination` is `exists()` then
  `fs::rename`, which replaces on Windows (TOCTOU on restore); `:125-141`
  `recycle_bin_paths_for_originals_from_entries` is O(originals × bin entries)
  with two lowercase allocations per pair.
- **L10** `file_ops.rs:454-478` `list_subdirectories` opens every child
  directory for `has_children` (N+1, a round trip each on SMB), omits
  junction/symlink directories that the listing shows as `is_dir=true`, and
  filters hidden by dot-prefix only. `file_ops.rs:504` batch-rename temp names
  are not covered by `dir_list::is_internal_transfer_artifact`.
  `folder_metrics.rs:129-131`: root-index lookup is linear over root
  subdirectories per visited directory. `shortcut.rs:28-29`: exists-then-save
  TOCTOU. `utils.rs:318-323`: `validate_existing_path_no_resolve` follows
  symlinks, so listing a dangling directory symlink errors while deleting it
  succeeds.
- **L11** Git: `git.rs:222-238` `diff` lacks `--literal-pathspecs` while add/
  restore/clean have it (`a[1].txt`); `:588-602` the fallback branch of
  `repo_relative_path` returns a lowercased pathspec; `:121-144, 160-171`
  directory pathspecs in discard do not match `status.changes`, so untracked
  files under a "discarded" directory remain; `:396` mixes separators
  (`C:/repo\src/x`).
- **L12** Archives: `seven_zip.rs:32-42` re-resolves the 7z binary by executing
  `7z`/`7za` from PATH on every op and every `get_archive_capabilities`;
  `list.rs:60-61, 95, 123, 169, 204` unchecked `total_size +=` on
  attacker-controlled sizes; RAR path does not filter link entries (`extract.rs`
  tar path does, `:92-99`); `metadata.rs:816-821` inspects `.tgz` but not
  `.tar.gz`.

---

## Performance hotspots (summary)

| Hot path | Cost today | Where |
| --- | --- | --- |
| Folder navigation with Git panel open | ~14 `git` spawns, 2 full `git status` | H4 |
| Delete to recycle bin | 2 full drive enumerations (3 s probes) + 2 full bin scans, inline | H5 |
| Listing an archive | archive parsed 2-3×; `.tar` read in full | H6 |
| Any 7z extract | one process per entry | H7 |
| Thumbnail | 4-5 SQLite opens + full cache-dir walk on its own thread | H10, M29 |
| Large-folder sort with options | allocating comparator, 3-4 full clones | M24 |
| Post-transfer cache invalidation | O(items × cache) on the runtime | M12 |
| OneDrive folder listing | one failing `read_link` per placeholder | M23 |
| Binary compare after 128 diff rows | 128 compares per differing byte | M25 |
| FTP many small files | 3-9 control round trips per file | M19 |
| Background work during search | fully starved | H2 |

---

## Redundancies and dead code

**Duplicated logic (with drift)**
- Conflict-type checking is implemented three times (`plan.rs`
  `resolve_destination`, `parallel.rs` checker, `execute.rs` `move_by_rename`)
  and not at all on the copy path (C4).
- Backup-and-swap replace: `execute.rs` `swap_source_into_place` (unjournaled)
  vs `transfer_staging.rs` `promote_attempt_leased` + `move_existing_to_backup_leased`
  (journaled); the backup-name loop exists twice (`transfer_staging.rs:214-235`
  and `:830-851`). `verify_source_unchanged` (`:261-304`) duplicates the
  lstat→open→fstat→id→hash block of `delete_verified_source` (`:463-523`).
- Retry-with-limiter loop appears three times (`execute.rs` ~970-1010,
  ~1215-1240, `transfer_staging.rs` `promote_leased_with_policy`), and the
  wait→reconnect→retry block twice in `remote/transfer.rs` (`331-348`,
  `521-569`); `abort_remote_upload_finalization` is called 8 times with the same
  7 arguments.
- Progress throttles stacked four deep: 80 ms (`reporting.rs:9`), 50 ms worker
  closure (`parallel.rs:1033-1036`), 200 ms coordinator (`parallel.rs:1341`),
  200 ms `Session::display` (`adaptive.rs:122`); the final `display` is emitted
  twice (`parallel.rs:1363`, `progress.rs:635`).
- "Which volume is this path on" exists four times: `plan::volume_key`,
  `plan::same_volume`, `adaptive::endpoint` (re-querying
  `GetVolumeInformationW` that `VolumeKindCache` already probed), and the
  drive-letter parsing in `utils::is_network_path`/`is_local_fixed_volume`/
  `drives::normalize_drive_root`. `utils::is_remote_like_file_system` and
  `drives::is_remote_like_file_system` have diverging token lists (M33).
- `Policy::normalized()` clamps every field and every accessor re-clamps the
  same field; `max_buffer_count()` is a constant 3 behind a method;
  `buffer_count`'s `_operation` parameter is unused.
- `dir_list.rs` has three near-identical pipelines (`list_directory`,
  `list_directory_with_options`, `list_directory_capturing_raw`) plus a thin
  `list_directory_streaming_options`; `if !options.final_entries { entries.clear(); }`
  at `:238-240` and `:341-343` is unreachable. `file_entry_from_find_data`
  duplicates `utils::build_file_entry`.
- `jobs.rs`: `spawn_folder_size` / `spawn_folder_item_count` /
  `spawn_folder_metrics` (`:225-365`) are three near-identical bodies;
  `spawn_duplicate_check` / `spawn_disk_cleanup` are structurally identical.
  `handlers.rs` has ~60 hand-expanded `match parse_params { … }` blocks that
  `reply_result` (`:1079`) already abstracts.
- `io.rs`: `validate_payload_length` runs twice per frame (`:533`, `:549`);
  the 80 MiB check is implemented in `write_binary_response`, `queue_payload`,
  `validate_payload_length` and `simplefile_ipc::frame::encode_frame`
  (test-only). `queued_bytes` (`:428`) is recomputed by summing all frames per
  iteration.
- Path normalise-and-prefix-compare: `git.rs:605-619` vs `open_with.rs:30-41`
  with opposite slash direction. `archive/path.rs:280-304` `unique_sibling_path`
  ≈ `archive/mutate.rs:646-668` `unique_destination_path`;
  `remote/transfer.rs:701-724` `unique_remote_keep_both` ≈ `plan.rs:98`
  `unique_destination_path`. `transfer.rs:73-79` `check_cancel` ≈
  `reporting.rs:11` `check_cancelled`. `thumbnail_cache.rs:203-214` private
  `hex::encode` ≈ `utils.rs:337` `hex_encode`. `recycle_bin.rs:390-392`
  `utf16_lossy_nul` is an alias of `utf16_lossy`. `file_ops.rs:507-509`
  `rename_no_replace` wraps `rename_exclusive`.
- `archive/list.rs:79-136`: gz and non-gz tar loop bodies duplicated verbatim.
- Five independently maintained extension tables already drifting:
  `metadata.rs:114-150`, `preview.rs:184-333`, `preview.rs:95-98`,
  `archive/path.rs:34-51`, `metadata.rs:1340-1360` (metadata lists heic/avif/jxl
  as images the `image` crate cannot decode; preview treats `gz`/`tgz` alike
  while metadata does not).
- `get_git_status` + `GitStatus` is a strict subset of `GitRepositoryStatus`
  (two RPCs, one data source). `git_output` → `git_output_os` →
  `command_output` → `command_output_with_name` is a three-wrapper chain.
  `APP_IDENTIFIER`/`PRODUCT_NAME` constants duplicated between
  `settings_store.rs:5-7` and `updater.rs:12-13`. `terminal.rs` has the `'`
  escaping inline twice (one of them is C3).

**Dead or unreachable**
- `SessionState.duplicate_check_cancel` and `disk_cleanup_cancel` are never
  read or written; `folder_item_count_cancel` and `count_items_cancel` always
  hold the same `Arc`, served by four cancel handlers (`async_ops.rs:319-352`).
  `session/mod.rs:291` `state.shutdown = true` is unobservable.
- `shared_memory.rs` (whole module, L4). `binary.rs:14-23` `as_u64` branch
  unreachable. `main.rs:116` checks null twice. `handlers.rs:41-43` method gate
  redundant with the `_ => method_not_found` arm.
- `preview.rs:151-182` `generate_thumbnails` is unused (`jobs.rs:616-648` loops
  `generate_thumbnail`) and contains an `h.join().unwrap()` that re-raises a
  worker panic. `thumbnail_cache.rs:120-122` `total_size`, `:197-200` `clear`
  have no callers.
- `ftp.rs:19-24` and `sftp.rs:27-32` `connect` (String-error variants) unused;
  `provider.rs:86-99` `UnsupportedRemoteProviderFactory` and
  `session.rs:508-521` `valid_profile` have no callers; `remote_download_file`,
  `remote_upload_file`, `transfer_single` (`transfer.rs:1330-1371`) have no UI
  caller; `transfer.rs:677-682` `remote_entry` is a passthrough;
  `transfer.rs:1048/1063` stat the same source twice and `:1357-1358` load
  `Policy` twice; `transfer_entries` (`:1207-1214`) is test-only.
- `execute.rs:169` prefix-verification path is unreachable in production (first
  attempt always has the lease's exclusive handle; retries pass
  `trusted_prefix=true`). `progress.rs:583` `scan_error` reporting is
  effectively dead (checkers only send the cancellation message, which is
  filtered). `parallel.rs:981` always spawns 32 worker threads regardless of
  `controller.concurrency`; `parallel.rs:258/259` vs `:868` clamp the same
  policy values to different ranges. `transfer_staging.rs:202`
  `capture_source_version_for_path_hashed` is `#[allow(dead_code)]`;
  `Record.source` is written to the journal but never read; `Retry::default()`
  is never used outside tests.
- `archive/path.rs:68` `found_format = Some(format)` no-op; `metadata.rs:627`
  `let _ = parent;` (unused parameter through the recursion); `:1090-1095` empty
  `"docx"` arm; `ensure_extract_path_within_destination` called twice on the
  same path at `extract.rs:333/340, 396/414, 480/487, 547/567`.
- All `#[cfg(not(windows))]` branches in core (`utils`, `file_ops`,
  `metadata_preserve`, `shortcut`, `path_conflict`, `settings_store:113-124`,
  `updater:400-403`) are dead: `utils.rs:9` unconditionally imports
  `std::os::windows::process::CommandExt`, so the crate only compiles on
  Windows.
- `models.rs` `SearchOptions.search_id`, legacy `CleanupResult`/`DuplicateGroup`
  are used only by the older `scan_disk_cleanup` path. Several `#[cfg(test)]`
  shims in `progress.rs`/`execute.rs` (`copy_files_using`, `copy_tree`,
  `copy_file_attempt`, `promote_staged_path`, …) exist only to feed tests.

---

## Checked and found sound

- Handshake gating: every non-handshake method is rejected before parsing
  (`handlers.rs:33-39`); no bypass found.
- SSH host key is pinned to `trusted_host_fingerprint` and unknown or
  mismatched keys are rejected (`sftp.rs:466-487`); no blind accept, no
  auto-TOFU. FTPS uses native-tls defaults with hostname verification; plain
  FTP requires explicit acknowledgement. Secrets do not appear in errors or
  logs; `CredWrite`/`CredRead`/`CredDelete` usage is correct.
- Remote download names are validated (`..`, `\`, `/`, reserved names,
  control characters) before joining to the local destination
  (`transfer.rs:655-675`); streaming is chunked with per-chunk cancellation.
- Zip-slip: `archive_entry_relative_path_from_name` rejects `..`, leading
  slashes, `:` (drive letters and alternate data streams), NULs and reserved
  device names, plus a canonical destination prefix check; tar link entries are
  skipped. Zip decompression is bounded to the declared size; lopdf page-tree
  iteration has depth limits; lofty and kamadak-exif have allocation caps.
- Transfer planning does not walk recursively (`plan.rs:239-298` stats top-level
  items only); the single traversal is the checker pool. Volume detection is
  cached per volume. Journal appends are fsynced before each replacement rename
  and torn tails are tolerated (`transfer_journal.rs:134-150, 273-284`); crash
  windows around rename/backup are handled by `recover` (`:210-245`). Registry
  tokens are removed in every spawn path. Final completion/error events bypass
  the running throttle. Preallocation sets AllocationSize, not EOF, so resume
  offsets are correct. Worker/coordinator channels cannot deadlock (concurrency
  is clamped to 32 and the channel holds 32 + workers).
- `SharedRemoteProvider::with` uses `try_lock` and reports "busy", so a running
  remote transfer cannot deadlock the session loop.
- `apply_creator_only_dacl` unsafe block: buffer sizing, SID string length loop
  and `LocalFree` calls are correct. `binary.rs` has no unsafe and checks every
  length with `u32::try_from`. `checksum.rs` streams with a 64 KB buffer and
  three hashers in one pass. Updater signature ordering (sha256 + ed25519 before
  launch) is correct.

---

## Suggested order

1. **C1** (one-line fix, restores a shipped feature) and **C6** (reply with
   `-32700` instead of exiting). Add a `serve_connection`-level test for
   `duplicate_check` and `disk_cleanup`.
2. **C2**, **C3**, **H13**: three small, contained fixes for a crash, an
   injection and an unsafe delete heuristic.
3. **C4**, **C5**, **M1-M3**: the copy-path type check and the merge-move
   manifest. These are the data-integrity items in the transfer engine and
   should land together with tests for file-over-directory Replace and
   merge-move with new subfolders.
4. **H1**: move the inline blocking handlers behind `BlockingScheduler`. This
   is the single largest UX fix and unblocks H3/H4/H14 timeouts being
   meaningful. Then **H2** (increment `foreground_in_flight` after the permit,
   release permits while yielding, or give background a guaranteed lane).
5. **H5**, **H10**, **H6**, **M29**: the per-operation enumeration/open/walk
   costs on trash, settings, archives and thumbnails.
6. **H3**, **H4**, **H14**, **M17**: remote/git/updater robustness.
7. **H8**, **H9**, **H11**, **H12**: smart folders, tags, and the two
   transfer-planning abort conditions.
8. The Medium list, then the redundancy consolidation (conflict checks,
   backup-swap, volume identity, extension tables) once behaviour is pinned by
   tests.
