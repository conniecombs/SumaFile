# Media Process and More Image Formats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SumaFile decodes PSD, PSB, EXR, HDR, JPEG XL, TIFF, SVG, DDS, TGA, QOI, PNM, Krita, OpenRaster and camera RAW itself, for thumbnails and previews, in a separate helper process the file service owns.

**Architecture:** The thumbnail pipeline moves from `simplefile-core` into a leaf crate, `sumafile-imaging`. A new helper binary, `sumafile-media`, holds decoders ported from Oculante and answers `thumbnail` and `preview` requests on its standard input and output. `sumafile-media-client` starts the helper on first use, enforces timeouts and isolates files that crash it. Core gets a `MediaProvider` registry (shaped like `archive::engine`) that the service fills.

**Tech Stack:** Rust 1.98 (`image` 0.25, `fast_image_resize`, `exr`, `jxl-oxide`, `tiff`, `resvg`, `image_dds`, `zip`), C# / WinUI 3, Node scripts for generated code.

**Spec:** `docs/superpowers/specs/2026-11-28-media-process-and-decoders-design.md`

## Global Constraints

- Every added library is permissively licensed. Never add `quickraw`, `dds-rs`, `rawloader`, `rawler` or any LGPL/GPL crate. Never add `psd` (rejects PSB) or the `dicom-*` crates (they pull in the unmaintained `encoding` crate; DICOM is deferred).
- No C or C++ library: `cargo tree -i cc --target x86_64-pc-windows-msvc -p sumafile-media` prints nothing.
- Code ported from Oculante starts with: `// Ported from Oculante (https://github.com/woelper/oculante), Copyright (c) Johann Woelper, MIT licence. See LICENSE-oculante.txt.`
- Nothing is added to startup: the service registers the provider without starting the helper.
- JPG, PNG, GIF, WebP and BMP thumbnails are byte-for-byte what they were: the existing thumbnail tests pass unchanged after the move.
- Numbers from the spec: 4 thumbnails and 2 previews at a time (gated in the client), thumbnail timeout 20 s, preview timeout 60 s, idle exit 120 s, pixel budget 1 GiB, process memory limit 3 GiB, 1,024 remembered bad files, preview long side 2560, header limit 64 KiB, body limit 256 MiB.
- Helper executable name `sumafile-media.exe`; override variable `SUMAFILE_MEDIA_HELPER`.
- Test-first: each test is run and seen to fail before its code is written.
- Commits end with the two attribution lines of this session. Push to `WorkingBranch` only. No pull request.
- Linux checks: `cargo test -p <crate>`; core and service build on Linux only with the scratchpad patch `core-linux-build.patch` applied temporarily (never committed). Windows type-check: scratchpad `xcheck.sh clippy -p <crate>`. C# tests: scratchpad `cstest.sh`.

## Review Focus

