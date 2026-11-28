# GPU video thumbnails

Date: 2026-11-28. Status: design, awaiting approval.

This is the second of two projects. It runs inside the media process built by the first
(`2026-11-28-media-process-and-decoders-design.md`).

## Intent

The user wants SumaFile to use the graphics card where it does real work, and video is that place: graphics chips are
built to decode video.

Today the app makes each video thumbnail itself, through Windows' video-editing API
(`VideoThumbnailExtractor.cs`: `MediaClip`, `MediaComposition.GetThumbnailAsync`), two at a time, in the app's own
process. The thumbnails live only in memory (at most 512 of them, 32 MiB), so every restart makes them again.

The user chose: **Media Foundation's reader with hardware decoding, in the separate media process**, with the result
kept in the disk thumbnail cache.

## Goals

1. A video's thumbnail is made by Windows' hardware decoder: the graphics card decodes the frame and scales it, and
   only the thumbnail-sized picture comes back to memory.
2. Video thumbnails go into the same disk cache as picture thumbnails (500 MB by default), so they survive restarts.
3. The frame is chosen as it is today: 25 % into the video, or the frame the user picked in the preview pane.
4. The fallback order is: hardware decoding, then software decoding, then Windows' own thumbnail, then the file's
   icon.
5. One switch in Settings: **Use the graphics card for video thumbnails**, on by default. Off means the media process
   decodes on the processor.
6. Under the switch, one line says what is really in use, for example "Hardware decoding is in use" or "This PC has no
   hardware decoder for the last video; software decoding was used".
7. A video that crashes or hangs a decoder or a graphics driver costs only its own thumbnail. The app and the file
   service are not affected.
8. A phone video shot upright shows upright, and a video with non-square pixels has its real shape.

## Non-goals (this round)

