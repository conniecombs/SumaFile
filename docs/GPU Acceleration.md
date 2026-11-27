# GPU Acceleration

**Status:** Proposed engineering design; no implementation or measured speedup is implied.
**Scope:** SumaFile Windows x64, C#/WinUI 3 frontend, Rust service and optional plugin backends.
**Goal:** Improve perceived responsiveness and media throughput without compromising file integrity, startup time, memory use, or stability.

## 1. Executive decision

Use GPU acceleration selectively for media decoding, image processing, preview rendering and optional image analysis. Keep ordinary filesystem operations on the CPU and storage stack. WinUI already uses hardware-backed composition where supported; do not duplicate existing acceleration. Preserve CPU fallback and make additional acceleration lazy, bounded, cancellable and optional.

| Workload | Potential | Preferred approach |
|---|---|---|
| Video thumbnails, seeking, playback | High | Media Foundation hardware decoding, D3D11 |
| Large image previews, zoom, rotation | High | Win2D/Direct2D or existing composition |
| Batch image resizing, conversion | High when compute-bound | wgpu compute, CPU codecs |
| Image thumbnails | Conditional | Adaptive CPU/GPU batching |
| RAW preview and tone mapping | High when compute-bound | CPU RAW decode, GPU demosaic/tone mapping where validated |
| Animated GIF/WebP/APNG | Conditional | CPU decode, GPU compositing |
| PDF previews | Conditional | Existing PDF renderer; accelerate display, not arbitrary parsing |
| Visual duplicate detection | High for large collections | CPU perceptual hashes; GPU embeddings or batched transforms |
| Semantic media search | High for optional inference | ONNX Runtime/DirectML or supported Windows inference runtime |
| OCR | Conditional | Existing OS OCR or optional inference provider |
| Checksums | Low/conditional | CPU SHA extensions, SIMD, parallel independent files |
| Archive compression | Low/conditional | Existing CPU codecs and parallelism first |
| Directory enumeration, sorting, metadata, Git | Low | CPU algorithms and caching |
| File copy/move, SMB, FTP/SFTP | Negligible | Native storage/network optimizations |

These are engineering hypotheses, not measured improvements.

## 2. Baseline and discovery

Before adding dependencies, inspect the current WorkingBranch implementation of Rust thumbnail generation, binary thumbnail IPC, C# VideoThumbnailExtractor, Quick Look, ShellIconLoader, listing virtualization and cache ownership. Determine whether Windows codecs, MediaPlayer, composition or the shell already use hardware acceleration. Capture cold/warm startup, first visible thumbnail, p50/p95/p99 preview latency, CPU time, GPU engines, VRAM/shared RAM, disk reads and UI frame latency. Use ETW/WPR/WPA, GPUView, PIX where appropriate, .NET allocation profiling and Rust tracing. Separate decode, resize, encode, IPC, upload and presentation costs. Do not assume that a GPU kernel benchmark predicts end-to-end performance.

## 3. Technology choices

- Media Foundation and D3D11/DXGI: preferred for hardware video decode, frame extraction, color conversion and Windows texture interoperability. Probe codecs and profiles at runtime; software fallback for unsupported media, missing Media Feature Pack, remote sessions and driver failures.
- Win2D/Direct2D and Windows Composition: preferred for UI-local image scaling, rotation, overlays and smooth preview interaction. Verify compatibility with the pinned Windows App SDK.
- wgpu/WGSL: optional Rust compute backend for batched image transforms and analysis. Prefer DX12 on Windows where supported; test Vulkan only if shipped and justified. No adapter initialization on normal startup. Avoid requiring discrete graphics.
- DirectML/ONNX Runtime: optional local inference provider for embeddings, OCR or classification; assess current runtime support, model licensing, download size and CPU fallback before adoption.
- CUDA: optional specialist backend only if a measured need exists; never a mandatory NVIDIA dependency.
- OpenCL/Vulkan/D3D12 directly: consider only if abstraction limitations or profiling justify additional maintenance.
- CPU: keep existing codecs, SIMD, Rayon or bounded workers; GPU does not inherently accelerate JPEG/PNG decoding or encoding.

