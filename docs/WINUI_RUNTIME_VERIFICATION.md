# WinUI runtime verification

`npm run smoke:winui-ui` drives the real Debug application and Rust service
through rendering, layout, transfer and plugin scenarios. Run it from a
Windows desktop session.

The script, `scripts/smoke-winui-ui.ps1`, builds the Debug service, the
bundled plugin backends, the SFTP test server and the WinUI application,
generates folders containing 10,000 and 100,000 mixed text/image files, and
launches the real application.
Use `-SkipBuild` after an existing Debug build, or `-Counts 10000` for the
smaller PR-sized run:

```powershell
npm run smoke:winui-ui
npm run smoke:winui-ui -- -SkipBuild -Counts 10000
npm run smoke:winui-ui -- -SkipBuild -Counts 10000 -MaxWindowWidth 1000
```

Rendering and stale-navigation checks exercise every requested folder size.
Layout, cancellation and bulk-copy correctness use the smallest requested
fixture (10,000 files in the default suite). This keeps the 100,000-entry
rendering gate independent of durable per-file copy/flush throughput. A
deliberate 100,000-file copy stress run can use
`-Counts 100000 -TimeoutSeconds 3600`; its elapsed time is diagnostic, not a
rendering performance measurement.

## Parameters

| Parameter | Effect |
| --- | --- |
| `-Counts <n>` | Sizes of the generated folders, each between 1,000 and 100,000. Default: 10,000 and 100,000 |
| `-SkipBuild` | Use the existing Debug build instead of building first |
| `-TimeoutSeconds <n>` | How long the script waits for the application to finish. Default 600, at most 7,200 |
| `-MaxWindowWidth <n>` | Caps the logical window width the layout scenarios request, from 1,000 to 3,840. Default 3,840 |
| `-PluginsDir <folder>` | Plugins-on mode: load the bundled plugins and, pre-approved, the plugins in `<folder>`. `<folder>` is one plugin folder or a folder of plugin folders |
| `-NoPlugins` | Plugins-off mode: load no plugin. It cannot be combined with `-PluginsDir` |
| `-Baseline` | Record only the large-folder and navigation scenarios, without the virtualization assertion |
| `-KeepResults` | Keep the run folder after a passing run |
| `-PointerInput` | Also make the gestures that have no keyboard or automation route, with the real mouse: a marquee selection and a drag onto a folder's row in a file pane, the mouse's side buttons, middle-clicks, drops onto a tab, a breadcrumb and a bookmark, a tab drag and a column header drag (the `pane-pointer-gestures` scenario, in every mode), and a click, a double-click, a right-click and drags in the FTP/SFTP manager (plugins-on mode). The pointer moves and its buttons are pressed for a few seconds, so leave the mouse alone meanwhile. A step fails, without clicking, if another window covers the point it is about to click. Off by default, and not used in CI |

## Modes

The plugin switches select one of three modes. Every mode runs the common
scenarios: `large-folder` and `navigate-during-loading` for each folder size,
`thumbnails`, `dual-pane-layout`, `single-pane-layout`,
`pane-filter-search-controls`, `preview-collapse-expand-pin`,
`conflict-comparison-dialog`, `rename-before-after-preview`,
`transfer-cancellation`, `adaptive-transfer-ui`, `storage-pressure-recovery`
and `adaptive-bulk-copy`.

| Mode | Switch | Plugins loaded | Additional scenarios | Needs |
| --- | --- | --- | --- | --- |
| Plain | none | The bundled plugins staged with the Debug build | None | Nothing extra |
| Plugins on | `-PluginsDir target\fixture-plugins\com.sumafile.hello` | The bundled plugins and the Hello fixture plugin | `plugins`, `sevenzip`, `sevenzip-backend-loss`, `remote`, `remote-backend-loss`, `git`, `service-reconnect` | The fixture plugin, the bundled 7-Zip, Git for Windows, the SFTP test server |
| Plugins off | `-NoPlugins` | None | `plugins`, `sevenzip`, `remote` and `git`, each asserting that the feature is absent | The bundled 7-Zip, Git for Windows |

What the plugin modes need:

- **The fixture plugin.** `scripts/build-fixture-plugin.ps1` builds the Hello
  plugin into `target\fixture-plugins\com.sumafile.hello`. The `plugins`
  scenario requires it, so `-PluginsDir` must include it.
- **The bundled 7-Zip.** `third_party\7zip\win-x64\7za.exe`, which is in the
  repository. The script packs the `sample.7z` fixture with it.