- Filmstrips, hover previews and timeline scrubbing. The reader is built so they can reuse it.
- Bundling FFmpeg. A video Windows has no decoder for (for example HEVC or AV1 without Microsoft's codec packs) gets
  Windows' thumbnail or its icon, as today.
- Video playback in the preview pane. It already uses hardware decoding through Windows' player.
- Correct tone mapping of HDR video. An HDR video's thumbnail may look washed out.
- Choosing which graphics card to use. Windows' default adapter is used.
- GPU work for picture thumbnails. Decoding is the slow part there, and it stays on the processor.

## Anti-goals (failure even if it "works")

- Any graphics or Media Foundation work at app start. Nothing is created until the first video thumbnail is needed.
- A graphics device, decoder or video file kept open while nothing is being decoded.
- A thumbnail that claims hardware decoding when software decoding was used. The status line reports what happened.
- Thumbnails showing the wrong frame: a frame other than the one asked for, beyond the tolerance in section 3.
- Video thumbnails slower than today on a PC without a usable hardware decoder.
- A video on a slow network share holding up thumbnails of local files.

## Constraints

- **Windows only.** The Media Foundation code compiles and runs only on Windows. It cannot be run in the development
  container; only type-checked there. It is proven by Windows CI and by the user's check on real hardware.
- **CI has no graphics card.** Hosted Windows runners have no hardware video decoder. CI can prove the software path
  and everything around it. Only a real PC can prove the hardware path. The status line (goal 6) and a log line are how
  the user sees it.
- **Media Foundation may be missing on the CI runner.** It is an optional feature on Windows Server. The first CI run
  shows whether it is there; if it is not, the workflow enables it before the tests.
- **Startup budget unchanged**, **no CPU-burning helpers**, **test-first**, **permissive licences only**: as in the
  first project. No new library is needed: the `windows` crate, already in the workspace, has these APIs.
- Push to `WorkingBranch` only. No pull request unless asked.

## Approaches considered

- **A. Media Foundation reader in the separate Rust media process. Chosen by the user.** Hardware decode and GPU
  scaling, the disk cache through the existing thumbnail path, and crash isolation. The most new code, and only
  testable on Windows.
- **B. Media Foundation reader in C#, inside the app.** Smaller, but a driver fault hits the app, there is no disk
  cache, and the Rust side cannot reuse it.
- **C. Windows `MediaPlayer` in frame-server mode.** Simple, but it builds a whole playback pipeline per file: slow for
  a folder of hundreds. Suited to a later hover preview.
- **D. Bundled FFmpeg.** Decodes nearly everything, at the cost of size, LGPL obligations and security updates. It could
  sit behind A later.

## Design

### 1. Where the code goes

- `crates/sumafile-media`: a `video` module.
  - Platform-neutral parts, tested on Linux: the position rule, the frame-size rule (rotation and pixel shape),
    turning the decoder's pixel rows into a picture, and the decision of when to try software after hardware.
  - `video/mf.rs`, Windows only: the Media Foundation and Direct3D calls, as thin as it can be made.
  - On other systems the request answers `unsupported`.
- `crates/sumafile-media-client`: a third request, `video_thumbnail`, with its own gate (two at a time, as the app's
  video gate is today) and its own timeout (30 seconds: opening a video on a network share is slow).
- `simplefile-core`: `MediaProvider::video_thumbnail`, and the thumbnail path for video extensions.
- `simplefile-service`: video permits on the media lane (two), separate from picture thumbnails and previews.

### 2. The request

- Helper wire format: `{ id, method: "video_thumbnail", path, size, positionPercent, hardware }` →
  `{ id, ok: true, format: "jpeg", width, height, decoder: "hardware" | "software" }` and the JPEG.
- App to service: `generate_thumbnail` gains an optional `positionPercent` (1 to 99). It is used for video extensions
  and ignored for pictures. Old clients keep working: the field is optional and defaults to 25.
- The service reads the setting `videoThumbnailsUseGpu` (default true) and passes it as `hardware`.
- **Disk cache.** The cache key gains the position, so the thumbnail at 25 % and the one at a picked frame are both
  kept. Picture keys do not change, so no picture thumbnail is made again.
- **Video extensions.** `ipc/schema/v1/media-formats.json` gains a `video` list, holding the extensions
  `MediaFolder.VideoExtensions` has today. The generator writes it for Rust and C#, and `MediaFolder` reads the
  generated list, so the two sides cannot drift.

### 3. Making the thumbnail (Windows)

1. **Once per process, on the first video request:** start Media Foundation. With `hardware` on, create a Direct3D 11
   device with video support on the default adapter and a DXGI device manager for it. If the device cannot be made,
   remember that for this process and use software.
2. **Open** the file with a source reader. With hardware: give it the device manager, allow hardware transforms and
   enable advanced video processing, so the reader's own video processor converts and scales on the graphics card.
   Select only the first video stream.
3. **Read what the file says:** frame size, rotation, pixel aspect ratio and duration.
4. **Ask for output** as 32-bit RGB at the thumbnail's size. The size is the frame fitted into the thumbnail box with
   twice the box's pixels on each side, as picture thumbnails do, and never larger than the frame.
5. **Seek** to the position. The rule is today's `VideoThumbnailFrame.PositionFor`: the percent of the duration, at
   most 250 ms before the end, and the start when the duration is unknown.
6. **Read frames** until one is at or past the position. A seek lands on the key frame before it, so frames are
   decoded forward from there. Decoding forward stops after 3 seconds, and the last frame decoded is used: on a file
   with key frames far apart, the thumbnail may then be a little earlier than asked.
7. **Copy the pixels out** (locking the buffer brings the small scaled frame back to memory), turn them by the file's
   rotation, and encode the JPEG with the existing pipeline.
8. **Close** the reader. The device and Media Foundation stay for the life of the process, which the client ends
   after 120 seconds idle.

**Software path.** The same steps without the device manager and with hardware transforms off. The reader's basic
video processing converts to RGB but does not scale, so the frame is scaled by the existing SIMD scaler.

**When software is tried after hardware**, for the same request: the device cannot be made, the reader refuses the
device manager or the output type, or reading a frame fails. If software fails too, the answer is a `decode` failure
and the app falls back to Windows' thumbnail.

**When the graphics device is lost** (driver reset, card removed): the request fails over to software, and the device
is made again on the next request.

**What reaches the app.** The reply says which decoder made the thumbnail. The service keeps the last answer and a
count of each, logs the first use of each and every change, and offers them through a small request,
`get_media_status`, that the Settings page reads for the status line.

### 4. In the app

- `VideoThumbnailExtractor` (the `MediaComposition` path) is removed. `FileListThumbnailHost` asks the file service
  for a video's thumbnail with its position, through the existing video gate (two at a time), and falls back to
  Windows' thumbnail when the service has none.
- The preview pane's "use this frame" keeps working: it stores a percent, and that percent goes with the request.
- **Settings**, on the page that holds the thumbnail cache:
  ```
  Video thumbnails
  [x] Use the graphics card for video thumbnails
      Hardware decoding is in use.
  ```
  The status line is one of: "Hardware decoding is in use.", "Software decoding is in use: this PC has no hardware
  decoder for the last video.", "Software decoding is in use: the graphics card is turned off above.", or "No video
  thumbnail has been made yet." Changing the switch applies to thumbnails made from then on; cached ones stay.

### 5. Tests (what must be proven)

**On Linux (development and CI):**

- The position rule equals `VideoThumbnailFrame.PositionFor` for the same inputs, including a zero duration and a
  position near the end.
- The frame-size rule: rotation swaps the sides, non-square pixels change the shape, the result is never larger than
  the frame, and the box is honoured.
- Pixel rows become the right picture: bottom-up and top-down rows, a row stride wider than the frame, and each
  rotation.
- The decision table for trying software after hardware.
- The client: `video_thumbnail` has its own gate and timeout, and the bad-file rules apply.
- Core: a video extension goes to the provider with its position; two positions of one file are cached separately;
  a picture's cache key is unchanged.
- The generated video list equals the schema, and `MediaFolder.IsVideo` agrees with it.
- C#: the position sent is the percent the user picked; the Settings status texts.

**On Windows CI (software path, no graphics card):**

- End to end through the helper with a video the test writes itself: an uncompressed AVI whose every frame is one
  known colour. The thumbnail at 25 %, at 1 % and at 99 % has the colour of the frame at that position, which proves
  the seek. An uncompressed AVI needs no codec, so the test does not depend on what the runner has installed.
- A file that is not a video, and a truncated one, answer `decode`.
- The reply says `software`.

**On a real PC (the user's check, because CI cannot):**

- A 4K H.264 file, an HEVC or AV1 file if the codec pack is installed, a phone video shot upright, and a folder of a
  few hundred videos: thumbnails appear, are upright, and the status line says hardware decoding is in use.
- The switch turned off: the status line says software, and thumbnails still appear.
- After a restart the thumbnails come from the disk cache at once.

## Calls I made (the user can overrule)

- The status line under the switch. It was not in what the user approved, but CI cannot prove the hardware path, so
  the user needs a way to see it.
- Decoding forward stops after 3 seconds.
- Two video thumbnails at a time, and a 30-second timeout.
- A video's position is part of the disk cache key.
- Changing the switch does not clear thumbnails already cached.
- The old in-app extractor is removed, not kept as another fallback: Windows' own thumbnail already covers that role.
- HDR tone mapping is left for later.

## Left to the builder

- The exact Media Foundation calls and attribute values, provided they follow section 3.
- Which `windows` crate version to use of the two already in the lock file.
- How the helper keeps its device and start-up state.
- Wording of log lines.
- The layout of the uncompressed AVI the Windows test writes.