1. A file whose extension is right and whose contents are wrong or truncated (a `.psd` that is a JPEG, a cut-off `.exr`): the answer is a `decode` failure and the fallback applies. Test in Task 4 (`wrong_contents_answer_decode`) and per format.
2. A very large picture (30,000 × 30,000 TIFF): the answer is `too_large` before pixels are allocated. Test in Task 4 (`over_budget_answers_too_large`).
3. One file that hangs while others are in flight: the others still get their thumbnails. Test in Task 3 (`a_timeout_retries_the_other_requests_alone`).
4. The helper is missing or cannot start (deleted, blocked by antivirus): every request answers unsupported at once and nothing loops. Test in Task 3 (`a_missing_helper_answers_unsupported`, `a_helper_that_cannot_start_is_not_retried_in_a_loop`).
5. A file recorded as bad that is then changed: it is tried again. Test in Task 3 (`a_bad_file_is_tried_again_after_it_changes`).
6. Paths with non-ASCII characters and long `\\?\` paths cross the pipe unchanged. Test in Task 4 (`a_unicode_path_round_trips`).

---

### Task 1: `sumafile-imaging`, the thumbnail pipeline as a leaf crate

**Files:**
- Create: `crates/sumafile-imaging/Cargo.toml`, `crates/sumafile-imaging/src/lib.rs`
- Move: `crates/simplefile-core/src/thumbnail.rs` → `crates/sumafile-imaging/src/thumbnail.rs` (`git mv`)
- Modify: `Cargo.toml` (workspace member), `crates/simplefile-core/Cargo.toml`, `crates/simplefile-core/src/lib.rs`, `crates/simplefile-core/src/preview.rs:115`

**Interfaces:**
- Produces, all in `sumafile_imaging::thumbnail`:
  - `pub struct Decoded { pub image: DynamicImage, pub full_size: (u32, u32) }`
  - `pub fn render(path: &Path, extension: &str, size: u32) -> Result<Vec<u8>, String>` (unchanged behaviour)
  - `pub fn decode(path: &Path, extension: &str, size: u32) -> Result<Decoded, String>`
  - `pub fn fit(width: u32, height: u32, size: u32) -> (u32, u32)`
  - `pub fn scale(image: &DynamicImage, width: u32, height: u32) -> DynamicImage`
  - `pub fn render_decoded(decoded: &Decoded, size: u32) -> Result<Vec<u8>, String>`: fit, scale, encode JPEG, exactly as `render` did (transparency is dropped, so built-in thumbnails do not change). `render` calls it.
  - `pub fn flatten_onto_white(image: &DynamicImage) -> DynamicImage`: blends each pixel with white by its alpha. The media process calls it before `render_decoded`.

- [ ] **Step 1:** Create the crate with the moved file and `pub` visibility; core depends on it and calls `sumafile_imaging::thumbnail::render`. Remove from core's `Cargo.toml` the dependencies only `thumbnail.rs` used.
- [ ] **Step 2:** Run `cargo test -p sumafile-imaging`. Expected: the moved tests pass.
- [ ] **Step 3:** Write failing tests `render_decoded_fits_the_box_and_keeps_the_shape` (a 400×200 image at size 100 gives a 100×50 JPEG), `render_decoded_takes_the_shape_from_the_full_size`, `flatten_onto_white_puts_transparent_pixels_on_white` and `flatten_onto_white_keeps_an_opaque_picture`.
- [ ] **Step 4:** Run them; expected FAIL (function missing). Implement `render_decoded` and `flatten_onto_white`; make `render` use `render_decoded`.
- [ ] **Step 5:** Run `cargo test -p sumafile-imaging` and, with the Linux patch applied, `cargo test -p simplefile-core preview thumbnail`. Expected: PASS. Remove the patch.
- [ ] **Step 6:** `xcheck.sh clippy -p sumafile-imaging -p simplefile-core -- -D warnings`. Commit.

### Task 2: One format list for Rust and C#

**Files:**
- Create: `ipc/schema/v1/media-formats.json`, `scripts/generate-media-formats.mjs`, `crates/sumafile-imaging/src/formats.rs`, `crates/sumafile-imaging/src/formats_generated.rs` (generated), `src-winui/SimpleFile.Core/MediaFormats.Generated.cs` (generated), `src-winui/SimpleFile.Tests/MediaFormatsTests.cs`
- Modify: `package.json` (`generate:media-formats`, `check:media-formats`, and the aggregate `check`)

**Interfaces:**
- `media-formats.json`: `{ "version": 1, "formats": [ { "name": "Photoshop", "extensions": ["psd", "psb"] }, ... ] }` with exactly the extensions in the spec's table.
- Rust: `pub const MEDIA_PROCESS_EXTENSIONS: &[&str]` (sorted, lowercase) and `pub fn is_media_process_extension(extension: &str) -> bool` (case-insensitive, with or without a leading dot).
- C#: `public static class MediaFormats { public static bool IsMediaProcessFormat(string? pathOrExtension); public static IReadOnlyCollection<string> Extensions { get; } }`
- The generator follows `scripts/generate-open-with-policy.mjs`: `--check` exits non-zero when a generated file is stale.

- [ ] **Step 1:** Write failing tests: Rust `the_list_matches_the_schema_file` (reads the JSON at test time and compares), `lookup_ignores_case_and_a_leading_dot`; C# `MediaFormatsTests.KnowsPsdAndRawButNotJpg`.
- [ ] **Step 2:** Run; expected FAIL. Write the JSON, the generator and `formats.rs`; run the generator.
- [ ] **Step 3:** Run the Rust tests, `node scripts/generate-media-formats.mjs --check`, and `cstest.sh` filtered to `MediaFormatsTests`. Expected: PASS. Commit.

### Task 3: `sumafile-media-client`: wire protocol, process management, fake helper

**Files:**
- Create: `crates/sumafile-media-client/Cargo.toml`, `src/lib.rs`, `src/protocol.rs`, `src/client.rs`, `src/process.rs` (spawn, Windows job object), `src/bin/sumafile-media-fake-helper.rs`, `tests/client.rs`
- Modify: `Cargo.toml` (workspace member)

**Interfaces:**
- `protocol`:
  - `pub const MAX_HEADER_BYTES: usize = 64 * 1024; pub const MAX_BODY_BYTES: usize = 256 * 1024 * 1024;`
  - `pub struct Request { pub id: u64, pub method: Method, pub path: String, pub size: Option<u32>, pub max_dimension: Option<u32> }` (serde, camelCase); `pub enum Method { Thumbnail, Preview }` (lowercase)
  - `pub struct ResponseHeader { pub id: u64, pub ok: bool, pub format: Option<ImageKind>, pub width: Option<u32>, pub height: Option<u32>, pub kind: Option<FailureKind>, pub error: Option<String> }`
  - `pub enum ImageKind { Jpeg, Png }`; `pub enum FailureKind { Unsupported, TooLarge, Decode, Io }` (snake_case)
  - `pub fn write_message<W: Write>(writer: &mut W, header: &impl Serialize, body: &[u8]) -> io::Result<()>`
  - `pub fn read_message<R: Read>(reader: &mut R) -> io::Result<Option<(Vec<u8>, Vec<u8>)>>` (`None` at a clean end of input; an over-limit length is `InvalidData`)
- `client`:
  - `pub struct ClientConfig { pub helper_path: PathBuf, pub thumbnail_timeout: Duration, pub preview_timeout: Duration, pub max_thumbnails: usize, pub max_previews: usize, pub max_bad_files: usize, pub memory_limit_bytes: u64 }` with `ClientConfig::for_helper(path)` filling the spec's numbers; also `idle_exit: Duration` (the client ends an idle helper; the helper has no idle timer) and `spawn_retry_interval: Duration`. `pub fn stats(&self) -> ClientStats { starts, spawn_failures, running, process_id }`. Calls beyond `max_thumbnails` / `max_previews` wait inside the client before the request is sent, so a timeout never counts queueing.
  - `pub fn default_helper_path() -> PathBuf`: `SUMAFILE_MEDIA_HELPER` if set, else `sumafile-media[.exe]` beside `current_exe()`
  - `pub struct MediaClient` (`Send + Sync`); `MediaClient::new(config)`; never spawns in `new`
  - `pub fn thumbnail(&self, path: &Path, size: u32) -> Result<Rendered, MediaError>`
  - `pub fn preview(&self, path: &Path, max_dimension: u32) -> Result<Rendered, MediaError>`
  - `pub struct Rendered { pub kind: ImageKind, pub width: u32, pub height: u32, pub bytes: Vec<u8> }`
  - `pub enum MediaError { Unsupported, TooLarge, Decode(String), Io(String), Failed(String) }` with `Display`
- Rules the client implements (spec section 3): a reader thread per helper generation; replies matched by id; a timeout ends the helper and records that file as bad; when the helper dies with one request in flight that file is bad; with several, each is retried once under an exclusive gate (`RwLock`: ordinary requests share, a retry is exclusive) and a file that fails alone is bad; a bad file is keyed by path, length and modified time, bounded FIFO; a missing helper answers `Unsupported`; a spawn failure answers `Failed` for that request and is tried again on a later one, at most once every 30 seconds.
- Windows spawn: `CREATE_NO_WINDOW | BELOW_NORMAL_PRIORITY_CLASS`, a job object with `KILL_ON_JOB_CLOSE` and `ProcessMemoryLimit` (`windows-sys` features `Win32_System_JobObjects`, `Win32_System_Threading`, `Win32_Foundation`, `Win32_Security`).
- Fake helper (test-only binary, never packaged), scripted by the requested file's name: contains `ok` → success with body `b"OK:" + name`; `slow` → waits 300 ms first; `hang` → never answers; `crash` → `exit(3)`; `toolarge` → `too_large` failure. `FAKE_HELPER_STARTS` names a file it appends one line to at start. `FAKE_HELPER_IDLE_MS` makes it exit after that long idle.

- [ ] **Step 1:** Protocol tests first (`a_message_round_trips`, `an_over_limit_header_is_refused`, `a_clean_end_reads_as_none`); see them fail; implement; pass.
- [ ] **Step 2:** Write the fake helper and the failing client tests in `tests/client.rs`: `replies_are_matched_out_of_order`, `calls_beyond_the_gate_wait_without_timing_out`, `a_timeout_ends_the_helper_and_records_the_file`, `a_timeout_retries_the_other_requests_alone`, `a_crash_with_one_request_records_the_file`, `a_bad_file_answers_without_starting_the_helper`, `a_bad_file_is_tried_again_after_it_changes`, `an_idle_exit_is_followed_by_a_restart`, `a_missing_helper_answers_unsupported`, `a_helper_that_cannot_start_is_not_retried_in_a_loop`, `a_failure_kind_maps_to_its_error`. Tests use timeouts of 1–2 s through `ClientConfig`.
- [ ] **Step 3:** Run; expected FAIL. Implement `client.rs` and `process.rs`.
- [ ] **Step 4:** `cargo test -p sumafile-media-client`; expected PASS, three runs in a row with no flake. `xcheck.sh clippy -p sumafile-media-client -- -D warnings`. Commit.

### Task 4: `sumafile-media`: request loop, render functions, the `image`-crate formats

**Files:**
- Create: `crates/sumafile-media/Cargo.toml`, `src/main.rs`, `src/lib.rs`, `src/server.rs`, `src/render.rs`, `src/decoders/mod.rs`, `src/decoders/simple.rs`, `src/limits.rs`, `tests/helper.rs`, `LICENSE-oculante.txt`
- Modify: `Cargo.toml` (workspace member)

**Interfaces:**
- `limits`: `pub const PIXEL_BUDGET_BYTES: u64 = 1 << 30; pub fn check_budget(width: u32, height: u32, channels: u8, bytes_per_sample: u8) -> Result<(), RenderError>`
- `render`: `pub enum RenderError { Unsupported(String), TooLarge, Decode(String), Io(String) }`; `pub enum Purpose { Thumbnail { size: u32 }, Preview { max_dimension: u32 } }`; `pub fn thumbnail(path: &Path, size: u32) -> Result<Rendered, RenderError>`; `pub fn preview(path: &Path, max_dimension: u32) -> Result<Rendered, RenderError>` (JPEG when opaque, PNG when any pixel is transparent; never enlarges)
- `decoders`: `pub fn decode(path: &Path, extension: &str, purpose: Purpose) -> Result<Decoded, RenderError>` dispatching by extension; this task covers `tga`, `qoi`, `pnm`, `pbm`, `pgm`, `ppm`, `pam`, `hdr` (tone-mapped) through `image`, each checked against the pixel budget from its header before decoding.
- `server`: `pub fn run<R: Read, W: Write + Send + 'static>(input: R, output: W)`: returns when the input ends. Each request runs on its own thread (the client gates how many arrive); a panic in a decoder is caught and answered as a `decode` failure. `main` exits the process as soon as `run` returns.

