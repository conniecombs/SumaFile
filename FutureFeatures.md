# Future Features

## Session-Only RAM Cache

**Goal:** Make SumaFile feel substantially faster during an active session by retaining recently computed filesystem and presentation data in RAM. The cache must never be required for correctness and must leave no persistent cache files on disk.

### Why this matters

Windows already caches file reads in RAM, but SumaFile still repeats application work such as directory enumeration, metadata conversion, sorting, IPC serialization, thumbnail generation, and result-to-view-model conversion. A bounded, session-only cache removes that repeated work when users revisit folders, switch panes, search again, or toggle views.

### Existing foundation

SumaFile already has a RAM-resident thumbnail cache in `src-winui/SimpleFile.App/FileListThumbnailHost.cs`. It deduplicates in-flight thumbnail requests and limits the cache to 512 entries, but when the limit is exceeded it clears the entire cache. The broader implementation should preserve the good parts of that design while moving to budget-aware least-recently-used eviction.

## Scope

### Cache domains

| Domain | Authoritative owner | Cache key | Primary benefit |
|---|---|---|---|
| Directory listings | Rust service | Normalized path + sort + filter + hidden-file policy | Near-instant back/forward navigation and pane switches |
| Per-file metadata | Rust service | Normalized path + file identity/version | Avoid repeat attribute, size, and type queries |
| Folder metrics | Rust service | Normalized path + identity/version | Avoid recomputing recursive counts and sizes |
| Search results | Rust service | Query + scope + options + root identity | Fast query revisit and UI paging |
| Thumbnails | WinUI app | Path + file identity/version + requested size + video-frame token | Eliminate repeated image decode and IPC |
| Shell icons | WinUI app | Extension/path class + requested size | Eliminate repeated shell-provider calls |
| Git and remote status | Rust service | Repository/root + path + state version | Prevent repeated scans and network requests |

The Rust service must own caches derived from the filesystem. The WinUI app should own only UI-ready objects and recently visible thumbnails/icons. This avoids keeping the same heavyweight data in both processes.

### Non-goals

- Do not make RAM caching a substitute for durable user data.
- Do not cache file contents generally; large file contents must be streamed.
- Do not cache sensitive remote credentials or unencrypted user data.
- Do not write a hidden persistent cache directory as part of this feature.
- Do not use an unbounded `Dictionary`/`ConcurrentDictionary` as a cache.

## Design

### 1. Shared cache policy

Create explicit cache configuration rather than per-feature magic numbers.

- Default total session budget: **256 MB** on systems with 8 GB RAM; **512 MB** on systems with 16 GB or more.
- Allow a user setting: **128 MB, 256 MB, 512 MB, 1 GB, Auto**.
- Give each cache a soft budget and enforce a process-wide hard ceiling.
- Use weighted LRU eviction: evict least-recently-used entries until the insertion fits.
- Track each entry's estimated byte cost and last access time.
- Use TTLs only for freshness-sensitive entries; use LRU for normal filesystem data.
- Expose cache size, item count, hit rate, evictions, and bypasses in diagnostics.

### 2. Freshness and invalidation

A cache hit is valid only when the underlying source has not changed.

- Store a file identity/version with each entry: normalized path, modified time, size, and Windows file identifier where available.
- Invalidate a path and its parent directory after copy, move, rename, delete, create, attribute change, or archive operation.
- Subscribe to existing filesystem watcher events and invalidate affected paths/subtrees.
- Treat network shares, removable drives, and remote providers as short-lived: lower TTLs and invalidate on reconnect/error.
- Flush only the affected entries; never clear every cache because one item is stale.
- Include view options in keys so changing sort, filters, pane mode, or thumbnail size cannot return the wrong data.

### 3. Concurrency and cancellation

- Coalesce identical in-flight requests so two panes do not enumerate or decode the same item twice.
- Give each request a cancellation token; navigation away from a folder cancels queued work that is no longer visible.
- Do not hold a cache lock while performing I/O, image decoding, or IPC.
- Insert results only when the request is still valid and its generation/version is current.
- Perform eviction and cleanup off the WinUI thread.