- **Git for Windows** on the `PATH`. The script creates the repositories of
  the `git` scenario with it.
- **The SFTP test server**, `target\debug\sumafile-remote-test-server.exe`.
  The script's own build and `scripts/build-fixture-plugin.ps1` both build it;
  the script starts it on the loopback interface and stops it afterwards.
- **The bundled plugins**, staged in the Debug build's `plugins` folder. The
  script's own build stages them. Plugins-on mode fails when they are missing,
  and every mode fails when the staged files no longer match the build outputs
  they were copied from, which `-SkipBuild` can leave behind.

What the additional scenarios check:

| Scenario | Plugins on | Plugins off |
| --- | --- | --- |
| `plugins` | The Hello fixture is discovered and enabled, its command is in the command palette and runs, its backend starts and registers the `.hello` archive format, and its sidebar section, window, dialog, settings page, panels and row decorations work, including decorations of the folder shown at startup | No plugin, plugin command, plugin sidebar section or row decoration provider exists |
| `sevenzip` | The bundled 7-Zip plugin is trusted through `bundled.json` and serves `.7z` listing, browsing, folder creation inside the archive, create and extract as Transfers rows, Cancel of a long create, and its executable override setting | `.7z` is unavailable everywhere, with the note that names the plugin |
| `sevenzip-backend-loss` | Killing the 7-Zip backend faults the plugin and shows the note in the pane; a restart brings the listing back | Not run |
| `remote` | The bundled FTP/SFTP plugin against the SFTP test server: a profile imported from a 1.0.4 database, the manager window, a new profile with a saved password, host-key trust, browsing (Enter on a folder, a typed remote path, Backspace), the Size column, a splitter moved with the keyboard, upload, download, Cancel from the Transfers row, folder operations by button, by F2 and Delete and from the context menu, disabling the plugin during a transfer, profile deletion and the settings page | No remote command, toolbar action, menu entry or Settings text exists |
| `remote-backend-loss` | Killing the remote backend faults the plugin and removes its sidebar section; a restart brings both back | Not run |
| `git` | The bundled Git plugin: trust and commands, the legacy setting migration, row decorations, the workbench panel, stage, unstage, diff, discard, fetch, pull, commit and push (each checked with the `git` command line), detaching and disabling, and a removed toolbar toggle staying removed | No Git command, toolbar action, menu entry, Settings text, decoration or backend exists |
| `service-reconnect` | Killing the service reconnects the application; `.7z` is served through the plugin again and the remote and Git backends keep running | Not run |

## Isolation and results

The harness exists only in Debug builds. It starts after the normal backend
handshake and workspace initialization, before deferred startup navigation.
The marker file created by the script must exist before the host accepts the
smoke root. Release binaries do not contain the scenario runner.

Settings, metadata database, operation journal, thumbnail cache, plugin data,
plugin approvals, plugin log, Git configuration, fixtures and report are
isolated under `test-results/ui-smoke/<run>/`, which Git ignores. A run
touches two things outside `test-results/ui-smoke/`:

- The application appends to its diagnostic logs in `%LOCALAPPDATA%\SumaFile`:
  `startup-timing.log`, which `npm run check:winui-startup-budget` reads, and
  `service.log` when the service writes to its error stream.
- In plugins-on mode the `remote` scenario saves one password as a generic
  Windows Credential Manager entry named `SumaFile.Remote.<profile id>` and
  deletes it again. When the application left the entry behind, the script
  removes it. When any other new `SumaFile.Remote.*` entry appeared, the
  script fails the run and deletes nothing.

After the application exits, the script also checks that the run changed
neither the pre-release plugin locations under `%LOCALAPPDATA%\SumaFile` nor
the plugin data, approvals and plugin log in the Debug build folder. The
script stops only the application and the SFTP server it started.

A failed run keeps its `<run>` folder as evidence. A passing run copies its
`report.json` to `test-results/ui-smoke/last-passed-report.json` and its polish
screenshots (`*-polish.png`) to `test-results/ui-smoke/last-passed/`, then
deletes the `<run>` folder; pass `-KeepResults` to keep it. Each run first
deletes the previous run's `last-passed-report.json` and `last-passed/*-polish.png`,
so a failed run never leaves an older pass's files looking like its own. A
folder that cannot be deleted (a helper process still running, a link inside
it, a file that cannot be listed or made writable) is kept with a warning and
does not fail a run that passed. Close the test process before removing a kept
run folder by hand.

## Assertions

- Directory enumeration uses the real named-pipe client and Rust service.
  The first rendered row and final UI collection are observed independently.
