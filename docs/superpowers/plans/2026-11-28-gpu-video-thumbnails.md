# GPU Video Thumbnails Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Video thumbnails are decoded by Windows' hardware decoder in a helper program of their own, kept in the disk thumbnail cache, with one Settings switch and a truthful count of what the graphics card decoded.

**Architecture:** A new program, `sumafile-video`, answers a `video_thumbnail` request on the wire format `sumafile-media` already speaks. It reads frames with Media Foundation's source reader in the decoder's own pixel format, converts and scales only the chosen frame with Windows' video processor, and reports `hardware` only when that frame arrived as a Direct3D texture. The file service owns it through a second `MediaClient`, routes video extensions to it from `generate_thumbnail`, and counts decodes for a new `get_media_status` request. The app sends the frame position, queues local and network videos apart, and shows the counts in Settings.

**Tech Stack:** Rust (`windows` 0.62: Media Foundation, Direct3D 11, DXGI), the existing `sumafile-media-client`, `sumafile-imaging` and `sumafile-media` crates, C# / WinUI 3, the IPC schema generators.

**Spec:** `docs/superpowers/specs/2026-11-28-gpu-video-thumbnails-design.md`. Read it first; this plan argues from it.

## Global Constraints

- Work on `WorkingBranch`. Push there. No pull request. No force-push, no rebase of pushed commits.
- Test-first: every behaviour change has a test that is seen failing before the code is written.
- Windows-only code cannot run in the development container. Type-check it with
  `xcheck.sh clippy --workspace --locked --all-targets --all-features -- -D warnings` (Windows GNU target). It is proven by the user's CI run and the user's PC check.
- No Media Foundation, Direct3D or COM call before the first video request. Nothing stays open while nothing is decoded.
- `sumafile-media` (the picture program) must not depend on `windows` or on `sumafile-video`: it must start on a Windows without Media Foundation.
- No new library except a direct dependency on `windows = "0.62"` (already in `Cargo.lock`) in `sumafile-video` only. No LGPL/GPL, no C/C++.
- Wire names, verbatim: method `video_thumbnail`; request fields `positionPercent`, `hardware`; reply fields `decoder` (`hardware` | `software`) and `reason` (`turned_off` | `no_device` | `no_hardware_decoder` | `hardware_failed`).
- Numbers, verbatim: position 1 to 99, default 25, at most 250 ms before the end; stepping forward stops after 3 seconds of real time; request timeout 30 s; four video requests at a time (two local list, one network list, one preview pane); preview-pane frame picture 144 pixels; setting key `videoThumbnailsUseGpu`, default true.
- Never raise the 50 ms startup budget. No model or tool names in code, comments, docs or commits.
- Before every commit: `cargo fmt --all`; `npm run check`; `npm run test:maintenance`.

## Review Focus

Failure modes the spec implies and no listed test covered; each has its test in the task named.

1. **A file with a video extension and no video stream** (an audio file renamed, a container Windows cannot parse): answers `decode` at once, no hang. Task 5, `a_file_without_a_video_stream_answers_decode`.
2. **The stream ends before the asked position** (wrong or missing duration, as in `.ts` and `.mts`): the last frame decoded is used; with no frame at all, `decode`. Task 4, `choose_frame` tests.
3. **An empty file, or one still being written:** `decode` at once; a later, complete version of the file is tried again (the bad-file list and the cache key both hold the size and time). Task 5, `an_empty_file_answers_decode`.
4. **A path outside the ANSI code page:** thumbnail as usual. Task 5, `a_unicode_path_gets_its_thumbnail`.
5. **Settings asks for the status while a helper is starting or hung:** it answers at once from the service's own counts and never waits on a client. Task 7, `the_status_is_answered_without_asking_a_client`.

Known unknowns, settled only on the user's PC: the turn direction of `MF_MT_VIDEO_ROTATION` (the plan turns the frame clockwise by its value; if a phone video shows sideways, the direction is flipped in `rules::Turn::from_degrees`), and whether the video processor, given a device, takes a frame held in ordinary memory (if not, the second attempt covers it and reports `hardware_failed`).

---

### Task 1: One video extension list for Rust and C#

**Files:**
- Modify: `ipc/schema/v1/media-formats.json`, `scripts/generate-media-formats.mjs`
- Modify (generated): `crates/sumafile-imaging/src/formats_generated.rs`, `src-winui/SimpleFile.Core/MediaFormats.Generated.cs`
- Modify: `crates/sumafile-imaging/src/formats.rs`, `src-winui/SimpleFile.Core/ParityFeatures.cs:208-237`
- Test: `crates/sumafile-imaging/src/formats.rs` (tests), `src-winui/SimpleFile.Tests/MediaFormatsTests.cs`