### 4. Memory-pressure behavior

- Reduce cache budgets when the app receives a Windows low-memory notification.
- On pressure: evict thumbnails first, then previews/search results, then old directory data.
- When minimized or idle, trim cold thumbnail and preview entries.
- Provide a **Clear session cache** control under Storage & Cache.
- Never call full-process GC as a normal cache strategy; allow normal garbage collection after references are evicted.

## Implementation plan

### Phase 0 — Baseline and acceptance metrics

1. Add timing and cache telemetry around directory listing, metadata, search, thumbnail generation, IPC encode/decode, and first render.
2. Record cold versus warm performance on:
   - 10,000-file local folder
   - 100,000-file local folder
   - HDD and SSD
   - network share
   - removable drive
   - image-heavy folder
3. Establish release-build baselines for:
   - time to first visible item
   - time to complete listing
   - time to return to a recent folder
   - thumbnail first-paint time
   - peak working set
   - UI frame drops while scrolling

### Phase 1 — Upgrade the existing WinUI thumbnail/icon cache

1. Replace the current `MaxCachedThumbnails = 512` clear-all behavior with a byte-budgeted LRU cache.
2. Keep request coalescing and the existing in-flight cap.
3. Key entries with a lightweight file version so changed images do not display stale thumbnails.
4. Cache decoded `ImageSource` objects only at display sizes; do not retain original full-resolution images.
5. Apply a separate, smaller budget to video thumbnails and preview images.
6. Add unit tests for LRU order, byte accounting, eviction, invalidation, concurrency, and cancellation.

### Phase 2 — Rust service filesystem cache

1. Add a service-owned `SessionCache` module with typed caches for directory listings, metadata, folder metrics, and search results.
2. Implement a normalized cache-key type; do not use raw user-entered paths as cache keys.
3. Add request coalescing using shared futures/tasks for identical work.
4. Route `list_directory`, metadata, folder metrics, and search paths through the cache layer.
5. Return cached results through existing IPC without changing the UI-facing protocol unless paging requires it.
6. Invalidate cached entries from file-operation completion and watcher events.

### Phase 3 — UI and IPC refinements

1. Render a cached directory listing immediately, then reconcile it in the background if freshness is uncertain.
2. Batch metadata and thumbnail requests for visible ranges instead of issuing one request per row.
3. Prefetch one small, low-priority navigation step ahead (adjacent pane history or likely scroll range), but cancel it aggressively.
4. Keep the Rust cache authoritative for raw data and the WinUI cache authoritative for visual objects.
5. Add a diagnostics panel showing per-cache hit rate, bytes, entries, evictions, and active work.

### Phase 4 — Adaptive policy and user controls

1. Choose default budgets based on available physical memory and process working set.
2. Reduce budgets for battery saver, low-memory conditions, and devices with 8 GB RAM or less.
3. Add Storage & Cache settings:
   - Session cache: Off / 128 MB / 256 MB / 512 MB / 1 GB / Auto
   - Cache thumbnails
   - Preload nearby items
   - Clear session cache
4. Persist only the user preference, never the cached session data.

## Correctness and safety requirements

- Every cache must be optional; bypassing it must preserve normal behavior.
- Results must be scoped to the active provider, account, and permissions context.
- Cache keys must not expose secrets, credentials, or raw remote tokens.
- Failures and cancellations must not be cached as successful results.
- Use bounded queues and limits to prevent a large directory or thumbnail storm from exhausting RAM.
- Add integration tests proving that rename, delete, copy, move, and external file changes invalidate the correct entries.

## Success criteria

| Scenario | Target |
|---|---|
| Return to a recently visited local folder | First visible items in under 100 ms when valid in RAM |
| Thumbnail revisit | No new backend call or decode for a valid cached thumbnail |
| Repeated sort/filter change | Reuse cached source listing where possible |
| Large-folder navigation | UI remains responsive; no blocking work on the UI thread |
| Memory pressure | Cache trims safely without crash, corruption, or multi-second UI pause |
| Freshness | File operations and watcher events do not show stale entries after completion |

