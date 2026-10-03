# Session Cache And Performance Roadmap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the `FutureFeatures.md` performance roadmap: measured warm-path session caching first, then progressive loading, scheduler, IPC, startup, search, slow-location, and rendering improvements without weakening filesystem correctness.

**Architecture:** Keep raw filesystem-derived caches inside the Rust service and UI-ready visual caches inside the WinUI app. Add shared cache policy, diagnostics, invalidation, and benchmark contracts before expanding caching, then stage cold-path performance work behind the same telemetry and verification gates. Existing IPC schema files stay the source of truth for any new commands or data models.

**Tech Stack:** Rust 2021 workspace crates, Tokio, named-pipe JSON-RPC and binary frames, C# .NET 10 WinUI 3, xUnit, PowerShell benchmark scripts, Node guard scripts, existing `npm run check*` gates.

**Spec:** `FutureFeatures.md`

## Global Constraints

- Session cache data must remain RAM-only; persist user preferences only.
- Every cache must be optional; bypassing it must preserve current behavior.
- No general file-content cache; large file contents remain streamed.
- Do not cache secrets, remote credentials, raw tokens, or unencrypted sensitive payloads.
- Cache keys must include provider/account/permission context where a result can differ by context.
- Use bounded byte budgets, bounded queues, and weighted least-recently-used eviction; no unbounded `Dictionary`, `ConcurrentDictionary`, or `HashMap` cache.
- Do not clear all caches because one entry is stale; invalidate affected paths, subtrees, domains, or providers.
- Do not hold cache locks while doing filesystem I/O, image decoding, network I/O, IPC writes, or WinUI work.
- Failures and cancellations must not be stored as successful cached results.
- Every performance claim needs before/after evidence for cold cache, warm Windows cache, warm SumaFile cache, and memory-pressure behavior where relevant.
- Required safety gates remain `npm run check`, `npm run check:winui`, `npm run check:rust`, `npm run check:release` when release-facing behavior changes, and `git diff --check`.

---

## File Structure

- Create `crates/simplefile-service/src/cache/mod.rs`: service-level session cache manager facade.
- Create `crates/simplefile-service/src/cache/lru.rs`: weighted LRU with byte accounting, hit/miss/eviction metrics.
- Create `crates/simplefile-service/src/cache/keys.rs`: normalized listing, metadata, folder metrics, search, Git, and provider keys.
- Create `crates/simplefile-service/src/cache/version.rs`: path identity/version helpers and slow-location TTL classification.
- Create `crates/simplefile-service/src/cache/coalesce.rs`: duplicate in-flight request coalescing.
- Create `src-winui/SimpleFile.Core/CachePolicy.cs`: normalized cache preference model.
- Create `src-winui/SimpleFile.Core/WeightedLruCache.cs`: WinUI-side byte-budget LRU for UI-ready objects.
- Create `src-winui/SimpleFile.Core/CacheDiagnostics.cs`: diagnostics view models and formatting.
- Modify `ipc/schema/v1/types.json` and `ipc/schema/v1/commands.json`: cache policy, diagnostics, clear-cache, and optional paging/batch contracts.
- Modify generated IPC bindings through `npm run generate:ipc-bindings`.
- Modify `crates/simplefile-service/src/session/mod.rs` and `crates/simplefile-service/src/session/jobs.rs`: hold session cache state, route cache-aware work, record timings, invalidate after operations.
- Modify `crates/simplefile-core/src/dir_list.rs`, `file_ops/folder_metrics.rs`, `metadata.rs`, `git.rs`, and `models.rs`: expose identity/version and diagnostics-safe models used by service caches.
- Modify `src-winui/SimpleFile.App/FileListThumbnailHost.cs` and `ShellIconLoader.cs`: replace existing clear-all caches with byte-budget LRU.
- Modify `src-winui/SimpleFile.Core/PaneNavigator.cs`, `ExplorerWorkspace.Navigation.cs`, `ExplorerWorkspace.FileOps.cs`, and `FileOperationService.cs`: warm paint, reconciliation, invalidation calls, cache diagnostics wrappers.
- Modify `src-winui/SimpleFile.App/SettingsWindow.xaml` and `SettingsWindow.SettingsState.cs`: session cache controls, diagnostics summary, and clear-cache action.
- Create `scripts/perf/create-session-cache-fixtures.ps1`, `scripts/perf/run-session-cache-benchmark.ps1`, and `scripts/check-session-cache-benchmark.mjs`: fixture generation, benchmark capture, and CI-friendly threshold checks.
- Create tests in `crates/simplefile-service/src/cache/*.rs`, `crates/simplefile-core/src/dir_list.rs`, `src-winui/SimpleFile.Tests/CachePolicyTests.cs`, `WeightedLruCacheTests.cs`, `ExplorerWorkspaceTests.cs`, `NamedPipeJsonClientTests.cs`, `WorkspaceSettingsStoreTests.cs`, and `WinUiSourceShapeTests.cs`.

---

### Task 1: Baseline Telemetry And Benchmark Harness

**Files:**
- Create: `scripts/perf/create-session-cache-fixtures.ps1`
- Create: `scripts/perf/run-session-cache-benchmark.ps1`
- Create: `scripts/check-session-cache-benchmark.mjs`
- Modify: `package.json`
- Modify: `src-winui/SimpleFile.Core/StartupTrace.cs`
- Modify: `src-winui/SimpleFile.Core/StartupTimingBudget.cs`
- Modify: `crates/simplefile-service/src/session/jobs.rs`
- Test: `src-winui/SimpleFile.Tests/StartupTimingBudgetTests.cs`

