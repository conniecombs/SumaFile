# GPU video thumbnails

Date: 2026-11-28. Status: design, awaiting approval. A second reader checked it as a builder would; the answers to
their questions are folded in, and the calls that came out of that are listed at the end.

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
6. Under the switch, Settings says what really happened: how many video thumbnails the graphics card decoded, how
   many the processor decoded, and why the processor was used.
7. A video that crashes or hangs a decoder or a graphics driver costs only its own thumbnail. The app, the file
   service and picture thumbnails are not affected.
8. A phone video shot upright shows upright, and a video with non-square pixels has its real shape.

## Non-goals (this round)

- Filmstrips, hover previews and timeline scrubbing. The reader is built so they can reuse it.
- Bundling FFmpeg. A video Windows has no decoder for (for example HEVC or AV1 without Microsoft's codec packs) gets
  Windows' thumbnail or its icon, as today.
- Video playback in the preview pane. It already uses hardware decoding through Windows' player.
- Correct tone mapping of HDR video. An HDR video's thumbnail may look washed out.
- Choosing which graphics card to use. Windows' default adapter is used.
- GPU work for picture thumbnails. Decoding is the slow part there, and it stays on the processor.
- Thumbnails of videos inside archives. They show their icon, as today: making one would mean unpacking the whole
  video to a temporary file.

## Anti-goals (failure even if it "works")

- Any graphics or Media Foundation work at app start. Nothing is created until the first video thumbnail is needed.
- A graphics device, decoder or video file kept open while nothing is being decoded.
- **A thumbnail counted as hardware-decoded when the processor decoded it.** "Hardware" means the video decoder itself
  ran on the graphics card. Scaling on the graphics card after a processor decode does not count (section 3, step 5).
- Thumbnails showing the wrong frame: a frame other than the one asked for, outside the one allowance in section 3,
  step 4.
- Video thumbnails slower than today on a PC without a usable hardware decoder. It is measured before the old code is
  removed (section 5).
- A video on a slow network share holding up other thumbnails: pictures, or videos on local drives (section 4).

## Constraints

- **Windows only.** The Media Foundation code compiles and runs only on Windows. It cannot be run in the development
  container; only type-checked there. It is proven by Windows CI and by the user's check on real hardware.
- **CI has no graphics card.** Hosted Windows runners have no hardware video decoder. CI can prove the software path
  and everything around it. Only a real PC can prove the hardware path. The Settings lines (goal 6) and the log are
  how the user sees it.
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
    turning the decoder's pixel rows into a picture, the rule for when to stop stepping forward, and the decision of
    which attempt comes next and what is reported.
  - `video/mf.rs`, Windows only: the Media Foundation and Direct3D calls, as thin as it can be made.
  - On other systems the request answers `unsupported`.
- **Video runs in its own copy of the media process.** The file service starts the same program (`sumafile-media`) a
  second time for video, through a second client. A video that times out, or a graphics driver that crashes, ends
  only the video copy: picture thumbnails and previews in the other copy carry on. Each copy exits on its own after
  120 seconds idle and has its own 3 GiB memory limit.
- `crates/sumafile-media-client`: a third request, `video_thumbnail`, with a 30-second timeout (opening a video on a
  network share is slow) and up to four at a time: two local list thumbnails, one network list thumbnail and one for
  the preview pane (section 4).
- `simplefile-core`: `MediaProvider::video_thumbnail`, and the thumbnail path for video extensions.
- `simplefile-service`: a video lane with four permits, separate from picture thumbnails and previews, and the second
  client.

### 2. The request

- Helper wire format: `{ id, method: "video_thumbnail", path, size, positionPercent, hardware }` →
  `{ id, ok: true, format: "jpeg", width, height, decoder, reason }` and the JPEG.
  - `decoder` is `hardware` or `software`.
  - `reason` says why the processor was used: `turned_off` (the switch is off), `no_device` (Windows has no graphics
    device that can decode video), `no_hardware_decoder` (there is a device, but Windows decoded this file on the
    processor) or `hardware_failed` (the hardware attempt failed and the software attempt worked). It is absent when
    `decoder` is `hardware`.
- App to service: `generate_thumbnail` gains an optional `positionPercent` (1 to 99). It is used for video extensions
  and ignored for pictures. Old clients keep working: the field is optional and defaults to 25.
- The service reads the setting `videoThumbnailsUseGpu` (default true) and passes it as `hardware`.
- **Disk cache.** The cache key gains the position, so the thumbnail at 25 % and the one at a picked frame are both
  kept. Picture keys do not change, so no picture thumbnail is made again.
- **Video extensions.** `ipc/schema/v1/media-formats.json` gains a `video` list, holding the extensions
  `MediaFolder.VideoExtensions` has today. The generator writes it for Rust and C#, and `MediaFolder` reads the
  generated list, so the two sides cannot drift.
- **Archives.** A video inside an archive answers `unsupported`, and the app shows its icon.

### 3. Making the thumbnail (Windows)

1. **Once per process, on the first video request:** start Media Foundation. With `hardware` on, create a Direct3D 11
   device with video support on the default adapter and a DXGI device manager for it. A software adapter (Microsoft's
   Basic Render Driver) is refused: it would only pretend. If no device can be made, remember that for this process;
   the reason is `no_device`.
2. **Open** the file with a source reader and select only the first video stream. With a device: give the reader the
   device manager and allow hardware transforms. The reader is asked for frames in the decoder's own pixel format,
   and its own converters stay off, so a frame that is only stepped over is never converted or scaled.
3. **Read what the file says:** frame size, rotation, pixel aspect ratio and duration.
4. **Seek** to the position and **read frames** until one is at or past it. The position rule is today's
   `VideoThumbnailFrame.PositionFor`: the percent of the duration, at most 250 ms before the end, and the start when
   the duration is unknown. A seek lands on the key frame before the position, so frames are decoded forward from
   there and dropped. **The one allowance:** stepping forward stops after 3 seconds of real time, and the last frame
   decoded is used. On a file with key frames far apart, on a slow PC, the thumbnail is then earlier than asked. It is
   cached like any other.
5. **See who decoded it.** A frame decoded on the graphics card arrives as a Direct3D texture; a frame decoded on the
   processor arrives in ordinary memory. That is what `decoder` reports. A video Windows has no hardware decoder for
   is decoded on the processor even when a device was given, without any error: it reports `software` with
   `no_hardware_decoder`.
6. **Convert and scale the one chosen frame** with Windows' video processor. Given the device, it works on the
   graphics card; without one, on the processor. The output is 32-bit RGB at the thumbnail's size: the frame fitted
   into the thumbnail box with twice the box's pixels on each side, as picture thumbnails do, and never larger than
   the frame.
7. **Copy the pixels out** (this brings the small scaled frame back to memory), turn them by the file's rotation, and
   encode the JPEG with the existing pipeline.
8. **Close** the reader. The device and Media Foundation stay for the life of the process, which the client ends
   after 120 seconds idle.

**Two attempts, the same steps.** The first is made with the device, when `hardware` is on and a device exists. The
second is made without it, and is tried when the first fails at any step; its reason is then `hardware_failed`. If the
second fails too, the answer is a `decode` failure and the app falls back to Windows' thumbnail.

**Odd decoders.** If a decoder offers none of the pixel formats the video processor takes, the reader's own converter
is used for that file. That is slower, because every frame stepped over is converted, and it is still correct.

**When the graphics device is lost** (driver reset, card removed): the request goes on to the second attempt, and the
device is made again on the next request.

**What reaches the app.** The service counts thumbnails decoded by the graphics card and by the processor since it
started, keeps the last reason, logs the first use of each decoder and every change, and offers all of it through a
small request, `get_media_status`. A thumbnail that comes from the disk cache is not decoded, so it is not counted.

### 4. In the app

- `VideoThumbnailExtractor` (the `MediaComposition` path) is removed. `FileListThumbnailHost` asks the file service
  for a video's thumbnail with its position, and falls back to Windows' thumbnail when the service has none.
- **Queues.** Videos on local drives keep their two slots. Videos on a network drive get one slot of their own, so a
  slow share never takes a local slot. A video thumbnail still waiting for a slot when nobody wants it any more
  (the folder was left, the row scrolled away) is dropped before it starts.
- **The preview pane's frame picker.** The small picture beside the frame choices comes from the same request
  (144 pixels, at the chosen percent). It does not wait in the list's queues: one of the service's four video slots
  is always free for it. Until the picture arrives, and when the service has none, the picker shows the file's icon.
  "Use this frame" keeps working: it stores a percent, and that percent goes with every later request.
- **Settings**, on the page that holds the thumbnail cache:
  ```
  Video thumbnails
  [x] Use the graphics card for video thumbnails
      Since SumaFile started: 212 decoded by the graphics card, 3 by the processor.
      Last processor decode: the graphics card has no decoder for that video's format.
  ```
  The second line appears only after a processor decode. Its reasons are: "the graphics card is turned off above",
  "Windows found no graphics card that can decode video", "the graphics card has no decoder for that video's format"
  and "hardware decoding failed for that video". Before any decode the first line reads "No video thumbnail has been
  decoded since SumaFile started. Thumbnails already in the cache are not decoded again." Changing the switch applies
  to thumbnails made from then on; cached ones stay.

### 5. Tests (what must be proven)

**On Linux (development and CI):**

- The position rule equals `VideoThumbnailFrame.PositionFor` for the same inputs, including a zero duration and a
  position near the end.
- The frame-size rule: rotation swaps the sides, non-square pixels change the shape, the result is never larger than
  the frame, and the box is honoured.
- Pixel rows become the right picture: bottom-up and top-down rows, a row stride wider than the frame, and each
  rotation.
- The stepping rule: it stops at the first frame at or past the position, and after 3 seconds it takes the last frame.
- The attempt table: which attempt runs for each combination of switch, device and failure, and the `decoder` and
  `reason` each ends with. A frame in ordinary memory never reports `hardware`.
- The client: `video_thumbnail` has its own limit and timeout, and the bad-file rules apply.
- The service: video goes to the second client; a video that ends its helper leaves a picture request running in the
  other one; the counts and the last reason; an archive path is refused.
- Core: a video extension goes to the provider with its position; two positions of one file are cached separately;
  a picture's cache key is unchanged.
- The generated video list equals the schema, and `MediaFolder.IsVideo` agrees with it.
- C#: the position sent is the percent the user picked; network and local videos use different slots; a request
  nobody waits for is dropped; the Settings texts.

**On Windows CI (software path, no graphics card):**

- End to end through the helper with a video the test writes itself: an uncompressed AVI whose every frame is one
  known colour. The thumbnail at 25 %, at 1 % and at 99 % has the colour of the frame at that position, which proves
  the seek. An uncompressed AVI needs no codec, so the test does not depend on what the runner has installed.
- The same file with `hardware` on and with it off: both replies say `software`, with a reason. An uncompressed video
  has no decoder at all, so this holds on a PC with a graphics card too.
- A file that is not a video, and a truncated one, answer `decode`.
- **Speed, before the old code goes.** The UI smoke's thumbnail scenario times a folder of generated videos through
  the old in-app path and through the new one, on the runner (which is "a PC without a usable hardware decoder").
  Both figures go in the commit that removes the old extractor. If the new path is slower, the old one is not
  removed and the user decides.

**On a real PC (the user's check, because CI cannot):**

- With the thumbnail cache cleared: a 4K H.264 file, an HEVC or AV1 file if the codec pack is installed, a phone video
  shot upright, and a folder of a few hundred videos. Thumbnails appear and are upright, and Settings counts them as
  decoded by the graphics card.
- The switch turned off and the cache cleared: Settings counts processor decodes, and thumbnails still appear.
- After a restart the thumbnails come from the disk cache at once.

## Checked against Microsoft's documentation

Read in the documentation's source (`MicrosoftDocs/win32`, `desktop-src/medfound`), because none of this can be run
in the development container:

- A decoder that cannot use the graphics device falls back to processor decoding inside Windows, and the caller gets
  no error (*Supporting Direct3D 11 Video Decoding in Media Foundation*, "Fallback to Software Decoding"). This is why
  section 3, step 5 looks at the frame itself.
- A decoder that does use the device hands out frames as Direct3D surfaces (same page, "Allocating Uncompressed
  Buffers"; `MF_SOURCE_READER_D3D_MANAGER`: "the source reader uses the Direct3D device to allocate video buffers").
- The reader's simple converter cannot be combined with a graphics device (`MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING`:
  `MF_SOURCE_READER_D3D_MANAGER` "must be FALSE"). So the two attempts cannot share it, and the design converts the
  chosen frame itself.
- Windows' video processor (`CLSID_VideoProcessorMFT`) takes every pixel format the usual decoders produce (NV12,
  P010, YUY2, I420, YV12, RGB24, RGB32 and more), outputs 32-bit RGB, resizes, and works on the graphics card through
  Direct3D 11.
- All the calls are in version 0.62 of the `windows` crate, which the workspace already builds.

Not stated in the documentation, so settled by the second attempt and by the user's PC check: whether the video
processor, given the device, also takes a frame that sits in ordinary memory.

## Calls I made (the user can overrule)

- **What "hardware" means.** Only a frame the graphics card itself decoded counts. So an old AVI or a format without
  a hardware decoder is counted under the processor, even on a PC with a good graphics card.
- **Counts in Settings**, not a single "in use" line. A folder can mix both kinds, and CI cannot prove the hardware
  path, so the user needs to see real numbers. The Settings lines were not in what the user approved.
- **Video gets its own copy of the media process.** For a short while Task Manager can show two `sumafile-media`
  processes. In return, a video problem never interrupts picture thumbnails.
- **Network videos load one at a time**, through a slot of their own.
- **A queued video thumbnail nobody wants is dropped.** Picture thumbnails keep today's behaviour.
- **The preview pane's frame picture comes from the file service**, on a slot kept free for it. Each frame the user
  tries is kept in the disk cache.
- **Videos inside archives keep their icon**, as today.
- Stepping forward stops after 3 seconds of real time, and the result is cached.
- Two local video thumbnails at a time, and a 30-second timeout.
- A video's position is part of the disk cache key.
- Changing the switch does not clear thumbnails already cached.
- The old in-app extractor is removed, not kept as another fallback, and only if it is not faster (section 5).
- HDR tone mapping is left for later.

## Left to the builder

- The exact Media Foundation calls and attribute values, provided they follow section 3. This includes the order in
  which the decoder's pixel formats are asked for.
- Which `windows` crate version to use of the two already in the lock file.
- How the helper keeps its device and start-up state.
- How the app tells a network drive from a local one.
- Wording of log lines.
- The layout of the uncompressed AVI the Windows test writes, and how the smoke makes its folder of videos.