- [ ] **Step 1:** Failing unit tests per format (`decodes_tga`, `decodes_qoi`, `decodes_ppm`, `decodes_hdr_tone_mapped`), each writing a 4×3 fixture with `image` and checking size and two pixels; `over_budget_answers_too_large` (a PPM header claiming 40,000 × 40,000); `wrong_contents_answer_decode`; `preview_keeps_transparency_as_png`; `preview_never_enlarges`.
- [ ] **Step 2:** See them fail; implement `limits`, `render`, `decoders::simple`.
- [ ] **Step 3:** Failing integration tests in `tests/helper.rs` using `env!("CARGO_BIN_EXE_sumafile-media")` with `MediaClient`: `thumbnail_end_to_end`, `preview_end_to_end`, `a_unicode_path_round_trips`, `the_helper_exits_when_its_input_closes`.
- [ ] **Step 4:** Implement `server.rs` and `main.rs`; pass. `cargo tree -i cc --target x86_64-pc-windows-msvc -p sumafile-media` prints nothing. `xcheck.sh clippy -p sumafile-media -- -D warnings`. Commit.

### Task 5: Ported decoders: EXR, TIFF, JPEG XL, SVG, DDS

**Files:**
- Create: `crates/sumafile-media/src/decoders/{exr.rs,tiff.rs,jxl.rs,svg.rs,dds.rs,tonemap.rs}`, `crates/sumafile-media/tests/fixtures/tiny.jxl` (+ `tiny.jxl.LICENSE`)
- Modify: `crates/sumafile-media/src/decoders/mod.rs`, `Cargo.toml`

