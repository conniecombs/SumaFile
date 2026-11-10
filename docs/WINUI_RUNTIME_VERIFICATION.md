# WinUI runtime verification

Latest release: **SumaFile 1.0.4**. See [release notes](RELEASE_1.0.4.md) for the complete change set.

Run `npm run smoke:winui-ui` from a Windows desktop session. The script builds
the Debug WinUI application and Rust service, generates folders containing
10,000 and 100,000 mixed text/image files, and launches the real application.
Use `-SkipBuild` after an existing Debug build, or `-Counts 10000` for the
smaller PR-sized run:

```powershell
npm run smoke:winui-ui
npm run smoke:winui-ui -- -SkipBuild -Counts 10000
npm run smoke:winui-ui -- -SkipBuild -Counts 10000 -MaxWindowWidth 1000
```

Rendering and stale-navigation checks exercise every requested folder size.
Cancellation and bulk-copy correctness use the smallest requested fixture
(10,000 files in the default suite). This keeps the 100,000-entry rendering
gate independent of durable per-file copy/flush throughput. A deliberate
100,000-file copy stress run can use `-Counts 100000 -TimeoutSeconds 3600`;
its elapsed time is diagnostic, not a rendering performance measurement.

The harness exists only in Debug builds. It starts after the normal backend
handshake and workspace initialization, before deferred startup navigation.
Settings, metadata database, operation journal, files and report are isolated
under `test-results/ui-smoke/<run>/`. The marker file created by the script
must exist before the host accepts the smoke root. Release binaries do not
contain the scenario runner. No user settings or folders are modified.

A failed run keeps its `<run>` folder as evidence. A passing run copies its
`report.json` to `test-results/ui-smoke/last-passed-report.json` and its polish
screenshots (`*-polish.png`) to `test-results/ui-smoke/last-passed/`, then
deletes the `<run>` folder; pass `-KeepResults` to keep it. Each run first
deletes the previous run's `last-passed-report.json` and `last-passed/*-polish.png`,
so a failed run never leaves an older pass's files looking like its own. A
folder that cannot be deleted (a helper process still running, a link inside
it, a file that cannot be listed or made writable) is kept with a warning and
does not fail a run that passed.

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
  changing List, Tiles and Details views. Details retains its column layout
  in both narrow and wide panes, with compact leading columns and horizontal
  scrolling for additional columns. A full-width single pane cycles through
  all views as well; resizing must never silently substitute List for Details.
  Requested window sizes may be capped by the desktop. `-MaxWindowWidth 1000`
  reproduces that constraint locally without changing the desktop resolution.
  Window sizes account for DPI scaling and the divider is recentered
  at each size (the product normally preserves its stored pixel width).
  Actual pane widths and the effective view are included in the report. These
  checks replace the former source-text assertion requiring a nonvirtualized
  panel.
- A real copy runs through the application's transfer queue, progress window,
  service and cancellation route. Cancellation must occur while running,
  reach the cancelled terminal UI state and remove staging paths.
- Pane-local filters remain visible across pane/omnibar changes; Clear filter
  affects only its pane. A real search exposes Return to folder and that button
  restores the listing.
- Unpinned empty preview collapses, the rail reopens it, and the pin button
  keeps it open. The toolbar reserves the omnibar's minimum usable width.
- The conflict dialog renders incoming/existing metadata and Keep both names.
  Choosing Keep both is checked against an actual backend copy without
  overwriting the existing file.
- Advanced Rename renders its Before/After rows, preserves extensions, and
  updates the rename scope. The run captures `workspace-polish.png`,
  `conflict-polish.png`, and `rename-polish.png` alongside the JSON report.

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

First-row latency is measured from navigation until layout produces a
container for the first row. Final navigation/render time includes a 200 ms
settling interval. Working set is a process snapshot, not a peak measurement.
Heartbeat gaps measure dispatcher scheduling delays, not display frame times.
Use the same machine, configuration and fixture size for before/after results.

`-Baseline` records the large-folder and navigation scenarios without the
virtualization assertion; it intentionally skips the remaining scenarios and
is for comparing an older implementation, not for acceptance or CI.

PR CI compiles the entire application/XAML, verifies production artifact
hashing, runs the 10,000-entry UI scenarios and uploads JSON reports. The
nightly/manual installer workflow also runs the full 100,000-entry suite and
uploads reports alongside installer evidence. CI requires an interactive
Windows desktop capable of hosting WinUI windows.

Fixture directories are retained for diagnosis and ignored by Git. Close the
test process before removing an individual run directory. The script owns
only its launched process; it never stops unrelated service instances.

## Other gates

`npm run check:winui` tests Core/IPC behavior without a XAML runtime.
`npm run build:winui` compiles the application and XAML.
`npm run check:winui-release-hash` loads only the production hash function from
the packaging script and checks known SHA-256 vectors, concurrent readers,
paths with spaces and missing-artifact failure. It does not build installers.
Packaged startup, file-operation, MSI, NSIS and upgrade smokes remain required
when changing their corresponding contracts.

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