**Interfaces:**
- Produces: `npm run perf:session-cache` and `npm run check:session-cache-benchmark`.
- Produces: benchmark JSON with `scenario`, `coldMs`, `warmWindowsMs`, `warmSumaFileMs`, `firstVisibleMs`, `completeMs`, `workingSetMb`, and `thumbnailFirstPaintMs`.
- Consumes: existing `job.timing`, `ipc.timing`, and `%LOCALAPPDATA%\SumaFile\startup-timing.log`.

- [ ] **Step 1: Write failing benchmark parser test**

Add an xUnit or Node-level test fixture proving benchmark JSON with missing `firstVisibleMs` fails validation and complete JSON passes.

Run: `node scripts/check-session-cache-benchmark.mjs --fixture scripts/perf/fixtures/missing-first-visible.json`
Expected: FAIL with `missing metric firstVisibleMs`.

- [ ] **Step 2: Add fixture generator**

Implement `create-session-cache-fixtures.ps1` so it creates deterministic folders under a caller-supplied root:

```powershell
param(
  [Parameter(Mandatory=$true)][string]$Root,
  [int]$SmallCount = 10000,
  [int]$LargeCount = 100000,
  [int]$ImageCount = 500
)
```

The script creates `local-10k`, `local-100k`, and `images` folders using small text files and generated PNG files; it must never write outside `$Root`.

- [ ] **Step 3: Add benchmark runner**

Implement `run-session-cache-benchmark.ps1` with parameters `-AppPath`, `-FixtureRoot`, `-Output`, and `-Scenario`. Capture at least cold navigation, repeat navigation, thumbnail revisit, and startup trace summaries. Use the existing app log locations and write one JSON array to `-Output`.

- [ ] **Step 4: Extend service timing logs**

In `session/jobs.rs`, expand current `job.timing` logs for `ListDirectory`, `SearchFiles`, and thumbnail generation to include row count, byte estimate, cache state once cache exists, and request id where available. Before cache implementation, emit `cache=disabled`.

- [ ] **Step 5: Wire scripts**

Add package scripts:

```json
"perf:session-cache": "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/perf/run-session-cache-benchmark.ps1",
"check:session-cache-benchmark": "node scripts/check-session-cache-benchmark.mjs"
```

- [ ] **Step 6: Verify Task 1**

Run:
`node scripts/check-session-cache-benchmark.mjs --fixture scripts/perf/fixtures/valid-session-cache.json`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~StartupTimingBudgetTests"`
`npm run check`

Expected: all pass.

### Task 2: Shared Cache Policy And Diagnostics Models

**Files:**
- Create: `src-winui/SimpleFile.Core/CachePolicy.cs`
- Create: `src-winui/SimpleFile.Core/CacheDiagnostics.cs`
- Modify: `src-winui/SimpleFile.Core/UiSettings.cs`
- Modify: `src-winui/SimpleFile.Core/WorkspaceSettingsStore.cs`
- Modify: `src-winui/SimpleFile.Ipc/Models.cs`
- Modify: `crates/simplefile-core/src/models.rs`
- Modify: `ipc/schema/v1/types.json`
- Test: `src-winui/SimpleFile.Tests/CachePolicyTests.cs`
- Test: `src-winui/SimpleFile.Tests/WorkspaceSettingsStoreTests.cs`

**Interfaces:**
- Produces C# enum `SessionCacheMode` with values `Off`, `Mb128`, `Mb256`, `Mb512`, `Gb1`, `Auto`.
- Produces Rust/C# IPC models `CachePolicy`, `CacheDiagnostics`, `CacheDomainDiagnostics`.
- Produces persisted settings keys `sessionCache.mode`, `sessionCache.thumbnails`, and `sessionCache.preloadNearbyItems`.
- Retains compatibility reads for existing `thumbnailCacheMaxMb` and `thumbnailCachePath` without using a disk cache path.

- [ ] **Step 1: Write failing C# normalization tests**

Add tests proving:

```csharp
Assert.Equal(SessionCacheMode.Auto, CachePolicy.NormalizeMode("Auto"));
Assert.Equal(256u, CachePolicy.ResolveBudgetMb(SessionCacheMode.Auto, physicalMemoryMb: 8192));
Assert.Equal(512u, CachePolicy.ResolveBudgetMb(SessionCacheMode.Auto, physicalMemoryMb: 16384));
Assert.Equal(0u, CachePolicy.ResolveBudgetMb(SessionCacheMode.Off, physicalMemoryMb: 32768));
```

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~CachePolicyTests"`
Expected: FAIL because `CachePolicy` does not exist.

- [ ] **Step 2: Implement settings model**

Add `SessionCacheMode`, `CacheThumbnails`, and `PreloadNearbyItems` to `UiSettings`. Keep `ThumbnailCacheMaxMb` and `ThumbnailCachePath` readable as legacy settings, but route new UI through `SessionCacheMode`.

- [ ] **Step 3: Implement diagnostics models**

Add:

```csharp
public sealed class CacheDomainDiagnostics
{
    public string Domain { get; set; } = "";
    public ulong Bytes { get; set; }
    public int Entries { get; set; }
    public ulong Hits { get; set; }
    public ulong Misses { get; set; }
    public ulong Evictions { get; set; }
    public ulong Bypasses { get; set; }
    public int InFlight { get; set; }
}
```

Mirror the same fields in Rust with snake_case serialization.