**Interfaces:**
- Each module: `pub(super) fn decode(path: &Path, purpose: Purpose) -> Result<Decoded, RenderError>`, with the Oculante credit header where code is ported (EXR, TIFF, tone mapping, JXL, SVG).
- `tonemap`: `pub fn tonemap_f32(px: f32) -> u8` as in Oculante.
- SVG: rendered so its long side equals the purpose's size (thumbnail: `size`; preview: `max_dimension`, never above the SVG's own size × 4); fonts loaded once through a `OnceLock`.

- [ ] **Step 1:** Failing tests: `decodes_exr` (fixture written with `exr`), `decodes_tiff_8_and_16_bit` (written with `tiff`), `decodes_jxl` (committed fixture), `decodes_svg_at_the_requested_size`, `decodes_dds_bc1` (hand-written 4×4 BC1 file), plus `truncated_<format>_answers_decode` for each.
- [ ] **Step 2:** See them fail; port and implement each decoder; pass.
- [ ] **Step 3:** No-C check and clippy as in Task 4. Commit.

### Task 6: Photoshop (own reader), Krita and OpenRaster

**Files:**
- Create: `crates/sumafile-media/src/decoders/{psd.rs,kra.rs}`
- Modify: `decoders/mod.rs`, `Cargo.toml`

