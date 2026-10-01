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