- [ ] **Step 4: Update schema and generated model consistency**

Add the cache diagnostics types to `types.json`. Regenerate IPC bindings after Task 4 adds commands, or run schema consistency tests here if models are hand-authored first.

- [ ] **Step 5: Verify Task 2**

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~CachePolicyTests|FullyQualifiedName~WorkspaceSettingsStoreTests"`
`cargo test -p simplefile-core models --locked`

Expected: all pass.

### Task 3: WinUI Weighted LRU For Thumbnails And Shell Icons

**Files:**
- Create: `src-winui/SimpleFile.Core/WeightedLruCache.cs`
- Modify: `src-winui/SimpleFile.App/FileListThumbnailHost.cs`
- Modify: `src-winui/SimpleFile.App/ShellIconLoader.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.Chrome.cs`
- Test: `src-winui/SimpleFile.Tests/WeightedLruCacheTests.cs`
- Test: `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`

**Interfaces:**
- Produces: `WeightedLruCache<TKey,TValue>` with `TryGet`, `Set`, `RemoveWhere`, `Clear`, and `Snapshot`.
- Produces: `FileListThumbnailHost.ApplyCachePolicy(CachePolicy policy)`.
- Produces: `FileListThumbnailHost.ClearSessionCache()` and `ShellIconLoader.ClearSessionCache()`.
- Consumes: current thumbnail request coalescing and `MaxInFlightThumbnails`.

- [ ] **Step 1: Write failing LRU tests**

Add tests proving byte-budget eviction, last-access promotion, replacement weight accounting, and clear behavior:

```csharp
var cache = new WeightedLruCache<string, string>(maxBytes: 10);
cache.Set("a", "A", weightBytes: 6);
cache.Set("b", "B", weightBytes: 4);
Assert.True(cache.TryGet("a", out _));
cache.Set("c", "C", weightBytes: 5);
Assert.True(cache.TryGet("a", out _));
Assert.False(cache.TryGet("b", out _));
Assert.True(cache.TryGet("c", out _));
```

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WeightedLruCacheTests"`
Expected: FAIL because the cache does not exist.

- [ ] **Step 2: Replace thumbnail clear-all behavior**

Remove `MaxCachedThumbnails = 512` and `Cache.Clear()` on overflow from `FileListThumbnailHost`. Estimate image weight as `requestSize * requestSize * 4` for decoded images and double it for video thumbnails. Keep the existing in-flight dictionary and load gate.

- [ ] **Step 3: Strengthen thumbnail cache key versioning**

Replace `row.ModifiedText` in the thumbnail key with a stable version helper using `row.Path`, `row.Size`, `row.Modified`, requested size, and video frame token. When file identity helpers exist in subsequent tasks, switch to them without changing public APIs.

- [ ] **Step 4: Convert shell icons to bounded LRU**

Replace `ConcurrentDictionary<string, BitmapImage>` in `ShellIconLoader` with a byte-budgeted LRU. Use key domains `extension`, `directory`, and `path-specific`. Keep file-specific executable/icon/shortcut extraction asynchronous and update the cache entry when the real icon arrives.

- [ ] **Step 5: Add source-shape regression**

Assert `FileListThumbnailHost.cs` no longer contains `MaxCachedThumbnails` or `Cache.Clear();` in the overflow path, and `ShellIconLoader.cs` no longer declares `ConcurrentDictionary<string, BitmapImage> Cache`.

- [ ] **Step 6: Verify Task 3**

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WeightedLruCacheTests|FullyQualifiedName~WinUiSourceShapeTests"`
`npm run build:winui`

Expected: all pass.

### Task 4: IPC Cache Configuration And Diagnostics Commands

**Files:**
- Modify: `ipc/schema/v1/commands.json`
- Modify: `ipc/schema/v1/types.json`
- Regenerate: `src-winui/SimpleFile.Ipc/Protocol.Generated.cs`
- Regenerate: `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.Generated.cs`
- Regenerate: `crates/simplefile-ipc/src/protocol_generated.rs`
- Modify: `src-winui/SimpleFile.Ipc/ISimpleFileIpc.cs`
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`
- Modify: `crates/simplefile-service/src/dispatch/mod.rs`
- Modify: `crates/simplefile-service/src/dispatch/params.rs`
- Modify: `crates/simplefile-service/src/dispatch/handlers.rs`
- Test: `src-winui/SimpleFile.Tests/NamedPipeJsonClientTests.cs`
- Test: `crates/simplefile-ipc/tests/schema_consistency.rs`
- Test: `crates/simplefile-service/src/dispatch/tests.rs`

**Interfaces:**
- Produces IPC methods:
  - `configure_session_cache({ policy: CachePolicy }) -> null`
  - `get_cache_diagnostics() -> CacheDiagnostics`
  - `clear_session_cache({ domains?: string[] }) -> CacheDiagnostics`
- Consumes: Task 2 model names exactly.

- [ ] **Step 1: Write failing schema and client tests**

Add tests proving the three method constants exist, generated C# client sends the expected method names, and service dispatch parses `clear_session_cache` with empty domains as all domains.

Run:
`npm run check:ipc-schema`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~NamedPipeJsonClientTests"`
`cargo test -p simplefile-service cache_diagnostics_dispatch --locked`

Expected: FAIL because schema commands do not exist.

- [ ] **Step 2: Update schema**

Add methods and types. Increment `domainMethodCount` by three and update parity/guard documentation in the same task if the checker requires the new count.

- [ ] **Step 3: Generate bindings**