**Interfaces:**
- Produces (Rust): `sumafile_imaging::formats::is_video_extension(extension: &str) -> bool` (lowercase, no dot), backed by generated `VIDEO_EXTENSIONS: &[&str]` (sorted).
- Produces (C#): `MediaFormats.VideoExtensions` (`IReadOnlyList<string>`), `MediaFormats.IsVideoFormat(string? nameOrExtension)`. `MediaFolder.IsVideo` returns `MediaFormats.IsVideoFormat(...)`; its private `VideoExtensions` set is deleted.

- [ ] **Step 1:** Add `"video": [...]` to `media-formats.json` with exactly the 30 extensions `MediaFolder.VideoExtensions` holds today (`mp4 m4v mov qt webm mkv mk3d avi wmv asf mpg mpeg mpe m2v m2ts mts vob flv f4v 3gp 3g2 ogv divx mxf rm rmvb h264 h265 hevc y4m`).
- [ ] **Step 2:** Write failing tests: Rust `the_video_list_is_sorted_and_holds_what_the_schema_lists` (30 entries, `is_video_extension("mp4")`, not `"psd"`, not `"MP4"`); C# `TheVideoListIsTheOneMediaFolderUsed` (the 30 names, in a literal array in the test, equal `MediaFormats.VideoExtensions` as a set) and `MediaFolderReadsTheGeneratedVideoList` (`MediaFolder.IsVideo("clip.M2TS")`, not `"app.ts"`).
- [ ] **Step 3:** Run them; expected FAIL (members missing).
- [ ] **Step 4:** Extend the generator: validate `video` as it validates format extensions (`/^[a-z0-9]+$/u`, no duplicates, none also in `formats`), emit both outputs, keep `--check`. Run `npm run generate:media-formats`. Implement `is_video_extension` and point `MediaFolder.IsVideo` at the generated list.
- [ ] **Step 5:** Run: `cargo test -p sumafile-imaging`, `cstest.sh <out> "FullyQualifiedName~MediaFormatsTests|FullyQualifiedName~PhotoFolderTests|FullyQualifiedName~PreviewCapabilitiesTests"`, `npm run check`. Expected: PASS.
- [ ] **Step 6:** Commit: `One video extension list for the service and the app`.

### Task 2: Wire format and client: `video_thumbnail`

**Files:**
- Modify: `crates/sumafile-media-client/src/protocol.rs`, `src/client.rs`, `src/lib.rs`, `src/bin/sumafile-media-fake-helper.rs`
- Test: `crates/sumafile-media-client/src/protocol.rs` (tests), `crates/sumafile-media-client/tests/client.rs`

**Interfaces (produces):**
```rust
// protocol.rs
pub enum Method { Thumbnail, Preview, #[serde(rename = "video_thumbnail")] VideoThumbnail }
pub struct Request { /* existing */ pub position_percent: Option<u8>, pub hardware: Option<bool> } // optional, omitted when None
#[serde(rename_all = "lowercase")]  pub enum Decoder { Hardware, Software }
#[serde(rename_all = "snake_case")] pub enum SoftwareReason { TurnedOff, NoDevice, NoHardwareDecoder, HardwareFailed }
pub struct ResponseHeader { /* existing */ pub decoder: Option<Decoder>, pub reason: Option<SoftwareReason> } // omitted when None
impl ResponseHeader { pub fn video(id: u64, width: u32, height: u32, decoder: Decoder, reason: Option<SoftwareReason>) -> Self } // format jpeg
// client.rs
pub struct VideoRendered { pub rendered: Rendered, pub decoder: Decoder, pub reason: Option<SoftwareReason> }
impl MediaClient { pub fn video_thumbnail(&self, path: &Path, size: u32, position_percent: u8, hardware: bool) -> Result<VideoRendered, MediaError> }
pub struct ClientConfig { /* existing */ pub video_timeout: Duration /* 30 s */, pub max_videos: usize /* 4 */ }
pub const VIDEO_HELPER_FILE_NAME: &str;            // "sumafile-video.exe" on Windows, "sumafile-video" elsewhere
pub const VIDEO_HELPER_PATH_VARIABLE: &str = "SUMAFILE_VIDEO_HELPER";
pub fn default_video_helper_path() -> PathBuf;     // the variable, else beside the running program
```
A successful reply to a video request without `decoder` is `MediaError::Failed` ("a reply SumaFile does not understand"). Video requests use a third gate (`max_videos`) and `video_timeout`; timeouts, crashes, bad files and failed starts follow the existing rules unchanged.

- [ ] **Step 1:** Failing protocol test `a_video_request_and_its_reply_have_these_words`: the request serializes to `{"id":7,"method":"video_thumbnail","path":"C:\\v\\a.mp4","size":256,"positionPercent":25,"hardware":true}`; `ResponseHeader::video(7, 256, 144, Decoder::Software, Some(SoftwareReason::NoHardwareDecoder))` to `{"id":7,"ok":true,"format":"jpeg","width":256,"height":144,"decoder":"software","reason":"no_hardware_decoder"}`; a thumbnail request still serializes without the two new fields.
- [ ] **Step 2:** Teach the fake helper the method: success with width = height = `size`, body `OK:<file name>@<positionPercent>`; `gpu` in the name answers `hardware`; otherwise `software` with `turned_off` when `hardware` is false, else by name: `nodevice`, `failedhw`, default `no_hardware_decoder`. The other name scripts (`crash`, `hang`, `slow<ms>`, failures) apply as for pictures.
- [ ] **Step 3:** Failing client tests:
  - `a_video_thumbnail_comes_back_with_who_decoded_it` (`gpu-a.mp4` → `Decoder::Hardware`, `reason == None`, body `OK:gpu-a.mp4@40`; `b.mp4` with `hardware: false` → `Software`, `TurnedOff`).
  - `videos_wait_in_a_gate_of_their_own` (`max_thumbnails = 1`, `max_videos = 1`: with `slow600-a.x` in flight as a thumbnail, a video call returns first).
  - `a_video_that_takes_too_long_is_recorded_as_bad` (`video_timeout = 300 ms`, `hang-a.mp4`: `Failed`, then `Unsupported` without a start).
  - `one_clients_video_timeout_leaves_another_clients_picture_running` (two clients on the fake helper: client A times out on `hang-a.mp4`; client B's `slow600-b.x` answers normally and `B.stats().starts == 1`).
- [ ] **Step 4:** Run; expected FAIL (members missing). Implement. Run `cargo test -p sumafile-media-client`; expected PASS (all earlier tests too).
- [ ] **Step 5:** Commit: `Media client: the video_thumbnail request`.

### Task 3: The request loop carries who decoded a frame

**Files:**
- Modify: `crates/sumafile-media/src/server.rs`
- Test: same file (tests)

**Interfaces (produces):**
```rust
/// What a handler answers with: the picture, and for a video frame who decoded it.
pub struct Answered { pub rendered: Rendered, pub video: Option<(Decoder, Option<SoftwareReason>)> }
impl From<Rendered> for Answered            // video: None
pub fn run<R, W, H, A>(input: R, output: W, handler: H)
where H: Fn(&Request) -> Result<A, RenderError> + Send + Sync + 'static, A: Into<Answered>, /* R, W as today */
```
`sumafile-media`'s `main.rs` and `render::handle` do not change.

- [ ] **Step 1:** Failing test `a_video_answer_names_its_decoder`: a handler returning `Answered { video: Some((Decoder::Hardware, None)), .. }` produces a reply header with `decoder == Some(Hardware)`, `reason == None`, `format == Some(Jpeg)`; a handler returning a plain `Rendered` produces a header without either.
- [ ] **Step 2:** Run; expected FAIL. Implement (`answer` builds `ResponseHeader::video` when `video` is set). Run `cargo test -p sumafile-media`; expected PASS (78 + 8 earlier tests).
- [ ] **Step 3:** Commit: `Media process: a reply can say who decoded a frame`.

### Task 4: `sumafile-video`: the crate and its platform-neutral rules

**Files:**
- Create: `crates/sumafile-video/Cargo.toml`, `src/lib.rs`, `src/main.rs`, `src/rules.rs`, `src/pixels.rs`, `src/attempt.rs`
- Modify: root `Cargo.toml` (workspace member), `Cargo.lock`
- Create: `scripts/tests/video-program.test.mjs`
- Test: unit tests in each new module

`Cargo.toml`: `version = "1.0.0"`, `publish = false`, MIT, `[[bin]] name = "sumafile-video"`. Dependencies: `sumafile-imaging`, `sumafile-media-client`, `sumafile-media` (for `server::run`, `server::Answered`, `render::RenderError`), `image` (as `sumafile-media` pins it). Windows-only: `windows = { version = "0.62", features = [...] }`, added in Task 5.

**Interfaces (produces):**
```rust
// lib.rs
pub fn handle(request: &Request) -> Result<Answered, RenderError>;   // video_thumbnail only; any other method: Unsupported
// On a system other than Windows, video_thumbnail answers RenderError::Unsupported("Video thumbnails need Windows.")

// rules.rs — times are in units of 100 nanoseconds, as Media Foundation counts them
pub const STEPPING_LIMIT: Duration = Duration::from_secs(3);
pub fn position_for(duration: u64, percent: u8) -> u64;
pub fn processor_size(frame: (u32, u32), pixel_aspect: (u32, u32), box_side: u32) -> (u32, u32);
#[derive(Debug, Clone, Copy, PartialEq, Eq)] pub enum Turn { None, Quarter, Half, ThreeQuarters }   // clockwise
impl Turn { pub fn from_degrees(degrees: u32) -> Turn }             // 90 → Quarter, 180 → Half, 270 → ThreeQuarters, else None
pub fn choose_frame<F, E>(
    next: impl FnMut() -> Result<Option<(i64, F)>, E>,   // the next frame and its time; None at the end of the stream
    target: i64,
    elapsed: impl FnMut() -> Duration,                    // since stepping began
) -> Result<Option<F>, E>;

// pixels.rs
/// `rows` are 32-bit pixels in the order blue, green, red, unused. `stride` is the bytes from one row to the
/// next: positive when the first row in `rows` is the top one, negative when it is the bottom one.
pub fn upright_rgb(rows: &[u8], stride: i32, width: u32, height: u32, turn: Turn) -> Option<image::RgbImage>;

// attempt.rs
#[derive(Clone, Copy, PartialEq, Eq, Debug)] pub enum Device { Ready, Missing }
#[derive(Clone, Copy, PartialEq, Eq, Debug)] pub enum Step { WithDevice, WithoutDevice }
pub fn first_step(hardware: bool, device: Device) -> Step;
pub fn report(hardware: bool, device: Device, step: Step, first_step_failed: bool, frame_on_graphics_card: bool)
    -> (Decoder, Option<SoftwareReason>);
```

Rules the tests pin:
- `position_for`: 0 when `duration` is 0; else `floor(duration × clamp(percent, 1, 99) / 100)` computed as the app did (`(duration as f64 * (percent as f64 / 100.0)) as u64`), capped at `duration − 2_500_000` (never below 0).
- `processor_size`: the shown size is `(width × par.0 / par.1, height)` (a zero in `pixel_aspect` counts as 1:1). It is fitted into a `2 × box_side` box keeping its shape and is never larger than the shown size; each side is at least 1.
- `choose_frame`: returns the first frame whose time is at or past `target`; at the end of the stream, the last frame seen; `None` when there was none; once `elapsed() >= STEPPING_LIMIT`, the frame just read.
- `report`: `(Hardware, None)` only for `step == WithDevice && frame_on_graphics_card`. Otherwise `Software` with: `TurnedOff` when `hardware` is false; `NoDevice` when the device is `Missing`; `HardwareFailed` when `first_step_failed`; else `NoHardwareDecoder`.

- [ ] **Step 1:** Failing tests in `rules.rs`:
  - `the_position_is_the_apps_rule`: `(0, 25) → 0`; `(100_000_000, 25) → 25_000_000`; `(100_000_000, 99) → 97_500_000`; `(100_000_000, 150) → 97_500_000`; `(100_000_000, 0) → 1_000_000`; `(1_000_000, 50) → 0`; `(36_000_000_000, 33) → 11_880_000_000`.
  - `the_processor_is_asked_for_twice_the_box_and_never_more_than_the_frame`: `((1920,1080),(1,1),256) → (512,288)`; `((720,576),(16,15),256) → (512,384)`; `((320,240),(1,1),256) → (320,240)`; `((7680,4320),(1,1),96) → (192,108)`; `((1,1),(1,1),256) → (1,1)`; `((1080,1920),(0,0),256) → (288,512)`.
  - `stepping_stops_at_the_first_frame_at_or_past_the_position`, `at_the_end_of_the_stream_the_last_frame_is_used`, `a_stream_with_no_frame_gives_none`, `after_three_seconds_the_frame_just_read_is_used`, `an_error_from_the_reader_is_passed_on` (scripted `next` and `elapsed` closures).
- [ ] **Step 2:** Failing tests in `pixels.rs`: `top_down_rows_are_read_in_order`, `bottom_up_rows_are_flipped`, `a_stride_wider_than_the_row_skips_the_padding`, `each_turn_puts_the_top_left_pixel_where_it_belongs` (a 2 × 3 picture with distinct pixels, all four turns, sizes swap for the quarter turns), `a_buffer_too_short_gives_none`, `blue_green_red_becomes_red_green_blue`.
- [ ] **Step 3:** Failing tests in `attempt.rs`: `the_table` (the five rows of the spec's section 3 plus `first_step`), and `a_frame_in_ordinary_memory_never_reports_hardware` (every combination of the five inputs with `frame_on_graphics_card == false`).
- [ ] **Step 4:** Failing test in `lib.rs`: `other_methods_are_unsupported`, and `#[cfg(not(windows))] video_needs_windows`.
- [ ] **Step 5:** Failing maintenance test `scripts/tests/video-program.test.mjs`: `crates/sumafile-media/Cargo.toml` names neither `windows` nor `sumafile-video`; `crates/sumafile-video/Cargo.toml` is `publish = false`.
- [ ] **Step 6:** Run all; expected FAIL. Implement. `main.rs` mirrors `sumafile-media`'s: the ready message, then `server::run(stdin, stdout, sumafile_video::handle)`, then `exit(0)`.
- [ ] **Step 7:** Run `cargo test -p sumafile-video`, `npm run test:maintenance`, clippy for both targets; expected PASS.
- [ ] **Step 8:** Commit: `sumafile-video: the program and its rules`.

### Task 5: `sumafile-video`: Media Foundation (Windows only)

**Files:**
- Create: `crates/sumafile-video/src/mf.rs` (`#[cfg(windows)]`)
- Create: `crates/sumafile-video/tests/support/avi.rs`, `tests/avi_layout.rs`, `tests/video.rs` (`#![cfg(windows)]`), `examples/write-test-avi.rs`
- Modify: `crates/sumafile-video/Cargo.toml` (the `windows` dependency), `src/lib.rs`; `.github/workflows/ci.yml` only if step 7 says so

**Interfaces:**
- Consumes: Task 4's rules, `sumafile_imaging::thumbnail::{Decoded, render_decoded}`.
- Produces: `pub(crate) fn mf::thumbnail(path: &Path, box_side: u32, position_percent: u8, hardware: bool) -> Result<Answered, RenderError>`.
- Test support: `avi::write(path, width, height, frames_per_second, colours: &[[u8; 3]])` writes an uncompressed 24-bit AVI, one solid-colour frame per entry, with an index. `avi::colour_of(index) -> [u8; 3]` gives frame `index` a colour whose channels are multiples of 80 (`[(i % 4) * 80, (i / 4 % 4) * 80, (i / 16 % 4) * 80 + 15]`), and `avi::index_of(colour) -> usize` rounds each channel back, so a thumbnail names its frame despite JPEG and colour conversion.

**The call sequence** (the tests cannot settle it; follow it, and resolve exact signatures against the `windows` 0.62 source in `~/.cargo/registry`):

1. *Once per process* (`OnceLock`): `CoIncrementMTAUsage`, then `MFStartup(MF_VERSION, MFSTARTUP_LITE)`. Never shut down: the process exits. Failure → `RenderError::Unsupported`.
2. *The device*, behind a `Mutex`, made on the first request with `hardware` on: `D3D11CreateDevice(None, D3D_DRIVER_TYPE_HARDWARE, .., D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT, ..)`. Refuse it when its adapter's `DXGI_ADAPTER_DESC1.Flags` has `DXGI_ADAPTER_FLAG_SOFTWARE`. Turn on `ID3D11Multithread::SetMultithreadProtected(true)`. `MFCreateDXGIDeviceManager`, `ResetDevice`. No device → `Device::Missing`, remembered. A device that reports `GetDeviceRemovedReason` as failed after an attempt is dropped and made again on the next request.
3. *One attempt* (`with_device: bool`):
   - Attributes: with a device, `MF_SOURCE_READER_D3D_MANAGER` and `MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = 1`. Neither `MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING` nor the advanced one.
   - `MFCreateSourceReaderFromURL` (the path as NUL-terminated UTF-16). Deselect all streams, select `MF_SOURCE_READER_FIRST_VIDEO_STREAM`.
   - From the native type: `MF_MT_FRAME_SIZE`, `MF_MT_PIXEL_ASPECT_RATIO`, `MF_MT_VIDEO_ROTATION`. Duration: `GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, MF_PD_DURATION)`; absent → 0.
   - Output type: try `MFVideoFormat_NV12`, then `P010`, then `YUY2`, then the native type itself; the first `SetCurrentMediaType` that succeeds. None → the attempt fails. Read the full type back with `GetCurrentMediaType`.
   - Seek with `SetCurrentPosition` when the position is past 0; a source that cannot seek is read from its start.
   - `rules::choose_frame` over `ReadSample`: `MF_SOURCE_READERF_ERROR` → error; `ENDOFSTREAM` → `None`; a missing sample (a gap) → read on; `CURRENTMEDIATYPECHANGED` → read the type again.
   - `frame_on_graphics_card` = the frame's first buffer casts to `IMFDXGIBuffer`.
   - Convert and scale that one frame: `CoCreateInstance(CLSID_VideoProcessorMFT)`; with a device, `ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, ..)`; `IMFVideoProcessorControl::SetRotation(ROTATION_NONE)`; input type = the reader's current type; output type `MFVideoFormat_RGB32` at `rules::processor_size`, progressive, square pixels; `ProcessInput`, `ProcessOutput` (allocate the output sample when the stream info says the transform provides none; on `MF_E_TRANSFORM_NEED_MORE_INPUT` send `MFT_MESSAGE_COMMAND_DRAIN` and ask once more).
   - Copy out: `ConvertToContiguousBuffer`; `IMF2DBuffer::Lock2D` (pointer to the top row and a signed pitch) when offered, else `Lock` with the output type's `MF_MT_DEFAULT_STRIDE` (negative means bottom-up; absent means `width × 4`, top-down). Then `pixels::upright_rgb` with `Turn::from_degrees(rotation)`.
   - JPEG, as `sumafile-media`'s `render::thumbnail` makes it: the box is `min(box_side, the picture's longer side)`, so a small video is not enlarged; `render_decoded(&Decoded { image, full_size: (image.width(), image.height()) }, box)`; the reply's width and height are `sumafile_imaging::thumbnail::fit` of the picture's size in that box.
4. *Two attempts:* `attempt::first_step`; when `WithDevice` fails, run `WithoutDevice` with `first_step_failed = true`. Both failed → `RenderError::Decode("The video could not be decoded.")`. A missing or unreadable file → `RenderError::Io`.
   *Odd decoders:* in the attempt without a device, when no output type can be set, open the reader once more with `MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = 1`, ask for `MFVideoFormat_RGB32`, and carry on from the seek.
5. Every COM object is released before the function returns. Log nothing; the service logs.

- [ ] **Step 1:** Write `tests/support/avi.rs` and the failing layout test `tests/avi_layout.rs` (runs on every system): `the_file_is_a_riff_avi_whose_sizes_add_up` (bytes 0..4 `RIFF`, 8..12 `AVI `, the RIFF size is the file length minus 8, the `movi` list holds one `00db` chunk of `width × height × 3` bytes per frame, `idx1` has one 16-byte entry per frame, `avih` says the frame count and `1_000_000 / fps` microseconds a frame) and `a_colour_names_its_frame` (`index_of(colour_of(i)) == i` for 0..64, also after ±30 on every channel).
- [ ] **Step 2:** Run `cargo test -p sumafile-video --test avi_layout`; expected FAIL, then implement the writer; expected PASS.
- [ ] **Step 3:** Write the Windows tests in `tests/video.rs`, driving the real program (`CARGO_BIN_EXE_sumafile-video`) through `MediaClient::video_thumbnail`, with a 96 × 64 file of 60 frames at 10 a second:
  - `the_frame_at_each_position_is_the_one_asked_for`: for 25, 1 and 99 percent, the thumbnail's centre pixel gives `index_of(..) == ceil(position_for(60_000_000, p) / 1_000_000)` (frames 15, 1 and 58), and the picture is 96 × 64 at size 256.
  - `with_the_switch_on_and_off_an_uncompressed_video_says_software`: `hardware: false` → `(Software, Some(TurnedOff))`; `hardware: true` → `Software` with `NoDevice` or `NoHardwareDecoder`.
  - `a_file_that_is_not_a_video_answers_decode` (text bytes named `notes.avi`), `a_video_cut_inside_its_headers_answers_decode` (the first 200 bytes), `an_empty_file_answers_decode`, `a_file_without_a_video_stream_answers_decode` (a one-second PCM WAV the test writes, named `sound.avi`).
  - `a_unicode_path_gets_its_thumbnail` (`vidéo 日本.avi`).
  - `the_program_exits_when_its_input_closes` (as `sumafile-media`'s test).
- [ ] **Step 4:** Add the `windows` dependency with the features the calls need (`Win32_Foundation`, `Win32_Media_MediaFoundation`, `Win32_Graphics_Direct3D`, `Win32_Graphics_Direct3D11`, `Win32_Graphics_Dxgi`, `Win32_Graphics_Dxgi_Common`, `Win32_System_Com`, `Win32_System_Com_StructuredStorage`, `Win32_System_Variant`, and what the compiler asks for beyond these). Implement `mf.rs`. Each `unsafe` block gets a `// SAFETY:` line.
- [ ] **Step 5:** `examples/write-test-avi.rs`: `write-test-avi <folder> <count>` writes `count` files `video-NNN.avi` (320 × 180, 20 frames, 10 a second) with `#[path = "../tests/support/avi.rs"] mod avi;`.
- [ ] **Step 6:** Run here: `cargo test -p sumafile-video` (Linux: rules and layout), `xcheck.sh clippy --workspace --locked --all-targets --all-features -- -D warnings`; expected PASS. Commit: `sumafile-video: frames through Media Foundation`. Push.
- [ ] **Step 7:** **User gate.** Ask the user to start CI. Read the Rust quality gate's log. If the Windows tests fail because the program does not start (Media Foundation is an optional feature on Windows Server), add to `ci.yml`, before "Run tests" in `quality` and before the smoke in `winui`: `Install-WindowsFeature Server-Media-Foundation` (PowerShell, fail the step if it reports that a restart is needed), commit, and ask for a new run. Fix what the Windows tests show, test-first where a rule is wrong. Do not go on until these tests pass on CI.

### Task 6: Core: a video thumbnail through the provider, cached by position

**Files:**
- Modify: `crates/simplefile-core/src/media.rs`, `src/preview.rs:77-137`, `src/thumbnail_cache.rs`
- Test: the same files' tests

**Interfaces (produces):**
```rust
// media.rs
#[derive(Debug, Clone, Copy, PartialEq, Eq)] pub enum VideoDecoder { Hardware, Software }
#[derive(Debug, Clone, Copy, PartialEq, Eq)] pub enum SoftwareReason { TurnedOff, NoDevice, NoHardwareDecoder, HardwareFailed }
pub struct VideoThumbnail { pub jpeg: Vec<u8>, pub decoder: VideoDecoder, pub reason: Option<SoftwareReason> }
pub trait MediaProvider { /* existing */
    /// Default: this provider makes no video thumbnails.
    fn video_thumbnail(&self, path: &Path, size: u32, position_percent: u8, hardware: bool) -> Result<VideoThumbnail, String> { Err(..) }
}
pub fn thumbnail_is_video(path: &str) -> bool;       // by extension alone, as thumbnail_uses_media_process
pub const VIDEO_GPU_SETTING_KEY: &str = "videoThumbnailsUseGpu";
// preview.rs
pub fn generate_thumbnail_at(path: String, size: Option<u32>, position_percent: Option<u8>) -> Result<Vec<u8>, String>;
pub fn generate_thumbnail(path: String, size: Option<u32>) -> Result<Vec<u8>, String>;   // = generate_thumbnail_at(path, size, None)
// thumbnail_cache.rs
pub fn get_at(path: &str, modified: SystemTime, size: u64, thumb_size: u32, position_percent: Option<u8>) -> Option<Vec<u8>>;
pub fn put_at(path: &str, modified: SystemTime, size: u64, thumb_size: u32, position_percent: Option<u8>, jpeg_bytes: &[u8]);
// get and put stay, and call these with None
```
Rules: a video extension (`is_video_extension`) takes the video route. On it: a path inside an archive (`crate::archive::is_archive_virtual_path`) is refused with `Unsupported image format: <extension>` **before** `resolve_readable_path`, so nothing is unpacked. The position is `position_percent.unwrap_or(25).clamp(1, 99)`. `hardware` comes from `pub(crate) fn video_uses_gpu(stored: Option<&str>) -> bool` (true unless the value, trimmed and lowercased, is `false`), given `settings_store::get_db_setting(VIDEO_GPU_SETTING_KEY)`; a read error counts as no value. The cache key adds `:position:` and the position byte only when a position is given, so every existing key is unchanged. A picture extension ignores `position_percent`.

- [ ] **Step 1:** Failing tests in `thumbnail_cache.rs`: `a_picture_key_is_what_it_was` (the key for `("C:\\p\\a.jpg", UNIX_EPOCH + 1_700_000_000 s, 12_345, 256)` equals a literal recorded from the current code before the change) and `two_positions_of_one_video_are_kept_apart` (`put_at(.., Some(25), a)`, `put_at(.., Some(60), b)`; each `get_at` returns its own; `get` with no position finds neither).
- [ ] **Step 2:** Failing tests in `media.rs` (with the test provider extended to record video calls): `a_video_goes_to_the_provider_with_its_position` (`generate_thumbnail_at("…/clip.mp4", Some(256), Some(40))` → the provider saw `(256, 40, true)`), `a_video_without_a_position_is_asked_for_at_25`, `a_position_is_kept_between_1_and_99`, `the_gpu_is_used_unless_the_setting_says_false` (`video_uses_gpu`: `None`, `"true"` and `"0"` → true; `"false"` and `" False "` → false), `a_video_thumbnail_is_cached_by_position` (second call with the same position does not reach the provider; another position does), `a_video_inside_an_archive_is_refused_and_nothing_is_unpacked`, `a_picture_ignores_the_position`, `without_a_provider_a_video_is_unsupported`.
- [ ] **Step 3:** Run with the Linux build patch (`git apply <scratchpad>/core-linux-build.patch`, reverse it before committing): `cargo test -p simplefile-core media:: thumbnail_cache::`; expected FAIL. Implement. Expected PASS, and the eight known Windows-only failures unchanged.
- [ ] **Step 4:** Commit: `Core: video thumbnails through the provider, cached by position`.

### Task 7: Service: the video program, its lane, the counts, the IPC

**Files:**
- Modify: `crates/simplefile-service/src/media_provider.rs`, `src/scheduler.rs`, `src/session/jobs.rs`, `src/session/mod.rs`, `src/session_cache.rs:660-700`, `src/dispatch/{mod,params,async_ops,handlers}.rs`
- Create: `crates/simplefile-service/src/media_status.rs`
- Modify: `ipc/schema/v1/commands.json`, `types.json`, `goldens/ipc.handshake.result.json`, `scripts/check-ipc-schema.mjs:100-104`, `scripts/check-winui-parity-gate.mjs:75-76`, `scripts/generate-ipc-bindings.mjs`, `docs/winui-migration/parity-gate.md`
- Modify (generated): `crates/simplefile-ipc/src/protocol_generated.rs`, `src-winui/SimpleFile.Ipc/Protocol.Generated.cs`, `NamedPipeJsonClient.Generated.cs`
- Modify: `src-winui/SimpleFile.Ipc/ISimpleFileIpc.cs`, `Models.cs`, `src-winui/SimpleFile.Core/FileOperationService.cs`, `src-winui/SimpleFile.Tests/NullIpc.cs`
- Test: `media_status.rs`, `scheduler.rs`, `session/jobs.rs`, `session/tests.rs`, `media_provider.rs`, `src-winui/SimpleFile.Tests/VideoThumbnailIpcTests.cs`

**Interfaces (produces):**
```rust
// media_status.rs
pub struct VideoStatus { pub hardware: u64, pub software: u64, pub last_software_reason: Option<SoftwareReason> }
pub(crate) struct VideoTally { /* counters */ }
impl VideoTally {
    pub(crate) fn record(&self, decoder: VideoDecoder, reason: Option<SoftwareReason>) -> Option<&'static str>; // a line to log: the first use of each decoder, and each change of decoder
    pub(crate) fn status(&self) -> VideoStatus;
}
// media_provider.rs
pub fn video_status() -> VideoStatus;          // the installed provider's tally; zeros before install
// scheduler.rs
const MEDIA_VIDEO_PERMITS: usize = 4;   enum MediaLane { Thumbnail, Preview, Video }   // Video does not wait for foreground idle
```
- `ClientProvider` owns a second `MediaClient` for `default_video_helper_path()` and implements `video_thumbnail` through it, mapping the client's `Decoder` / `SoftwareReason` to core's, recording each success in the tally and logging (`log::info!`) the line `record` returns.
- `thumbnail_lane` gains `ThumbnailLane::Video`: every path is a video (`thumbnail_is_video`). It runs on `MediaLane::Video`.
- IPC `generate_thumbnail` params become `{ "path": "string", "size": "number", "positionPercent": "number?" }`; `ThumbnailParams` gains `position_percent: Option<u8>` (`#[serde(default, rename = "positionPercent")]`); `Dispatch::GenerateThumbnail` and `generate_thumbnail_and_reply` carry it to `generate_thumbnail_at`. The session cache key (`SessionCache::thumbnail_key`) gains the position when one is given.
- New IPC method `get_media_status`: no params; result type `MediaStatus { "videoHardware": "number", "videoSoftware": "number", "lastSoftwareReason": "string?" }` (the reason in the wire words). Answered at once with `reply_ok` from `video_status()`. `domainMethodCount` becomes 86 in the four literal places; regenerate the bindings; add a parity-gate row (status `MANUAL`: "Settings shows the video decode counts").
- C#: `Task<byte[]> GenerateVideoThumbnailAsync(string path, uint size, int positionPercent, CancellationToken ct = default)` (a second generator row for `generate_thumbnail`, body `new { path, size, positionPercent }`; if the generator allows one row a method, let it take several and keep "every method has a row"), `Task<MediaStatus> GetMediaStatusAsync(CancellationToken ct = default)`, model `MediaStatus { ulong VideoHardware; ulong VideoSoftware; string? LastSoftwareReason }`, both passed through `FileOperationService`, both `virtual` and throwing in `NullIpc`.

- [ ] **Step 1:** Failing tests: `media_status.rs`: `decodes_are_counted_by_who_made_them`, `the_last_processor_reason_is_kept`, `the_first_use_of_each_decoder_and_each_change_are_logged_once`. `scheduler.rs`: `videos_do_not_wait_for_thumbnails_or_previews` (all thumbnail and preview permits held; a `Video` task runs), `at_most_four_videos_run_at_a_time`. `jobs.rs`: `a_video_waits_in_the_video_lane`, `a_batch_of_videos_and_pictures_stays_in_the_background_lane`. `media_provider.rs`: `a_video_goes_to_the_video_program_and_never_the_picture_one` (a provider whose picture client names a missing program and whose video client names another missing program: `video_thumbnail` answers the unsupported message and `stats().starts == 0`), `the_status_is_answered_without_asking_a_client`. `session/tests.rs`: `generate_thumbnail_passes_the_position_to_the_provider`, `get_media_status_answers_the_counts`.
- [ ] **Step 2:** Failing C# tests in `VideoThumbnailIpcTests.cs`: `AVideoThumbnailRequestCarriesItsPosition` (the JSON sent is method `generate_thumbnail` with `positionPercent: 40`), `APictureThumbnailRequestCarriesNone`, `TheMediaStatusIsRead` (a canned reply → the model).
- [ ] **Step 3:** Run; expected FAIL. Implement, regenerate (`npm run generate:ipc-bindings`).
- [ ] **Step 4:** Run: `cargo test -p simplefile-service` under the Linux patch (the ten known failures unchanged), `cargo test -p simplefile-ipc`, the C# tests, `npm run check`, `npm run test:maintenance`; expected PASS.
- [ ] **Step 5:** Commit: `Service: video thumbnails in their own program and lane, and the decode counts`.

### Task 8: App: list thumbnails and the frame picker ask the service

**Files:**
- Create: `src-winui/SimpleFile.Core/NetworkPaths.cs`, `src-winui/SimpleFile.Core/WantedRequests.cs`
- Modify: `src-winui/SimpleFile.App/FileListThumbnailHost.cs`, `PreviewPresenter.Video.cs:38-58`, `MainWindow.xaml.cs:240`, `MainWindow.Chrome.cs:795`, `VideoThumbnailFrame.cs`
- Test: `src-winui/SimpleFile.Tests/NetworkPathsTests.cs`, `WantedRequestsTests.cs`, `VideoThumbnailRequestTests.cs` (source-shape), `WinUiSourceShapeTests.cs:778-781`

**Interfaces (produces):**
```csharp
public static class NetworkPaths {
    /// True for \\server\share paths and for drive letters Windows maps to a network share.
    public static bool IsNetwork(string? path, Func<string, DriveType>? driveTypeOf = null);   // the lookup is remembered per drive root
}
public sealed class WantedRequests {
    public IDisposable Want(string key);     // one caller is waiting for this request
    public bool IsWanted(string key);        // someone still is
}
```
App rules:
- `FileListThumbnailHost.Configure` takes a second delegate, `Func<string, uint, int, CancellationToken, Task<byte[]>>? loadVideoThumbnailAsync`, wired in `MainWindow` to `fileOps.GenerateVideoThumbnailAsync`.
- A video row loads: the service's bytes (`PreviewImageSourceFactory.FromBytesAsync`), then Windows' thumbnail, then nothing. `VideoThumbnailExtractor` is **not** called from the list any more, and is **not** deleted yet (Task 10 measures against it).
- Gates: `VideoLoadGate` (2) for local videos; a new `NetworkVideoLoadGate` (1) for `NetworkPaths.IsNetwork(path)`.
- Each caller of `LoadThumbnailAsync` holds `Wanted.Want(key)` while it awaits. A video load that reaches the front of its gate with `!Wanted.IsWanted(key)` returns null without asking the service and without caching. Picture loads are unchanged.
- The preview pane's frame picture: `fileOps.GenerateVideoThumbnailAsync(path, 144, frame.Percent, cancellationToken)`, outside the list's gates; the icon stays until it arrives and when there is none.
- `VideoThumbnailFrame.PositionFor` stays until Task 11 (the old extractor uses it).

- [ ] **Step 1:** Failing tests: `NetworkPathsTests` (`\\server\share\a.mp4` and `//server/share` true; `C:\a.mp4` with a lookup answering `Fixed` false; `Z:\a.mp4` with `Network` true; the lookup is called once for two paths on one drive; null and empty false). `WantedRequestsTests` (`ARequestIsWantedWhileSomeoneWaits`, `TwoWaitersKeepItWantedUntilBothLeave`, `ANewWaiterMakesItWantedAgain`).
- [ ] **Step 2:** Failing source-shape tests in `VideoThumbnailRequestTests.cs`: the list's `LoadCoreAsync` calls the video delegate and has no `VideoThumbnailExtractor.LoadAsync`; the gate choice names `NetworkVideoLoadGate` under `NetworkPaths.IsNetwork`; `IsWanted` is checked after the gate and before the load; `PreviewPresenter.Video.cs` calls `GenerateVideoThumbnailAsync(path, 144, frame.Percent`. Update `WinUiSourceShapeTests` 778-781 to match.
- [ ] **Step 3:** Run; expected FAIL. Implement. Run the full C# suite (the 64 known Linux-only failures unchanged) and the App type-check (`appcheck`: only the known `ShellIconLoader` errors); expected PASS.
- [ ] **Step 4:** Commit: `App: video thumbnails and the frame picker come from the file service`.

### Task 9: App: the Settings switch and the counts

**Files:**
- Create: `src-winui/SimpleFile.Core/MediaStatusText.cs`
- Modify: `src-winui/SimpleFile.Core/UiSettings.cs`, `WorkspaceSettingsStore.cs:22-69,102,212`, `src-winui/SimpleFile.App/SettingsWindow.xaml:200-278`, `SettingsWindow.SettingsState.cs`
- Test: `src-winui/SimpleFile.Tests/MediaStatusTextTests.cs`, `WorkspaceSettingsStoreTests.cs`, `UiSettingsTests.cs`

**Interfaces (produces):**
```csharp
public bool VideoThumbnailsUseGpu { get; set; } = true;                  // UiSettings; stored under "videoThumbnailsUseGpu"
public static class MediaStatusText {
    public static string Counts(MediaStatus? status);                     // line 1
    public static string? LastProcessorDecode(MediaStatus? status);       // line 2, null when there is none
}
```
Exact texts:
- Counts, before any decode or when the status cannot be read: `No video thumbnail has been decoded since SumaFile started. Thumbnails already in the cache are not decoded again.`
- Counts, otherwise: `Since SumaFile started: {hardware:N0} decoded by the graphics card, {software:N0} by the processor.`
- Line 2: `Last processor decode: ` followed by `the graphics card is turned off above.` (`turned_off`), `Windows found no graphics card that can decode video.` (`no_device`), `the graphics card has no decoder for that video's format.` (`no_hardware_decoder`), `hardware decoding failed for that video.` (`hardware_failed`). Null for no reason or an unknown word.

UI: in `StoragePanel`, under the cache controls, a header `Video thumbnails`, `ToggleSwitch x:Name="VideoThumbnailsGpuSwitch" Header="Use the graphics card for video thumbnails"`, and `TextBlock`s `VideoThumbnailsCountsText` and `VideoThumbnailsReasonText` (collapsed when null). The texts are filled from `GetMediaStatusAsync` when the Storage page is shown; a failure shows the "No video thumbnail…" text.

- [ ] **Step 1:** Failing tests: `MediaStatusTextTests` (the six texts above, verbatim; `1234` shows as `1,234` under the invariant culture; null status; an unknown reason word gives no second line). `UiSettingsTests`: the default is true. `WorkspaceSettingsStoreTests`: `false` round-trips under the key `videoThumbnailsUseGpu`; a missing value loads as true.
- [ ] **Step 2:** Run; expected FAIL. Implement. Run the C# suite and the App type-check; expected PASS.
- [ ] **Step 3:** Commit: `Settings: the graphics-card switch for video thumbnails, and what was decoded`.

### Task 10: Ship `sumafile-video`, measure both paths in the UI smoke, document

**Files:**
- Modify: `scripts/build-winui-release.ps1`, `packaging/winui/sumafile-winui.nsi`, `scripts/smoke-winui-msi.ps1`, `scripts/smoke-winui-nsis.ps1`, `scripts/check-winui-packaging.mjs`, `scripts/smoke-winui-ui.ps1`
- Create: `src-winui/SimpleFile.App/MainWindow.UiSmoke.VideoThumbnails.cs` (`#if DEBUG`); modify `MainWindow.UiSmoke.cs:150-153`
- Modify: `docs/ARCHITECTURE.md`, `README.md`, `docs/CHANGELOG.md`, `docs/WINUI_RUNTIME_VERIFICATION.md`

Rules:
- Everything `sumafile-media.exe` gets in packaging, `sumafile-video.exe` gets too: built with `cargo build -p sumafile-video --bin sumafile-video`, copied beside the service, required in the payload, ended before files are replaced (release script's process list, NSIS `taskkill`), required by both installer smokes, policed by `check-winui-packaging.mjs`.
- UI smoke script: builds `sumafile-video`, fails when it is missing beside the service, waits for it at cleanup, and makes the fixture with `cargo run --locked -p sumafile-video --example write-test-avi -- <testRoot>\videos 24`.
- Smoke scenario `video-thumbnails` (after `thumbnails`), with the disk cache pointed at an empty folder as the photo scenario does:
  1. New path, one file at a time: `fileOps.GenerateVideoThumbnailAsync(path, 192, 25)` then `PreviewImageSourceFactory.FromBytesAsync`. Every file must give a picture, or the scenario fails.
  2. Old path, one file at a time: `VideoThumbnailExtractor.LoadAsync(path, 192, VideoThumbnailFrame.Default)`; count the files it gives a picture for.
  3. Warm: the new path again for every file, from the disk cache.
  4. `GetMediaStatusAsync`: `videoSoftware` is at least 24 (the runner has no hardware decoder; an uncompressed video never has one).
  5. Result: `{ scenario = "video-thumbnails", videos, newMs, newPerThumbnailMs, oldMs, oldPictures, oldPerThumbnailMs, warmMs, videoHardware, videoSoftware, passed }`. The script prints it as it prints `thumbnails`.
- Docs: architecture guide (the video program: why it is separate, the request, the two attempts, what `hardware` means, the lane, the counts, the fallbacks); README and changelog (hardware-decoded video thumbnails kept in the disk cache; the switch); runtime verification (the user's PC check from the spec's section 5, word for word).

- [ ] **Step 1:** Failing checks: extend `scripts/tests` beside the packaging tests so each place above must name `sumafile-video`; a source-shape test that `MainWindow.UiSmoke.cs` runs the `video-thumbnails` checkpoint.
- [ ] **Step 2:** Run `npm run check`, `npm run test:maintenance`; expected FAIL. Implement. Expected PASS. App type-check clean.
- [ ] **Step 3:** Commit: `Ship sumafile-video; time both video thumbnail paths in the UI smoke`. Push.
- [ ] **Step 4:** **User gate.** Ask the user to start CI. From the WinUI job's report, read `newPerThumbnailMs`, `oldPerThumbnailMs` and `oldPictures`. Tell the user the figures. The new path is "not slower" when `newPerThumbnailMs <= oldPerThumbnailMs`. If it is slower, or the old path made no picture of the fixture so nothing can be compared, stop: the user decides whether the old extractor goes.

### Task 11: Remove the old in-app extractor

Only after Task 10's figures say the new path is not slower, or the user says to go on.

**Files:**
- Delete: `src-winui/SimpleFile.App/VideoThumbnailExtractor.cs`
- Modify: `src-winui/SimpleFile.App/VideoThumbnailFrame.cs` (drop `PositionFor`), `FileListThumbnailHost.cs:337-338` (`IsVideoThumbnail` → `MediaFolder.IsVideo`), `PreviewPresenter.Video.cs` (`CanUseVideoThumbnail` → `MediaFolder.IsVideo`), `MainWindow.UiSmoke.VideoThumbnails.cs` (drop the old-path timing and its result fields), `WinUiSourceShapeTests.cs`, `docs/CHANGELOG.md`

- [ ] **Step 1:** Failing source-shape test `TheInAppVideoExtractorIsGone`: no file under `SimpleFile.App` names `VideoThumbnailExtractor`, `MediaComposition` or `MediaClip`.
- [ ] **Step 2:** Run; expected FAIL. Delete and adjust. Run the C# suite, the App type-check, `npm run check`, `npm run test:maintenance`; expected PASS.
- [ ] **Step 3:** Commit, with the two CI figures in the message: `Remove the in-app video thumbnail extractor`. Push.
- [ ] **Step 4:** Final checks before the report: `cargo fmt --all -- --check`; both clippy targets with CI's flags; all Rust tests here (new crates in full, core and service against their known Windows-only failures); the full C# suite against its baseline; `npm run check`; `npm run test:maintenance`; `npm run check:security`. Ask for a whole-branch review, fix what it finds, and report to the user what is proven here, what CI proved, and the PC check that remains (spec section 5).