**Interfaces:**
- `psd::decode(path, purpose)`: SumaFile's own reader, reading with `Seek` so the layer section is skipped, never loaded. Header (version 1 PSD, version 2 PSB) → colour mode data → image resources → skip layer and mask info (4-byte length in PSD, 8-byte in PSB) → image data (compression 0 raw or 1 RLE; RLE row counts are 2 bytes in PSD, 4 in PSB). Colour modes RGB, greyscale, CMYK and indexed; depth 8 and 16 (high byte). Anything else is `RenderError::Decode`. The pixel budget is checked from the header.
- For `Purpose::Thumbnail { size }` it returns the embedded JPEG of image resource 1036 when its long side is at least `size`; otherwise the flattened image. `pub(super) fn embedded_thumbnail<R: Read + Seek>(reader: &mut R) -> Option<DynamicImage>`.
- `kra::decode`: the entry `mergedimage.png` of the zip, for `kra` and `ora`; the entry's decoded size is checked against the pixel budget.

- [ ] **Step 1:** Failing tests, with files written by a test helper `write_psd(spec)`: `decodes_a_raw_rgb_psd`, `decodes_an_rle_psd`, `decodes_a_psb`, `decodes_greyscale_cmyk_and_indexed`, `decodes_16_bit`, `a_psd_thumbnail_uses_the_embedded_preview` (embedded preview red, flattened image blue; a small thumbnail is red, a larger one and a preview are blue), `an_unsupported_colour_mode_answers_decode`, `a_psd_over_budget_answers_too_large`, `decodes_kra_and_ora` (zip written in the test), and the truncated cases.
- [ ] **Step 2:** See them fail; implement; pass. No-C check, clippy. Commit.

