# Roadmap

Open work for SumaFile, in one place. An item leaves this page when it ships;
what shipped is in the [changelog](CHANGELOG.md). Nothing here is a promise or
a schedule.

## Direction

- Keep local file navigation fast and reliable, including in very large
  folders.
- Preserve Windows drive labels, mapped network share names and native drive
  types.
- Keep folder openings inside SumaFile unless the user opens a file
  externally.
- Keep transfers safe first: no half-written file under its final name, no
  source deleted before its copy is verified, and an honest report of what
  happened.
- Keep signed updates, verified downloads and the Windows installers reliable.
- Grow by plugins where a feature is not for everyone.

Out of scope:

- Cloud-provider integrations and provider-backed mounts built into the app.
  FTP, FTPS and SFTP are served by the bundled remote plugin.
- macOS or Linux builds and installers.

## Performance

The session cache, service-side sorting and filtering, paged listings, row
deltas, the byte-budgeted thumbnail cache, the foreground-first scheduler and
on-demand column values have shipped. What remains:

- **A data-virtualized file list.** The app still reads every row of a folder,
  in pages behind the first one. Leaving rows unread until they scroll into
  view needs a data-virtualized list in the WinUI app.
- **Measurements.** Record cold and warm timings for 10,000 and 100,000 file
  folders on SSD, HDD, a network share and removable media: first visible row,
  complete listing, return to a recent folder, first thumbnail, peak memory,
  frame drops while scrolling. Startup stages and the UI smoke are measured
  today; nothing else is.
- **A benchmark suite in CI** on fixed fixtures, tracking p50, p95 and p99, so
  a performance claim comes with a before and after.
- **Cache budgets and controls.** One session budget chosen from physical
  memory, a setting (Off, sizes, Auto), a "Clear session cache" control, and
  trimming under Windows low-memory notifications: thumbnails first, then
  previews and search results, then old listings.
- **A diagnostics view** of each cache: bytes, entries, hit rate, evictions,
  active work.
- **Search-result caching** for a query revisited in the same session.
- **Limited prefetch**: a small scroll-ahead window and the other pane's
  visible range, cancelled as soon as the user changes direction; none on
  network or removable locations.
- **Allocation and rendering hygiene.** Profile allocations and frame times
  while scrolling, sorting and receiving updates; batch collection changes;
  format lazily for visible rows only.
- **Slow locations.** Shorter time budgets and lower concurrency for network,
  offline and failing sources, and brief caching of timeouts with backoff.
  Network drive probes already time out.

## Listing, columns and views

- A sort by an on-demand column reads at most 5,000 rows; larger folders sort
  the rest as unknown. Raise the cap or read in the background.
- Nothing is read automatically for on-demand columns on a network path.
  Consider a slower automatic mode there.
- The column presets have different names in Settings (Basic, Full details)
  and in the column picker (Default, Details). Use one set.
- Every folder shows "…" in Size by default because folder size is read on
  request. Decide whether an unread folder size should show nothing instead.

## Transfers

- Benchmark copy and move against Windows Explorer on SSD, HDD, USB, a network
  share and many small files, and on a physical NAS.
- The options dialog says "Write directly to the destination" leaves partial
  files behind after a failure. [Transfers](TRANSFER_TUNING.md) records what
  the engine does today; make the text and the behaviour agree.
- Partial files left by copies that failed before 1.1.4 under a path with a
  doubled separator are not matched by recovery and must be deleted by hand.
  Teach recovery to match a record whose path differs only in repeated
  separators.
- The same-volume replace swap is two renames without an ownership record. A
  crash between them leaves the destination missing and a replace backup that
  recovery does not restore. Fixing it needs a change to the record format.
- Whether two paths are on the same volume is decided from the first path
  component, which is wrong for mount points and substituted drives.
- Learned transfer settings are not saved when any file in the task failed.
- The file service installs no logger, so warnings such as retried transfer
  attempts and unrecoverable ownership records are never written to
  `service.log`. Only text printed directly to the error stream reaches it.

## Search

- Every search hit is sent in a batch notification and again in the final
  reply. Dropping the duplicate needs a protocol change.
- Each batch of results replaces the whole result list in the pane, which
  resets scroll and selection during a long search. Append instead.
- Windows Search integration is used when a folder is indexed. Say in the UI
  whether results are indexed or live, and how fresh they are.
- Smart-folder values are interpolated into the Windows Search query; only the
  folder scope is escaped. A quote in a value gives an empty result.
- The rename preview runs user regular expressions on the UI thread without a
  timeout.

## App and UI

Open findings from the [UI audit of November 2026](UI_AUDIT_2026-11-05.md),
by its ids:

| Id | Problem |
| --- | --- |
| S5 | Shortcut keys named Plus, Minus, Comma, Period, Slash, Backslash and PrintScreen in Settings do not map to the keys they name. |
| S27 | The command palette has a fixed width that clips in a narrow window, and does not restore focus when it closes. |
| F15 | The Details list handles both the right-tap and the context-request event, so one right-click may build its menu twice. Not confirmed at runtime. |
| F16 | After a context menu closes in the Details list, arrow keys do nothing until a row is clicked. |
| F25 | Pasting one item into its own folder is refused, while two or more are duplicated. |
| F26 | Dragging over a pane makes it the active pane before anything is dropped. |
| T21 | The keep-both preview may probe up to 10,000 names per conflict while the prompt is held. |
| T23 | `MainWindow.Transfer.cs` holds the transfer retry and undo logic that belongs in Core. |
| T24 | `FileOperationDialogService` takes dozens of delegates and builds dialogs three different ways. |
| C11 | Profile export suggests a double extension, and there is no profile import. |
| C24 | History compares paths by exact case, so a path that differs only in case adds a second Back entry. |
| P13 | See Search: rename preview on the UI thread. |
| P15 | See Search: result batches replace the list. |
| P23 | See Search: unescaped values in the Windows Search query. |

Also open:

- Reduce the adapter code duplicated between the window's partial classes when
  a workflow is next changed; move behaviour into Core where possible.
- Review the lifetime of the static `AppServices` when reconnect or session
  ownership changes.
- Interactive actions in archive previews, and visual smoke coverage per
  preview format.
- The archive viewer dialog can no longer be opened: the context-menu id that
  led to it is not produced any more, and no other control reaches it. Restore
  an entry point or remove the dialog.
- There is no C# test of permanent delete, of redoing a copy, or of
  `startup.log`. Three rows of the parity record are marked as passing
  without an automated test.

## Service and storage

Open findings from the
[backend audit of November 2026](RUST_BACKEND_AUDIT_2026-11-05.md), by its ids:

| Id | Problem |
| --- | --- |
| H9 | Labels are keyed by path and are not updated when a file is renamed, moved or deleted. |
| M9 | See Transfers: the unjournaled replace swap. |
| M10 | Folder size and folder count requests share one "latest request" cancel flag each. |
| M15 | See Search: results sent twice. |
| M19 | FTP transfers make more round trips than needed. This now belongs to the remote plugin. |
| L2 | Blocking filesystem calls and the watcher's setup and teardown run on the async runtime. |
| L3 | The pipe's access control list is applied after the pipe is created. |
| L5 | See Transfers: same-volume detection and unsaved learning. |
| L8 | A file opened from inside an archive is extracted to a temporary file that may be removed before the launched program has read it. |

Also open:

- The application data identifier `com.simplefile.desktop` is defined in
  three places in Rust and one in C#. Centralise it only with migration
  coverage; the value itself stays.
- `SharedMemoryReader` remains in the IPC project and is used only by its own
  test.

## Plugins

- Publish `SumaFile.PluginSdk` to NuGet and `sumafile-plugin` to crates.io.
  Both are packed and checked in CI and neither is published.
- Decide whether the `SUMAFILE_PLUGIN_*` environment overrides should be
  honoured in release builds.
- Decide what a plugin signature should mean beyond integrity.
- Decide whether uninstalling removes the remote plugin's saved credentials.
- The two clients of a plugin backend disagree in the handshake: the app
  sends its four-part assembly version as `hostVersion` and the file service
  the display version, and they check the reply's `sdkVersion` by different
  rules. Pick one form and one rule.
- `ipc/schema/plugin/v1/protocol.json` does not list error `-32700`, which the
  helper crate sends, so the documentation test does not pin it.
- The Rust example in the plugin guide and the crate README is compiled by
  nothing.
- The template's staging script hard-codes the project, assembly and backend
  names that a rename has to change; derive them instead.

## Build, release and repository

- The maintenance workflow's `release` recipe cannot publish the change
  `npm run release:prepare` makes: its allowlist omits four files the command
  now edits. Release preparation is done by hand; see
  [the release runbook](../.github/RELEASE.md).
- The `rustfmt` repair recipe's allowlist covers three crates, while the
  formatter and CI cover the whole workspace, so a formatting failure in any
  other crate produces a repair that is refused.
- An in-app update does not check how SumaFile was installed. On an MSI
  install it runs the NSIS setup into the same default folder and leaves two
  registrations with Windows. No smoke runs the in-app update end to end.
- Workflow leftovers: the release build is passed two signing variables of the
  retired application that nothing reads; one release step runs only for
  `v1.0.0`; the upgrade smoke in the release gate skips silently when no
  published installer can be downloaded; the Release build workflow has an
  input with no effect and installs an audit tool it never runs; two step
  names still say "dual-stack"; CI is triggered for a `C#` branch that does
  not exist.
- The UI smoke appends to the user's own `startup-timing.log` and
  `service.log`, because neither path can be redirected.
- `tools/sevenz-evaluation` has its own lock file, which no longer matches the
  workspace crates, and nothing builds it in CI.
- There is no third-party notices file for the Rust crates and NuGet packages
  the installers bundle. Only 7-Zip's licence ships.
- `ipc/schema/v1/events.json` still lists event names of the retired
  application, and an `app.restart-required` notification, that nothing emits.