Run: `npm run generate:ipc-bindings`

Review generated Rust and C# files only for expected cache method additions.

- [ ] **Step 4: Implement no-op service handlers**

Before the cache manager lands, return diagnostics with all domains at zero and accept configure/clear. This lets UI and tests integrate against stable IPC before filesystem caching changes behavior.

- [ ] **Step 5: Verify Task 4**

Run:
`npm run check:ipc-generated`
`npm run check:ipc-schema`
`cargo test -p simplefile-service cache_diagnostics_dispatch --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~NamedPipeJsonClientTests"`

Expected: all pass.

### Task 5: Rust Service SessionCache Manager For Listings, Metadata, Metrics, And Search

**Files:**
- Create: `crates/simplefile-service/src/cache/mod.rs`
- Create: `crates/simplefile-service/src/cache/lru.rs`
- Create: `crates/simplefile-service/src/cache/keys.rs`
- Create: `crates/simplefile-service/src/cache/version.rs`
- Create: `crates/simplefile-service/src/cache/coalesce.rs`
- Modify: `crates/simplefile-service/src/lib.rs`
- Modify: `crates/simplefile-service/src/session/mod.rs`
- Modify: `crates/simplefile-service/src/session/jobs.rs`
- Modify: `crates/simplefile-core/src/dir_list.rs`
- Modify: `crates/simplefile-core/src/file_ops/folder_metrics.rs`
- Test: Rust unit tests in the new cache modules
- Test: `crates/simplefile-service/src/session/tests.rs`

**Interfaces:**
- Produces: `SessionCacheManager::new(policy: CachePolicy)`.
- Produces: `get_or_compute_listing`, `get_or_compute_folder_metrics`, `get_or_compute_search`, `invalidate_paths`, `clear`, and `diagnostics`.
- Produces: `PathVersion { normalized_path, modified_utc, size, file_id }` where `file_id` is `None` on platforms or locations where it cannot be read cheaply.
- Consumes: existing `ListDirectoryOptions`, `SearchOptions`, `FolderMetrics`, and cache policy from Task 4.

- [ ] **Step 1: Write failing weighted LRU Rust tests**

Add tests for byte-budget eviction, hit/miss accounting, replacement accounting, and oversize bypass.

Run: `cargo test -p simplefile-service cache::lru --locked`
Expected: FAIL because cache modules do not exist.

- [ ] **Step 2: Implement weighted LRU**

Use only standard collections: `HashMap<K, CacheEntry<V>>` plus `VecDeque<K>` access log with lazy stale-key removal during eviction. Require `K: Clone + Eq + Hash`. Track `hits`, `misses`, `evictions`, `bypasses`, `bytes`, and `entries`.

- [ ] **Step 3: Implement normalized keys**

Add typed keys:

```rust
pub(crate) struct ListingCacheKey {
    pub normalized_path: String,
    pub sort_by: String,
    pub sort_ascending: bool,
    pub filter: Option<String>,
    pub include_hidden: bool,
    pub provider_context: String,
}
```

Add equivalent keys for folder metrics, search, metadata, and Git status. Normalize paths with existing validation helpers and lower-case only where Windows path semantics require case-insensitive matching.

- [ ] **Step 4: Implement path versioning**

Read modified time, size, and Windows file index where available. For network shares, removable drives, remote providers, and error-prone paths, set short TTL metadata and count freshness uncertainty in diagnostics as `bypasses`.

- [ ] **Step 5: Add coalescing**

Implement `InFlightRegistry<K,V>` with Tokio `Mutex<HashMap<K, Vec<oneshot::Sender<Result<V, String>>>>>`. The first request computes; followers await the result clone. On cancellation, remove only the waiting sender, not the underlying compute unless no waiters remain and the compute has not started.

- [ ] **Step 6: Route service jobs through cache**

Update `spawn_list_directory`, `spawn_folder_metrics`, and `spawn_search_files` to use `SessionCacheManager` when policy is not Off. Cache only successful complete results. For `list_directory` streaming, emit cached chunks immediately using the same chunk sizing and return the same final result shape.

- [ ] **Step 7: Verify Task 5**

Run:
`cargo test -p simplefile-service cache --locked`
`cargo test -p simplefile-service session --locked`
`cargo test -p simplefile-core dir_list --locked`

Expected: all pass.

### Task 6: Invalidation From File Operations, Watchers, And Provider State

**Files:**
- Modify: `crates/simplefile-service/src/session/mod.rs`
- Modify: `crates/simplefile-service/src/session/jobs.rs`
- Modify: `crates/simplefile-service/src/dispatch/handlers.rs`
- Modify: `crates/simplefile-service/src/watcher.rs`
- Modify: `crates/simplefile-service/src/progress.rs`
- Test: `crates/simplefile-service/src/session/tests.rs`
- Test: `crates/simplefile-service/src/watcher.rs`

**Interfaces:**
- Produces: `SessionCacheManager::invalidate_paths<I: IntoIterator<Item = String>>(paths, reason)`.
- Produces invalidation reasons: `Create`, `Delete`, `Rename`, `Copy`, `Move`, `AttributeChange`, `ArchiveMutation`, `Watcher`, `ProviderReconnect`.
- Consumes: current file-operation dispatch and watcher `FileChangeEvent`.

- [ ] **Step 1: Write failing invalidation tests**

Add tests proving a cached parent listing is invalidated after create/delete/rename and watcher events invalidate both the changed path and its parent.