### Task 7: Camera RAW embedded previews

**Files:**
- Create: `crates/sumafile-media/src/decoders/raw/{mod.rs,tiff_walk.rs,raf.rs,cr3.rs}`
- Modify: `decoders/mod.rs`

**Interfaces:**
- `raw::decode(path, purpose)`: collects candidate JPEG byte ranges, decodes the one with the most pixels that decodes, and turns it by the EXIF orientation of IFD0. Size rule: `Thumbnail { size }` needs a long side of at least `size / 2`; `Preview` needs at least 1,000; never enlarged; otherwise `RenderError::Decode("no embedded preview large enough")`.
- `tiff_walk::jpeg_candidates(bytes: &[u8]) -> Vec<Range<usize>>`: the whole IFD chain (IFD0, IFD1, IFD2, ...) and every SubIFD (tag `0x014A`); a candidate is `0x0201`+`0x0202`, or compression 6/7 with one strip (`0x0111`+`0x0117`), or tag `0x002E`; accepts the TIFF magic variants of ORF and RW2; bounded: at most 64 IFDs, every offset checked against the file length.
- `raf::jpeg_candidate(bytes)`: offset and length at bytes 84 and 88 of a `FUJIFILMCCD-RAW` file.
- `cr3::jpeg_candidates(bytes)`: the `PRVW` and `THMB` boxes of the Canon uuid boxes.
- No candidate → `RenderError::Decode("no embedded preview")`.

- [ ] **Step 1:** Failing tests with synthetic containers built in the test: `a_tiff_raw_uses_the_largest_embedded_jpeg` (IFD1 thumbnail 16×16, SubIFD preview 64×48 → 64×48), `a_raw_preview_is_turned_by_its_orientation`, `a_panasonic_jpg_from_raw_is_found`, `a_preview_in_the_third_ifd_is_found`, `a_small_preview_is_refused_for_a_large_thumbnail_and_for_a_preview`, `a_raf_preview_is_found`, `a_cr3_preview_is_found`, `offsets_outside_the_file_are_ignored`, `an_ifd_loop_ends`, `a_raw_without_a_preview_answers_decode`.
- [ ] **Step 2:** See them fail; implement; pass. Clippy. Commit.

### Task 8: Core provider registry and thumbnail routing; the service registers the client

**Files:**
- Create: `crates/simplefile-core/src/media.rs`, `crates/simplefile-service/src/media_provider.rs`
- Modify: `crates/simplefile-core/src/lib.rs`, `crates/simplefile-core/src/preview.rs` (`generate_thumbnail`, the extension table), `crates/simplefile-core/src/models.rs`, `crates/simplefile-service/Cargo.toml`, `crates/simplefile-service/src/lib.rs` (register at session-state creation)