## Rollout order

1. Instrumentation and benchmarks
2. Thumbnail/icon LRU replacement
3. Rust directory and metadata cache
4. Invalidation wiring
5. Search, Git, remote, and folder-metrics caches
6. Adaptive budgets, diagnostics, and settings

Do not move to the next phase until the prior phase shows a measurable warm-path improvement without a meaningful regression in memory use, data freshness, or UI responsiveness.


## Performance Roadmap Beyond Session Cache

The session-only RAM cache is the first warm-path improvement. The following work targets the cold path, sustained responsiveness, and large or slow locations. Complete it in priority order and keep every change behind a benchmark.

### Priority 1 — Progressive, virtualized folder loading

**Problem:** A file manager feels slow when it waits to fully enumerate, sort, decorate, and create UI objects before it displays anything.

**Plan:**

1. Enumerate directories in pages and send the first useful batch immediately.
2. Render only rows in or near the viewport; recycle UI containers while scrolling.
3. Apply metadata, icons, Git status, and thumbnails after the filename/path rows are visible.
4. Sort incrementally when a full sort is expensive: show initial results quickly, then reconcile once the complete listing is ready.
5. Preserve selection, focus, and scroll position during background updates.

**Success measure:** First useful file rows appear in under 150 ms for a local folder and the UI remains responsive while a 100,000-item folder continues loading.

### Priority 2 — Foreground-first work scheduler

**Problem:** Background work such as thumbnails, folder sizes, Git scans, preview generation, and network probing can compete with what the user is trying to do now.

**Plan:**

1. Classify work into four queues: user input/navigation, visible content, near-visible prefetch, and background maintenance.
2. Always service navigation and visible rows before prefetch or maintenance.
3. Set bounded concurrency independently for disk I/O, CPU-heavy work, network I/O, thumbnail decodes, and archive operations.
4. Pause or reduce low-priority work during scrolling, typing, drag-and-drop, transfers, battery saver, or high CPU use.
5. Coalesce duplicate work and cancel stale jobs when the active folder, search, selection, or preview changes.

**Success measure:** Starting a search, changing folders, or opening a context menu never waits behind thumbnail, Git, or background scan work.

### Priority 3 — IPC batching, paging, and change deltas

**Problem:** The WinUI/Rust service split can lose responsiveness when it sends many small requests or repeatedly transmits full result sets.

**Plan:**

1. Use batch requests for visible-range metadata, icons, thumbnails, and selection details.
2. Page large directory and search results rather than transferring every row upfront.
3. Send only changed fields after the initial listing; do not resend unchanged rows.
4. Continue using binary frames for image-like payloads and extend compact typed frames to large repeated data where profiling proves JSON serialization is material.
5. Rate-limit progress updates and UI notifications to a smooth cadence rather than emitting one per low-level operation.

**Success measure:** IPC bytes and request count decrease on large folders without delaying the first visible result.

### Priority 4 — Demand-driven enrichment and predictive prefetch

**Problem:** Features can waste time calculating information the user may never see.

**Plan:**

1. Defer recursive folder sizes, hashes, media metadata, archive contents, Git status, and rich previews until the item is visible, selected, or explicitly requested.
2. Prefetch only likely next work: a small scroll-ahead window, the other pane's visible range, or recent back/forward history.
3. Stop prefetch immediately when the user reverses direction, changes folders, or starts an interactive operation.
4. Keep per-provider policies: no aggressive prefetch on network shares, removable drives, or remote accounts.

**Success measure:** Initial navigation requires fewer filesystem calls while common next actions still feel immediate.

### Priority 5 — Fast startup and staged feature activation

**Problem:** A slow first window makes the entire app feel slow even when later operations are fast.

**Plan:**

1. Measure process launch, first window, first interactive frame, service handshake, and first folder paint separately.
2. Start only the minimum required for the first window and initial directory.
3. Delay settings migration, update checks, remote-provider initialization, Git discovery, plug-in discovery, archive capability checks, and non-visible toolbar assets until after first interaction.
4. Reuse a single long-lived Rust service connection instead of repeatedly starting or reconnecting it.
5. Avoid synchronous disk or network access on the WinUI startup path.