Run: `cargo test -p simplefile-service cache_invalidation --locked`
Expected: FAIL because cache invalidation is not wired.

- [ ] **Step 2: Invalidate synchronous dispatch mutations**

After successful `create_directory`, `create_file`, `rename_entry`, `delete_entry`, `move_to_trash`, `restore_recycle_bin`, `empty_recycle_bin`, archive extraction/create, tag changes that alter visible file rows, and remote mutations, invalidate affected parent paths and changed paths before responding.

- [ ] **Step 3: Invalidate progress copy/move completion**

In `spawn_copy_move_with_progress`, inspect committed, skipped, failed, and pending outcomes. Invalidate source parents and destination on successful or partial successful operations. Do not invalidate on cancellation before any committed result.

- [ ] **Step 4: Invalidate watcher events**

When `watcher.rs` emits `FileChangeEvent`, call cache invalidation before emitting the event to the UI. Collapse storm invalidation to the watched path, matching existing coalescer behavior.

- [ ] **Step 5: Verify Task 6**

Run:
`cargo test -p simplefile-service cache_invalidation --locked`
`cargo test -p simplefile-service watcher --locked`
`npm run check:rust`

Expected: all pass.

### Task 7: Warm Paint, Reconciliation, Visible-Range Batching, And Prefetch

**Files:**
- Modify: `src-winui/SimpleFile.Core/PaneNavigator.cs`
- Modify: `src-winui/SimpleFile.Core/ExplorerWorkspace.Navigation.cs`
- Modify: `src-winui/SimpleFile.Core/ExplorerPane.cs`
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`
- Modify: `src-winui/SimpleFile.App/FileRowView.xaml.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.Chrome.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.FileListEvents.cs`
- Test: `src-winui/SimpleFile.Tests/ExplorerWorkspaceTests.cs`
- Test: `src-winui/SimpleFile.Tests/FileOperationServiceTests.cs`

**Interfaces:**
- Produces: `PaneNavigator.TryApplyWarmListingAsync(...)` and `PaneNavigationResult.FromWarmCache`.
- Produces: `FileOperationService.ConfigureSessionCacheAsync(CachePolicy policy)` and `GetCacheDiagnosticsAsync`.
- Produces: visible range thumbnail batch request hook in WinUI list surfaces.

- [ ] **Step 1: Write failing warm-paint tests**

Add a fake backend that returns a warm cached listing immediately and a reconciled listing afterward. Assert the pane shows cached rows without clearing to empty, then replaces them with reconciled rows if the navigation token is still current.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ExplorerWorkspaceTests"`
Expected: FAIL because the workspace always clears entries before listing.

- [ ] **Step 2: Add cache-aware navigation state**

Add pane fields `HasWarmListing`, `ListingIsReconciling`, and `LastListingSource`. Keep `IsNavigating` false after the first visible cached rows, and keep `ListingInProgress` true until reconciliation completes.

- [ ] **Step 3: Configure service cache on startup and settings save**

On backend connection, send `configure_session_cache` using current settings. On Settings save, reconfigure the service and clear UI caches if the mode moves to Off or the budget shrinks below current usage.

- [ ] **Step 4: Batch visible-range thumbnail requests**

When the file list realizes or scrolls rows, collect visible image/video paths and call existing `GenerateThumbnailsAsync` for ranges instead of one request per row where possible. Keep per-row fallback for cache misses and cancellation.

- [ ] **Step 5: Add limited prefetch**

Prefetch only the next small scroll window and the other pane's visible range when `PreloadNearbyItems` is true. Cancel prefetch on navigation, typing, drag/drop, transfer start, and scroll direction reversal.

- [ ] **Step 6: Verify Task 7**

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ExplorerWorkspaceTests|FullyQualifiedName~FileOperationServiceTests"`
`npm run build:winui`

Expected: all pass.

### Task 8: Foreground-First Scheduler And Cancellation Audit