**Interfaces:**
- `simplefile_core::media`:
  - `pub struct EncodedImage { pub format: String /* "jpeg" | "png" */, pub width: u32, pub height: u32, pub data: Vec<u8> }`
  - `pub trait MediaProvider: Send + Sync { fn thumbnail(&self, path: &Path, size: u32) -> Result<Vec<u8>, String>; fn preview(&self, path: &Path, max_dimension: u32) -> Result<EncodedImage, String>; }`
  - `pub fn register_media_provider(provider: Arc<dyn MediaProvider>)`, `pub fn unregister_media_provider()`, `pub fn media_provider() -> Option<Arc<dyn MediaProvider>>`
  - `pub fn render_image_preview(path: String, max_dimension: Option<u32>) -> Result<EncodedImage, String>`: resolves the readable path as `generate_thumbnail` does; a media-process extension with a provider → `provider.preview`; anything else → `Err("Unsupported image format: <ext>")`. Default `max_dimension` 2560, clamped to 64..=8192.
- `generate_thumbnail`: supported = the six built-ins, or a media-process extension while a provider is registered. Cache lookup and write are shared. `pub fn thumbnail_uses_media_process(path: &str) -> bool` tells the service which lane to use.
- `preview.rs` extension table gains `psd`, `psb`, `kra`, `ora` and the RAW extensions of the format list not yet there, all as `"image"`.
- Service: `pub(crate) struct ClientProvider(MediaClient)` implementing the trait; registered once, lazily constructed, never spawning.
- Scheduler: `pub(crate) enum MediaLane { Thumbnail, Preview }` and `pub(crate) async fn run_media<F, R>(&self, lane: MediaLane, task: F) -> Result<R, String>` with its own semaphores (4 and 2); it takes no general or background permit and waits for foreground work like `run_background`. `generate_thumbnail_and_reply` and the batch job use it for media-process paths.

- [ ] **Step 1:** Failing core tests with a counting fake provider: `a_media_extension_goes_to_the_provider`, `a_media_thumbnail_is_cached_on_disk` (second call does not reach the provider), `a_built_in_extension_never_reaches_the_provider`, `without_a_provider_the_extension_is_unsupported`, `render_image_preview_clamps_the_dimension`, `the_preview_table_calls_psd_an_image`.
- [ ] **Step 2:** See them fail; implement; pass (Linux patch applied, then removed).
- [ ] **Step 3:** Failing service tests `the_provider_maps_client_errors_to_messages` and `media_work_does_not_take_background_permits` (with every background permit held, `run_media` still runs); implement `media_provider.rs` and `run_media`; pass. `xcheck.sh clippy -p simplefile-core -p simplefile-service -- -D warnings`. Commit.

### Task 9: IPC method `render_image_preview`

**Files:**
- Modify: `ipc/schema/v1/commands.json`, `types.json`, `protocol.json` (binary tag 10), regenerate with `scripts/generate-ipc-bindings.mjs`; `crates/simplefile-service/src/binary.rs`, `dispatch/mod.rs`, `dispatch/async_ops.rs`, `session/mod.rs`, `session/jobs.rs`; `src-winui/SimpleFile.Ipc/{ISimpleFileIpc.cs,Models.cs,BinaryFrameCodec.cs,NamedPipeJsonClient*.cs}`; the C# fake IPC used by tests

**Interfaces:**
- Schema: `"render_image_preview": { "params": { "path": "string", "maxDimension": "number?" }, "result": "ImagePreview", "binaryFrameTag": 10 }`; type `ImagePreview { format, width, height, data }`.
- Binary frame 10: `i32 request id | string format | u32 width | u32 height | bytes data`. JSON form: `data` is base64.
- Rust: `pub(crate) fn encode_image_preview_result(request_id: i32, preview: &EncodedImage) -> EncodeResult<Vec<u8>>`; the job runs with `run_media(MediaLane::Preview, ...)`.
- C#: `Task<ImagePreview> RenderImagePreviewAsync(string path, uint maxDimension, CancellationToken ct = default)`; `public sealed class ImagePreview { public string Format; public uint Width; public uint Height; public byte[] Data; }`