## 4. Proposed architecture

Keep the WinUI rendering and playback path in the UI process where native texture interoperability is simplest. Add an optional media compute backend for isolated bulk processing, using the existing plugin lifecycle and IPC patterns. A new media-provider contract may be required; do not assume the archive plugin contract supports media.

```text
WinUI / Quick Look / visible thumbnail scheduler
  |-- UI-local Media Foundation / D3D / Win2D
  |-- existing Rust service and CPU thumbnail provider
  |-- optional media provider (plugin backend)
        |-- bounded job queue and cancellation
        |-- device / adapter manager
        |-- CPU fallback
        |-- wgpu compute / optional inference
        |-- resource accounting and diagnostics
```

Suggested layout: plugins/media-accel/plugin.json, backend/Cargo.toml, backend/src/{main,device,scheduler,resources,thumbnails,image_compute,similarity,fallback}.rs, tests/. Keep reusable CPU/GPU-independent media contracts in a leaf crate if justified. Do not introduce circular dependencies. UI integration should use the existing C# service abstractions and generated IPC schema.

### IPC and ownership

Pass file paths or validated handles, requested dimensions, orientation, color profile intent, request IDs, generation IDs and cancellation tokens. Return bounded thumbnail payloads using existing binary framing where practical. Avoid base64 or full-resolution pixel buffers over JSON. Prioritize terminal events over coalescible progress. Reject stale results after navigation, file changes or cancellation. For zero-copy cross-process textures, evaluate DXGI shared handles, adapter LUID matching, synchronization fences/keyed mutexes, ACLs, lifetime and device-loss recovery; this is an advanced optional optimization, not a prerequisite. Never expose raw pointers across processes. Avoid GPU readback followed by a second upload when UI-local processing can eliminate both.

## 5. Media pipelines

### Thumbnails

Read only needed source data; use embedded EXIF/RAW thumbnails or shell cache when appropriate. Decode at target resolution when supported. Apply EXIF orientation, aspect-preserving resize, alpha handling and ICC conversion. CPU handles small or isolated requests; GPU batches large decoded images when transfer overhead is amortized. Use bounded staging buffers, reusable textures, asynchronous submission and cancellation. Prioritize visible rows, then near-viewport prefetch; cancel obsolete work on navigation. Cache key includes file identity, size, modification state, requested dimensions, orientation, color policy and algorithm version. Avoid caching failed or partial results indefinitely.

### Video

Use Media Foundation source reader or suitable Windows media API with hardware transforms and D3D device manager where supported. Seek to representative frames, account for keyframe distance, variable frame rates, rotation, pixel aspect ratio and HDR/SDR conversion. Limit simultaneous decoder sessions. Decode only required frames; reuse sessions for filmstrips and timeline scrubbing. Test H.264, HEVC, VP9, AV1 and common containers; codec availability depends on OS, hardware and installed extensions. Never claim universal hardware support. For unsupported formats use existing software paths. Optional future features: hover previews, contact sheets, frame extraction and timeline thumbnails. Respect power and accessibility preferences; no autoplay by default.

### Quick Look and image processing

Use existing GPU-backed composition first. Consider UI-local textures for smooth zoom, pan, rotation, fit-to-window, tiled large-image display and mipmaps. Support ICC-aware display, correct premultiplied alpha and HDR tone mapping; compare against CPU reference. Avoid full-size allocations for oversized images. Optional batch conversion, resize, crop, rotation, contact sheets and histogram generation may use compute shaders; encoding stays CPU unless a compatible hardware encoder is explicitly selected. RAW processing must preserve metadata and distinguish embedded preview from developed image. PDF parsing/rasterization stays with existing renderer unless a measured alternative is available. Text, archive listings and source previews do not need GPU compute.

### Similarity and search

Stage 1: exact duplicate detection by size, hashes and byte verification as appropriate. Stage 2: CPU dHash/pHash and indexed Hamming-distance search. Stage 3: optional GPU-batched perceptual transforms or local image embeddings, with model/version-specific cache and approximate nearest-neighbor index. Support resized/recompressed images, screenshots and optional video-frame similarity; cropped-image matching needs suitable algorithms. Similarity is not identity: never automatically delete files based on similarity. Optional OCR and semantic search must be opt-in, local by default, with clear index storage/deletion controls and no silent uploads.