**Files:**
- Modify: `crates/simplefile-service/src/scheduler.rs`
- Modify: `crates/simplefile-service/src/session/jobs.rs`
- Modify: `crates/simplefile-service/src/search.rs`
- Modify: `crates/simplefile-service/src/progress.rs`
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`
- Test: `crates/simplefile-service/src/scheduler.rs`
- Test: `crates/simplefile-service/src/session/tests.rs`

**Interfaces:**
- Produces work lanes: `Navigation`, `VisibleContent`, `NearVisiblePrefetch`, `BackgroundMaintenance`, `Transfer`.
- Produces resource classes: `DiskIo`, `Cpu`, `NetworkIo`, `ThumbnailDecode`, `Archive`.
- Consumes existing `BlockingScheduler::run_general` and `run_transfer`, preserving compatibility wrappers.

- [ ] **Step 1: Write failing scheduler tests**

Add tests proving a queued navigation job starts before queued background thumbnail/search work, transfer concurrency stays capped, and cancellation removes stale prefetch before execution.

Run: `cargo test -p simplefile-service scheduler --locked`
Expected: FAIL because the scheduler has only general and transfer semaphores.

- [ ] **Step 2: Extend scheduler API**

Add `run_with_priority(lane, resource, task)` and keep `run_general`/`run_transfer` as wrappers. Navigation and visible content must always acquire before prefetch and background work when permits are available.

- [ ] **Step 3: Route jobs to lanes**

Use `Navigation` for directory listing, `VisibleContent` for visible metadata/thumbs, `NearVisiblePrefetch` for prefetch, `BackgroundMaintenance` for Git scans/search enrichment/folder metrics, and `Transfer` for copy/move/archive transfers.

- [ ] **Step 4: Audit cancellation**

Ensure search, folder metrics, duplicate scan, disk cleanup, thumbnail batch, and prefetch tasks check cancellation before expensive loops, before cache insert, and before emitting final completion.

- [ ] **Step 5: Verify Task 8**

Run:
`cargo test -p simplefile-service scheduler --locked`
`cargo test -p simplefile-service session --locked`
`npm run check:rust`

Expected: all pass.

### Task 9: IPC Batching, Paging, And Change Deltas

**Files:**
- Modify: `ipc/schema/v1/types.json`
- Modify: `ipc/schema/v1/commands.json`
- Modify: `src-winui/SimpleFile.Ipc/BinaryFrameCodec.cs`
- Modify: `src-winui/SimpleFile.Ipc/NamedPipeJsonClient.cs`
- Modify: `crates/simplefile-service/src/binary.rs`
- Modify: `crates/simplefile-service/src/session/jobs.rs`
- Modify: `crates/simplefile-core/src/dir_list.rs`
- Test: `src-winui/SimpleFile.Tests/NamedPipeJsonClientTests.cs`
- Test: `crates/simplefile-ipc/tests/schema_consistency.rs`
- Test: Rust binary codec tests in `crates/simplefile-service/src/binary.rs`

**Interfaces:**
- Produces optional listing/search paging params `pageToken`, `pageSize`, and result field `nextPageToken`.
- Produces optional `list_directory.delta` event for changed fields after an initial listing.
- Keeps current `list_directory.chunk` behavior valid for existing callers.

- [ ] **Step 1: Write failing codec tests**

Add binary codec tests for a paged directory result and a delta event containing path, field names, and changed values.

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~NamedPipeJsonClientTests"`
`cargo test -p simplefile-service binary --locked`

Expected: FAIL because paging and delta frames do not exist.

- [ ] **Step 2: Extend schema compatibly**

Add optional params and optional result fields so old callers still work. Add binary frame tags for deltas only after schema tests pin the value.

- [ ] **Step 3: Implement paged service paths**

For large directories and searches, keep first useful rows streaming immediately. Use page tokens only when the caller opts in. Never delay first rows to build every page.

- [ ] **Step 4: Implement delta emission**

When a cached listing is shown and reconciliation finds changes, send only changed rows or fields where this is smaller than a full replacement. Fall back to full listing chunks if the delta would be larger or ambiguous.

- [ ] **Step 5: Verify Task 9**

Run:
`npm run check:ipc-generated`
`npm run check:ipc-schema`
`cargo test -p simplefile-service binary --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~NamedPipeJsonClientTests"`

Expected: all pass.

### Task 10: Demand-Driven Enrichment, Git/Search Cache, And Indexed Search Routing

**Files:**
- Modify: `src-winui/SimpleFile.Core/SearchViewModel.cs`
- Modify: `src-winui/SimpleFile.Core/SearchOptionsFactory.cs`
- Modify: `src-winui/SimpleFile.Core/WindowsSearchService.cs`
- Modify: `src-winui/SimpleFile.Core/ExplorerWorkspace.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.Search.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.Git.cs`
- Modify: `crates/simplefile-service/src/search.rs`
- Modify: `crates/simplefile-core/src/git.rs`
- Test: `src-winui/SimpleFile.Tests/SearchOptionsFactoryTests.cs`
- Test: `src-winui/SimpleFile.Tests/WindowsSearchServiceTests.cs`
- Test: Rust tests in `crates/simplefile-service/src/search.rs`

**Interfaces:**
- Produces: search source labels `Indexed`, `SessionCache`, and `LiveFilesystem`.
- Produces: Git status cache keyed by repository root, path, and HEAD/index/worktree state version.
- Consumes: existing `WindowsSearchService.IsPathIndexed` and `SearchIndexAsync`.

- [ ] **Step 1: Write failing search source tests**

Add tests proving indexed local paths route to Windows Search when available, unindexed/removable/network paths route to live filesystem search, and repeated identical live searches can report `SessionCache`.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WindowsSearchServiceTests|FullyQualifiedName~SearchOptionsFactoryTests"`
Expected: FAIL because source labeling and routing are not exposed.

- [ ] **Step 2: Defer expensive enrichment**

Ensure recursive folder sizes, hashes, media metadata, archive contents, Git status, and rich previews are requested only when visible, selected, explicitly enabled, or explicitly invoked. Remove automatic enrichment from initial navigation paths unless it is already behind an explicit setting.

- [ ] **Step 3: Add search cache and source display**

Cache successful search results in the service by query, scope, options, root identity, and provider context. In WinUI, show source and freshness state in the search status text.

- [ ] **Step 4: Add Git status cache**

Cache Git file status results by repo root and state version. Invalidate on watcher events under `.git`, Git command completion, and file-operation changes under the repo.

- [ ] **Step 5: Verify Task 10**

Run:
`cargo test -p simplefile-service search --locked`
`cargo test -p simplefile-core git --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WindowsSearchServiceTests|FullyQualifiedName~SearchOptionsFactoryTests"`

Expected: all pass.

### Task 11: Startup Staging And File-Operation Pipeline Tuning