- [ ] **Step 1:** Failing tests: Rust `image_preview_frame_layout` (golden bytes) and a dispatch test `render_image_preview_answers_through_the_provider`; C# `BinaryFrameCodecTests.DecodesImagePreview` with the same golden bytes.
- [ ] **Step 2:** See them fail; change the schema, regenerate, implement both sides; pass. `node scripts/check-ipc-schema.mjs` and `npm run check:ipc-generated` pass. Commit.

### Task 10: App: thumbnails and previews use the new formats

**Files:**
- Create: `src-winui/SimpleFile.Core/ThumbnailRouting.cs`, `src-winui/SimpleFile.Tests/ThumbnailRoutingTests.cs`
- Modify: `src-winui/SimpleFile.App/FileListThumbnailHost.cs` (`CanUseAppThumbnail`), `src-winui/SimpleFile.App/PreviewPresenter.Image.cs`, the C# preview extension lists that classify images

**Interfaces:**
- `public static class ThumbnailRouting { public static bool CanUseAppThumbnail(string extension); public static bool UsesMediaProcess(string extension); public static bool PreviewsThroughService(string? path); public const uint PreviewMaxDimension = 2560; }` (`CanUseAppThumbnail`: the six built-ins or `MediaFormats.IsMediaProcessFormat`; `PreviewsThroughService`: a media-process format other than `svg`).
- `FileListThumbnailHost`: a third gate, `MediaLoadGate = new(2, 2)`, used when `UsesMediaProcess(extension)`; video keeps `VideoLoadGate`, everything else `LoadGate`.
- `PreviewPresenter.Image`: when `PreviewsThroughService(path)`, call `RenderImagePreviewAsync` first and show the bytes; on any failure continue with the existing Windows path.

- [ ] **Step 1:** Failing tests `ThumbnailRoutingTests`: `AppThumbnailsCoverBuiltInsAndMediaFormats`, `HeicStaysWithWindows`, `PsdPreviewsThroughTheService`, `JpgDoesNotPreviewThroughTheService`, `SvgKeepsItsVectorPreview`, `OnlyMediaFormatsUseTheMediaGate`.
- [ ] **Step 2:** See them fail; implement; pass with `cstest.sh`. Type-check the App project with the scratchpad `appcheck`. Commit.

### Task 11: Packaging, licence text, documentation, full checks

**Files:**
- Modify: `scripts/build-winui-release.ps1`, `scripts/check-winui-packaging.mjs`, the smoke scripts that list payload files (`smoke-winui-msi.ps1`, `smoke-winui-nsis.ps1`, `smoke-winui-file-ops.ps1`), installer definitions that list files, `docs/ARCHITECTURE.md`, `README.md`, `docs/CHANGELOG.md`, `docs/GPU Acceleration.md` (status note pointing at the two designs)

- [ ] **Step 1:** Failing check: extend `scripts/check-winui-packaging.mjs` (and its test, if it has one) to require `sumafile-media.exe` in the payload list and `LICENSE-oculante.txt` in the licence files; see it fail.
- [ ] **Step 2:** Update the release script (build `-p sumafile-media --release`, copy beside the service, stop-process list), the smoke scripts and installer definitions; the check passes.
- [ ] **Step 3:** Documentation: architecture (the media process, the three crates, the wire format, the limits), README format list, changelog.
- [ ] **Step 4:** Full verification: `cargo test` for the four touched Rust crates and the service (Linux patch), `xcheck.sh clippy --workspace -- -D warnings`, `npm run check`, `cstest.sh` full run compared with the baseline failures. Commit and push.

## Execution

Native: one session implements the tasks in order, test-first, committing after each, then a fresh reviewer reads the whole branch.