- List view realizes fewer than 1,000 containers and fewer than half the
  fixture entries. This catches accidental loss of UI virtualization.
- Scrolling to the start, middle and end preserves visible rows without
  overlaps. Selecting the last row must realize and retain that selection.
- Two pending navigations must leave only the newer folder's rows visible;
  late chunks from the previous folder cannot repaint the pane.
- Both panes are exercised at logical window widths 1,000, 1,280 and 1,920 while
  changing List, Tiles and Details views. Details must stay the effective
  view at every width, and the run must see it both in a pane narrower than
  720 pixels and in a full-width single pane, which cycles through all views
  as well; resizing must never silently substitute List for Details.
  Requested window sizes may be capped by the desktop. `-MaxWindowWidth 1000`
  reproduces that constraint locally without changing the desktop resolution.
  Window sizes account for DPI scaling and the divider is recentered
  at each size (the product normally preserves its stored pixel width).
  Actual pane widths and the effective view are included in the report.
- A real copy runs through the application's transfer queue, progress window,
  service and cancellation route. Cancellation must occur while running,
  reach the cancelled terminal UI state, remove staging paths and leave the
  source files unchanged.
- The transfer options dialog persists its mode, concurrency and bandwidth
  limit. A copy of eight files shows four active file rows, accepts a live
  concurrency change and produces files whose SHA-256 matches the source. A
  destination file held open by another reader makes the transfer show that
  it is retrying, keeps the original file until the reader lets go, and then
  completes without a manual retry. A bulk copy of the smallest fixture folder
  must match its source byte for byte.
- Pane-local filters remain visible across pane/omnibar changes; Clear filter
  affects only its pane. A real search exposes Return to folder and that button
  restores the listing.
- With nothing selected, the unpinned preview stays open and shows its empty
  state. A cramped dual-pane layout collapses it to its rail, the rail reopens
  it, and the pin button keeps it open. The toolbar reserves the omnibar's
  minimum usable width.
- The conflict dialog renders incoming/existing metadata and Keep both names.
  Choosing Keep both is checked against an actual backend copy without
  overwriting the existing file.
- Typing in the omnibar and in a pane's path box brings up suggestions that
  the keyboard works (Down, Tab, Enter, Esc); F2 renames in place in the List
  and Tiles views with the box over the name; and a Details column moves from
  its header's menu without passing Name (the `typing-and-columns` scenario).
- Quick Look shows the item's place beside its name, has the Source | Rendered
  switch for a Markdown file and none for plain text, steps with Right, zooms
  a picture with + and - and fits it again with 0, and closes on Space (the
  `quick-look` scenario).
- A folder that changes from outside while a pane shows it (the
  `live-folder-changes` scenario): a new file is one new row; a selected
  row that is renamed stays selected, in the Details view and in the List
  view; 700 files made at once are all shown and all gone again after they
  are deleted; a pane whose folder is deleted or renamed goes to the folder
  above it; and a folder that is removed and made again stays open, shows
  its new contents and is followed again.
- The conflict dialog's cards show a thumbnail for a picture and none for a
  text file, and "Make the sidebar wider" and "narrower" move the sidebar by
  one step.
- Advanced Rename renders its Before/After columns aligned with their headers,
  disables Rename while the new names conflict, and preserves extensions. The
  run captures `workspace-polish.png`, `conflict-polish.png`, and
  `rename-polish.png` alongside the JSON report. The plugins-on run also
  captures the connected FTP/SFTP manager as `remote-manager-polish.png`.

## Manual folder merge checks

The [1.1.5 verification cases](RELEASE_1.1.5.md#manual-verification) cover an
empty destination folder, nested file conflicts, and Undo and Redo with
destination-only files. Use copy/paste, drag and drop, or the other-pane commands
to exercise the shared transfer path.

## Refresh the README screenshots

Use the `sumafile-winui-runtime` artifact from a successful **WinUI host** job on
a GitHub-hosted Windows runner at the intended application revision. Take
`workspace-polish.png`, `conflict-polish.png` and `rename-polish.png` from its
`last-passed/` folder, which the `-NoPlugins` smoke wrote, so no test plugin
contributes to them; `plugins-passed/` holds the plugin smoke's copies (run with
the bundled plugins and the Hello fixture loaded) next to `plugins-passed-report.json`. A failed run's
screenshots are under its `<run>/` folder instead. The three PNGs
come from the running app with generated fixtures and isolated settings. Review
each image at full size, including its sidebar, breadcrumbs and title. Use only
runner-generated sample data for public screenshots. Record the source run next
to the images in the README.