**Files:**
- Modify: `src-winui/SimpleFile.App/MainWindow.xaml.cs`
- Modify: `src-winui/SimpleFile.Core/BackendSession.cs`
- Modify: `src-winui/SimpleFile.Core/StartupTimingBudget.cs`
- Modify: `crates/simplefile-service/src/progress.rs`
- Modify: `crates/simplefile-service/src/progress/execute.rs`
- Modify: `crates/simplefile-service/src/progress/plan.rs`
- Test: `src-winui/SimpleFile.Tests/StartupTimingBudgetTests.cs`
- Test: Rust tests in `crates/simplefile-service/src/progress.rs`

**Interfaces:**
- Produces startup markers for `first-window`, `first-interactive-frame`, `service-handshake`, `first-folder-paint`, and optional-service activation.
- Produces progress status before expensive transfer validation finishes.
- Consumes existing `OperationRegistry` cancellation.

- [ ] **Step 1: Write failing startup-stage tests**

Add budget tests requiring the new markers and proving optional work markers occur after `MainWindow.Connect.ready`.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~StartupTimingBudgetTests"`
Expected: FAIL because those markers are absent.

- [ ] **Step 2: Stage optional startup services**

Delay settings migration, update checks, remote-provider initialization, Git discovery, archive capability checks, non-visible toolbar assets, and non-visible preview work until after first interaction or idle.

- [ ] **Step 3: Tune file operation progress**

Emit operation queued/started progress before full enumeration. Use reusable bounded buffers for large files. Keep same-disk parallelism conservative, allow controlled parallelism across independent drives, and keep network/removable transfer concurrency lower.

- [ ] **Step 4: Preserve metadata and cancellation**

Keep sparse-file/timestamp/metadata preservation in the execution path. Ensure cancellation leaves partial output cleaned up or clearly reported through `TransferBatchOutcome`.

- [ ] **Step 5: Verify Task 11**

Run:
`cargo test -p simplefile-service progress --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~StartupTimingBudgetTests|FullyQualifiedName~Transfer"`
`npm run smoke:winui-file-ops`

Expected: all pass after a release payload exists for the smoke.

### Task 12: Adaptive Slow-Location Policy And Memory Pressure

**Files:**
- Create: `crates/simplefile-service/src/location_policy.rs`
- Modify: `crates/simplefile-service/src/cache/version.rs`
- Modify: `crates/simplefile-service/src/scheduler.rs`
- Modify: `crates/simplefile-core/src/drives.rs`
- Modify: `src-winui/SimpleFile.Core/DrivePresentation.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.xaml.cs`
- Test: Rust tests in `crates/simplefile-service/src/location_policy.rs`
- Test: `src-winui/SimpleFile.Tests/DrivePresentationTests.cs`

**Interfaces:**
- Produces `LocationKind`: `LocalSsdOrFixed`, `HddOrUnknownFixed`, `NetworkShare`, `Removable`, `RemoteProvider`, `Archive`, `Unavailable`.
- Produces policy decisions for TTL, concurrency caps, prefetch enablement, metadata timeouts, and negative-cache backoff.
- Consumes existing drive/network detection and watcher events.

- [ ] **Step 1: Write failing policy tests**

Add tests proving UNC paths disable aggressive prefetch, removable drives use short TTLs, unavailable drives use negative-cache backoff, and local fixed paths keep normal policy.

Run: `cargo test -p simplefile-service location_policy --locked`
Expected: FAIL because policy module does not exist.

- [ ] **Step 2: Implement provider classification**

Classify request location at operation start. Avoid repeated provider probing inside a single request. Cache negative transient outcomes briefly with backoff and diagnostics.

- [ ] **Step 3: Add low-memory trimming**

On Windows low-memory notification in the WinUI host and service-side working-set threshold checks, trim in order: thumbnails/icons, previews/search results, directory and metadata caches. Never call full-process GC as normal strategy.

- [ ] **Step 4: Verify Task 12**

Run:
`cargo test -p simplefile-service location_policy --locked`
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~DrivePresentationTests"`
`npm run check:rust`

Expected: all pass.

### Task 13: Settings, Diagnostics Panel, And Clear Session Cache

**Files:**
- Modify: `src-winui/SimpleFile.App/SettingsWindow.xaml`
- Modify: `src-winui/SimpleFile.App/SettingsWindow.SettingsState.cs`
- Modify: `src-winui/SimpleFile.App/SettingsWindow.xaml.cs`
- Modify: `src-winui/SimpleFile.Core/WorkspaceSettingsStore.cs`
- Modify: `src-winui/SimpleFile.Core/FileOperationService.cs`
- Test: `src-winui/SimpleFile.Tests/WorkspaceSettingsStoreTests.cs`
- Test: `src-winui/SimpleFile.Tests/WinUiSourceShapeTests.cs`

**Interfaces:**
- Produces settings controls:
  - Session cache: `Off`, `128 MB`, `256 MB`, `512 MB`, `1 GB`, `Auto`
  - `Cache thumbnails`
  - `Preload nearby items`
  - `Clear session cache`
- Produces diagnostics summary rows for cache domain, bytes, entries, hit rate, evictions, bypasses, and active work.
- Removes disk cache location from the visible Settings page.

- [ ] **Step 1: Write failing settings tests**

Add tests proving settings persistence stores `sessionCache.mode`, `sessionCache.thumbnails`, and `sessionCache.preloadNearbyItems`, and that visible settings XAML no longer contains `Cache Location`.

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WorkspaceSettingsStoreTests|FullyQualifiedName~WinUiSourceShapeTests"`
Expected: FAIL because settings still expose thumbnail disk cache controls.

- [ ] **Step 2: Replace Storage & Cache UI**

Use a ComboBox for session cache budget, ToggleSwitch controls for thumbnails and preload, a compact diagnostics table, and a clear-cache button. Keep page text focused on current state, not implementation description.