## 6. Resource management and caching

Three tiers: persistent disk thumbnail cache, bounded decoded RAM cache, and optional GPU-resident visible/recent texture cache. Avoid duplicating WinUI-managed textures. RGBA8 256x256 costs 256 KiB per texture before overhead; 128 MiB theoretically holds 512 such textures. 512x512 costs 1 MiB. Mipmaps, row alignment, intermediates and driver allocations add overhead.

Use DXGI budget telemetry where available, not installed VRAM alone. Account for integrated GPUs sharing system memory. Start with conservative experimental limits (e.g. 64–128 MiB texture cache, 1–2 video sessions, small bounded compute batches); tune from measurements. Track resident textures, staging/readback buffers, decoder surfaces, inference weights and temporary resources. Use LRU eviction with visible-item protection, shrink under pressure, and release idle resources. Coordinate with transfer and archive CPU/RAM budgets across processes; existing per-process limits are not global limits. Avoid GPU work during battery saver or heavy foreground contention when configured. Never reserve large VRAM on startup.

Scheduler: foreground preview > visible thumbnails > near-viewport prefetch > user-requested batch work > background indexing. Apply backpressure, fair per-pane scheduling, deduplication of identical requests, bounded queue sizes and timeouts. Coalesce progress updates; deliver completion reliably. Handle GPU device removal, out-of-memory, shader validation failure, suspend/resume, adapter change and plugin crash with bounded retries and CPU fallback. Do not retry indefinitely.

## 7. Checksums, compression and storage

SHA-256 single-file hashing has sequential block dependencies; GPUs are better suited to many independent hashes. CPU SHA extensions, SIMD, buffer reuse and parallel file hashing should be measured first. BLAKE3 is a separate optional algorithm, not a silent substitute for SHA-256. GPU verification must preserve exact digest semantics and never weaken integrity checks.

RAR, LZMA2 and Deflate have irregular dictionary workloads and dependencies. Prefer rars/other archive engine CPU parallelism and memory limits. GPU compression for LZ4/Zstandard or specialized Deflate may be evaluated only if output is interoperable, resource use is bounded and end-to-end performance improves. Do not invent a GPU RAR encoder as part of this project. TAR/store operations need no compute acceleration.

Do not route ordinary file copies through VRAM. Prioritize overlapped I/O, IOCP, CopyFile2, device-aware scheduling, ReFS block cloning, SMB server-side copy and ODX where supported, with safe fallback and existing staging/recovery semantics. DirectStorage is not a general-purpose file-copy API. No GPU path may bypass transfer integrity or cancellation policies.

## 8. Settings and UX

Proposed settings: acceleration Off/Auto/On (Auto default only after validation), hardware video decoding toggle, optional GPU compute toggle, adapter preference (system default first), cache budget Auto/64/128/256 MiB/custom, background processing and battery policy, diagnostics showing active backend and fallback reason. On means prefer supported hardware, not fail when unavailable. Keep advanced controls collapsed. Provide reset and clear cache. Avoid intrusive GPU warnings; report failures once and fall back. Expose actual active backend rather than claiming acceleration based solely on configuration.

## 9. Security, correctness and privacy

Treat media files, metadata, shaders and models as untrusted. Enforce dimensions, decoded byte limits, frame counts, duration, recursion and execution budgets; use checked arithmetic. Test malformed codecs, decompression bombs, truncated media, hostile paths and reparse points. Use maintained codecs and dependency scanning. No arbitrary user-supplied shaders in first release. Validate IPC paths and payloads, redact sensitive paths in logs, avoid remote model downloads without consent. Plugin process isolation is not a sandbox; evaluate restricted worker tokens or OS sandboxing for untrusted decoding separately. Preserve source files; batch conversion writes staged outputs and never silently overwrites originals. Compare GPU output to CPU reference with defined tolerances; exact hashes require exact equality. Handle orientation, alpha, ICC, HDR and animation correctly.