## Measurements and evidence

Each run writes `report.json` with per-folder entry count, first painted-row
latency, final navigation/render time, realized containers, working set and
managed memory. A 30 ms dispatcher timer records p95 and maximum heartbeat
gaps throughout the run, including layout/view changes and cancellation.
Timing and memory are diagnostic values rather than machine-dependent CI
thresholds; rendering/selection/cancellation/container assertions are gates.
The harness also atomically updates `progress.json` before each major scenario,
including transfer options, concurrency, storage pressure and bulk copying.
If the host exceeds its process timeout, the script prints the last checkpoint;
both CI workflows retain it even when a final report could not be written.

The `thumbnails` scenario opens a folder of 96 camera-like photos (1600x1200
JPEGs; every other one a portrait stored on its side with Exif orientation 6,
two of three with the 160x120 preview cameras embed) in the tiles view with a
fresh thumbnail cache. It reports the time until every visible row has its
thumbnail (`viewportMs`), then loads the remaining thumbnails one at a time
cold (`coldMs`, decoded by the service and written to its disk cache) and all of
them again warm (`warmMs`, served from that cache with the in-app cache
dropped), with per-thumbnail averages. Every photo must get a thumbnail and
every portrait must come out taller than wide; those are gates, the times are
diagnostic.

First-row latency is measured from navigation until layout produces a
container for the first row. Final navigation/render time includes a 200 ms
settling interval. Working set is a process snapshot, not a peak measurement.
Of the managed memory figures, compare `liveManagedBytes` (the heap after a
full collection) and `allocatedBytes` (allocated process-wide from navigating
until the listing settled) between runs. `managedBytes` is the heap at that
moment, garbage included, and moves by megabytes with where the collector is
in its cycle.
Heartbeat gaps measure dispatcher scheduling delays, not display frame times.
Use the same machine, configuration and fixture size for before/after results.

`-Baseline` records the large-folder and navigation scenarios without the
virtualization assertion; it skips every scenario after them and is for
comparing an older implementation, not for acceptance or CI.

Pull-request CI (the **WinUI host** job) compiles the entire application/XAML,
verifies production artifact hashing, and runs the 10,000-entry scenarios
twice: first in plugins-on mode with the Hello fixture, then in plugins-off
mode. Between the two it runs `npm run check:winui-startup-budget`, which
reads the startup timings of the plugins-on run, and copies that run's report
and screenshots to `plugins-passed-report.json` and `plugins-passed/`. The job
uploads the reports and screenshots as the `sumafile-winui-runtime` artifact,
also when a step failed. The installer workflow runs the plain mode with
10,000 and 100,000 entries and uploads its reports alongside installer
evidence. It runs daily, on manual dispatch, and in CI for Dependabot and
maintenance pull requests. Both run on GitHub-hosted `windows-2025` runners.

## Other gates

`npm run check:winui` runs the C# tests in `src-winui/SimpleFile.Tests`. Run
`scripts/build-fixture-plugin.ps1` and `npm run build:plugins` first: the test
project copies the plugins they stage.
`npm run build:winui` compiles the application and XAML.
`npm run check:winui-release-hash` loads only the production hash function from
the packaging script and checks known SHA-256 vectors, concurrent readers,
paths with spaces and missing-artifact failure. It does not build installers.
Run the packaged startup, file-operation, MSI, NSIS and upgrade smokes when
changing what they cover; [the release runbook](../.github/RELEASE.md) lists
them.

## Local measurements, 2026-10-18

Single runs on the same development machine, Debug configuration with mixed
text/image fixtures on `R:`. These are observations, not universal budgets.

| Folder / implementation | First painted row | Final render including settle | Realized rows | Working set snapshot |
| --- | --- | --- | --- | --- |
| 10,000 / previous `StackPanel` | 288 ms | 21,683 ms | 10,000 | 2,632 MB |
| 10,000 / `ItemsStackPanel` | 214 ms | 546 ms | 58 | 286 MB |
| 100,000 / `ItemsStackPanel` | 114 ms | 2,815 ms | 87 | 572 MB |

The combined virtualized run passed both navigation-replacement scenarios,
12 dual-pane/view/width combinations, scrolling and selection, and active
transfer cancellation. Its dispatcher heartbeat p95 gap was 124 ms and maximum
gap was 2,119 ms over the entire run. The maximum still shows that final list
publication can block the dispatcher; virtualization does not eliminate all
large-folder UI work. First-row latency varies with caching and scan timing.