- [ ] **Step 3: Wire clear-cache action**

Clear WinUI `FileListThumbnailHost` and `ShellIconLoader` caches, call `clear_session_cache` on the service, then refresh diagnostics. Do not refresh folders unless the user separately requests Refresh.

- [ ] **Step 4: Verify Task 13**

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~WorkspaceSettingsStoreTests|FullyQualifiedName~WinUiSourceShapeTests"`
`npm run build:winui`

Expected: all pass.

### Task 14: Allocation And Rendering Hygiene

**Files:**
- Modify: `src-winui/SimpleFile.Core/ListReplace.cs`
- Modify: `src-winui/SimpleFile.Core/EntryPresentation.cs`
- Modify: `src-winui/SimpleFile.App/FileRowView.xaml.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.Columns.cs`
- Modify: `src-winui/SimpleFile.App/MainWindow.FileListEvents.cs`
- Modify: `crates/simplefile-core/src/dir_list.rs`
- Modify: `crates/simplefile-service/src/binary.rs`
- Test: `src-winui/SimpleFile.Tests/ListReplaceTests.cs`
- Test: `src-winui/SimpleFile.Tests/EntryPresentationTests.cs`

**Interfaces:**
- Produces batched collection update helper for listing replacement and chunk append.
- Produces lazy formatting for expensive row text where the row is not visible.
- Consumes benchmark frame-time instrumentation from Task 1.

- [ ] **Step 1: Write failing collection batching tests**

Add tests proving replacing 1000 entries produces one logical replace notification path and preserving selected paths does not require rebuilding every row object.

Run: `dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ListReplaceTests|FullyQualifiedName~EntryPresentationTests"`
Expected: FAIL if current helper cannot express the batched behavior.

- [ ] **Step 2: Batch visible row updates**

Avoid repeated collection resets during listing chunks, reconciliation deltas, sort changes, and filter updates. Preserve selection, focus, and scroll where current WinUI controls expose stable APIs.

- [ ] **Step 3: Make formatting visible-range-aware**

Avoid formatting size, modified date, type, Git text, parent path, and symlink text for rows outside the realized range when a visible-range path is available. Keep fallback behavior for unit tests and non-virtualized callers.

- [ ] **Step 4: Reduce binary serialization churn**

Reuse internal buffers in binary frame writers where profiles show repeated allocations. Keep frame output immutable once queued to the writer.

- [ ] **Step 5: Verify Task 14**

Run:
`dotnet test src-winui/SimpleFile.Tests/SimpleFile.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ListReplaceTests|FullyQualifiedName~EntryPresentationTests"`
`cargo test -p simplefile-service binary --locked`
`npm run build:winui`

Expected: all pass.

### Task 15: Continuous Performance Gates And Final Release Proof

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `package.json`
- Modify: `README.md`
- Modify: `docs/ROADMAP.md`
- Modify: `docs/FEATURE_OPPORTUNITIES.md`
- Modify: `docs/winui-migration/parity-gate.md` if command counts or manual smoke rows change
- Test: release and smoke scripts under `scripts/`

**Interfaces:**
- Produces CI benchmark job on stable fixtures where available.
- Produces local command `npm run check:performance` that validates benchmark JSON and startup timing budgets.
- Consumes all prior task outputs.

- [ ] **Step 1: Add performance gate script**

Wire `npm run check:performance` to `check-winui-startup-budget.mjs` and `check-session-cache-benchmark.mjs`. Keep benchmark thresholds conservative and based on recorded baseline deltas, not absolute developer-machine guesses.

- [ ] **Step 2: Update docs**

Update README Settings and Performance wording from disk thumbnail cache to session cache. Add roadmap note that session cache is RAM-only, optional, and bounded.

- [ ] **Step 3: Run broad local verification**

Run:
`npm run check`
`npm run check:winui`
`npm run check:rust`
`npm run check:performance`
`npm run build:winui`
`git diff --check`

Expected: all pass.

- [ ] **Step 4: Run release-quality verification when implementation is ready to ship**

Run:
`npm run check:release`
`npm run release:build`
`npm run smoke:winui`
`npm run smoke:winui-file-ops`
`npm run smoke:winui-msi`
`npm run smoke:winui-installer`

Expected: all pass, with installer/upgrade smokes skipped only when their documented local prerequisites are unavailable.

## Self-Review

- Spec coverage: Task 1 covers baseline and metrics. Tasks 2, 4, 5, 6, 12, and 13 cover shared cache policy, freshness, invalidation, memory pressure, diagnostics, and user controls. Task 3 covers thumbnail and shell-icon LRU replacement. Tasks 7, 8, 9, 10, 11, and 14 cover progressive loading, scheduling, IPC batching/paging/deltas, demand-driven enrichment, startup/file operation tuning, indexed search, and rendering hygiene. Task 15 covers continuous performance gates and docs.
- Placeholder scan: no task relies on placeholder labels, unspecified edge handling, or undefined method names.
- Type consistency: C# and Rust model names reuse `CachePolicy`, `CacheDiagnostics`, and `CacheDomainDiagnostics`; IPC methods are named once and consumed consistently.
- Scope check: the spec is large enough for multiple PRs. This plan keeps each task independently testable and ordered so early tasks deliver measurement and low-risk UI cache wins before service-wide caching and IPC changes.
- Risk note: tasks that change IPC method counts must update schema-generated files, parity guard expectations, and docs in the same task.