## 10. Testing and acceptance

Unit tests: scheduler priorities, cache eviction, byte accounting, checked dimensions, cancellation, stale generations, fallback, device-loss simulation, IPC schema and resource limits. Golden image tests: orientation, alpha, color profiles, scaling, HDR tone mapping and corrupted input. Compare CPU/GPU results with documented per-operation tolerances. Integration tests: rapid folder navigation, dual panes, concurrent transfers, plugin disable/uninstall, no GPU, software adapter, low VRAM, remote desktop, suspend/resume and driver reset. Hardware matrix: Intel integrated, AMD integrated/discrete, NVIDIA discrete, Windows 10 2004+ where supported and Windows 11; document unsupported combinations. Test release packaging and offline startup. Use software GPU tests in CI only as correctness checks, not hardware performance evidence.

Benchmark workloads: 100/1,000/10,000 JPEGs, 24/48/100 MP images, PNG with alpha, RAW files, 1080p/4K/8K video, mixed media on HDD/SSD/NVMe/NAS, repeated warm browsing, simultaneous transfer and preview, large image similarity scan. Measure cold/warm p50/p95/p99 latency, time to first visible thumbnail, thumbnails/sec, CPU utilization, peak RAM/VRAM, GPU busy time, disk reads, IPC bytes, power and cancellation latency. Compare CPU baseline, existing OS acceleration and proposed backend on identical hardware and data. Record driver, codec, build, cache state and settings.

Proposed release gates (subject to baseline calibration): no correctness regression; no startup GPU initialization; no unbounded resource growth; CPU fallback passes all existing workflows; no meaningful regression in non-media browsing or concurrent transfers; measurable end-to-end improvement for target workloads (initial target >=20% p95 latency reduction or materially lower CPU use at equal latency). Reject GPU paths that are slower for common workloads. No fabricated speedup claims.

## 11. Implementation roadmap

1. Baseline and audit: inspect current code, identify existing hardware paths, add tracing and reproducible benchmark fixtures.
2. Native video: improve hardware decoding and frame extraction in C#; add fallback and codec/device tests.
3. Quick Look: optimize existing composition, image scaling and texture lifetime; measure interaction latency.
4. Thumbnail scheduling: visible-first batching, cancellation, RAM/disk cache accounting; implement CPU improvements before GPU.
5. Experimental wgpu provider: lazy adapter initialization, bounded compute, CPU reference parity, resource telemetry and fallback; add media-provider IPC contract if needed.
6. Optional GPU cache: only if profiling shows repeated texture uploads are costly; prefer UI-local ownership.
7. Optional visual similarity and inference plugin: CPU baseline, GPU batching, privacy controls and index lifecycle.
8. Stabilization: hardware matrix, stress tests, security review, dependency/license inventory, packaging, user documentation and opt-in rollout.
9. Promote Auto default only after evidence; retain kill switch and rollback path.

## 12. Alternatives and non-goals

Do not replace WinUI, rewrite the Rust transfer engine, add mandatory CUDA, create a full image editor, or introduce GPU acceleration merely for marketing. Do not add a GPU dependency to the core service unless measurements justify it. Prefer existing OS acceleration and CPU optimization when simpler. True data virtualization, adaptive RAM caching, native asynchronous storage I/O and transfer safety remain higher overall priorities for ordinary file management.

## 13. References

- [SumaFile architecture](ARCHITECTURE.md)
- [Windows Media Foundation](https://learn.microsoft.com/en-us/windows/win32/medfound/microsoft-media-foundation-sdk)
- [Direct3D 11 video APIs](https://learn.microsoft.com/en-us/windows/win32/medfound/direct3d-11-video-apis)
- [DXGI video memory budget](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo)
- [Win2D](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/)
- [wgpu](https://github.com/gfx-rs/wgpu)
- [ONNX Runtime DirectML](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
- [CopyFile2](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-copyfile2)

API choices and proposed thresholds require validation against the actual pinned dependencies and supported Windows versions. This document is a design proposal, not a claim that the described functionality is implemented.