**Success measure:** The first usable window appears before optional services finish initializing.

### Priority 6 — File-operation pipeline tuning

**Problem:** Copy, move, delete, and archive work can monopolize the system or delay the first visible progress update.

**Plan:**

1. Show the operation and first progress state before expensive validation and enumeration finish.
2. Stream large files with reusable, bounded buffers; never load a whole file into memory.
3. Select concurrency based on source and destination: limit same-disk parallelism, permit controlled parallelism across independent drives, and use conservative limits for network/removable media.
4. Preserve sparse files, timestamps, and metadata without introducing unnecessary extra passes.
5. Separate planning, execution, progress reporting, and post-operation cache invalidation.
6. Benchmark against Windows Explorer for local SSD, HDD, external USB, network-share, and many-small-file transfers.

**Success measure:** Copy/move begins promptly, sustains throughput, and leaves browsing and cancellation responsive.

### Priority 7 — Database/index-backed search for opt-in instant search

**Problem:** Repeated recursive search is inherently slow because it must walk the filesystem.

**Plan:**

1. Add an optional, clearly labeled Windows Search integration for indexed locations.
2. Maintain SumaFile's own lightweight per-session result cache for locations not covered by Windows Search.
3. Fall back to cancellable streaming filesystem search for unindexed, removable, network, or user-excluded locations.
4. Show the search source and freshness state so users understand whether a result is indexed or live.

**Success measure:** Indexed searches return initial results nearly immediately without weakening correctness for live searches.

### Priority 8 — Allocation and rendering hygiene

**Problem:** Frequent allocations, string formatting, collection resets, and binding churn cause stutters even when I/O is fast.

**Plan:**

1. Profile allocations and garbage-collection pauses while scrolling, sorting, and receiving directory updates.
2. Reuse buffers and collections in Rust; use object pooling only where profiles show significant churn.
3. Batch observable-collection updates and property notifications.
4. Avoid recreating images, brushes, styles, converters, or formatted strings for every row.
5. Make expensive formatting lazy and visible-range only.
6. Add frame-time instrumentation around scrolling, selection, pane resize, and view changes.

**Success measure:** Smooth scrolling and input under large-folder load, with no recurring long UI-thread frames.

### Priority 9 — Adaptive slow-location policy

**Problem:** Network shares, offline drives, cloud providers, archives, and failing devices can cause long stalls that should not be treated like local SSD folders.

**Plan:**

1. Detect provider and storage characteristics at request start.
2. Use shorter metadata budgets, lower concurrency, no speculative prefetch, and clear cancellation on slow/remote locations.
3. Timebox optional calls such as free-space lookup, Git detection, preview generation, and archive probing.
4. Surface an unobtrusive loading state instead of freezing the pane.
5. Cache negative/transient outcomes briefly to prevent repeated timeouts, then retry with backoff.

**Success measure:** An unavailable or slow source degrades gracefully without blocking the rest of the application.

## Cross-cutting performance guardrails

- Add a release-build benchmark suite and run it in CI on stable fixture sets.
- Require a before/after measurement for every claimed performance improvement.
- Track p50, p95, and p99 latency—not just averages.
- Test with Defender enabled, because real Windows users commonly have file scanning active.
- Test cold cache, warm Windows cache, warm SumaFile cache, and memory-pressure conditions separately.
- Prefer algorithmic, scheduling, batching, and rendering improvements before lower-level micro-optimizations.
- Keep an escape hatch for every optimization: users must be able to disable previews, background enrichment, prefetch, and large cache budgets.

## Combined rollout order

1. Measurement baseline and trace tooling
2. Progressive/virtualized directory loading
3. Foreground-first scheduler and cancellation audit
4. Session RAM cache and thumbnail LRU improvements
5. IPC batching and result paging
6. Demand-driven enrichment and limited prefetch
7. Startup staging and file-operation pipeline tuning
8. Indexed-search integration, adaptive slow-location policy, and continuous regression benchmarks
