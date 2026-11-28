# Media process and more image formats

Date: 2026-11-28. Status: design; the user said to proceed, so this is the working design.

This is the first of two projects. The second, GPU video thumbnails, runs inside the media process built here and
gets its own design.

## Intent

SumaFile makes its own thumbnails for six image formats (JPG, PNG, GIF, WebP, BMP). For every other picture it asks
Windows (`FileListThumbnailHost.cs`, `CanUseWindowsThumbnail`), so a file gets a thumbnail only when that PC has a
codec for it. PSD, EXR, JPEG XL, Krita and DICOM files usually show a plain icon. Camera RAW, TIFF and DDS depend on
which codec packs are installed.

The user wants SumaFile to decode these formats itself, using the decoder set of
[Oculante](https://github.com/woelper/oculante), a Rust image viewer. They chose to **port Oculante's loader into a
SumaFile crate** rather than depend on Oculante, and to run the decoders in a **separate media process** so a bad
file can never take down the app or the file service.

## Goals

1. Thumbnails for these formats, whatever codecs Windows has: PSD and PSB, EXR, Radiance HDR, JPEG XL, TIFF, SVG, DDS,
   TGA, QOI, PNM, Krita, OpenRaster and camera RAW.
2. Quick Look and the preview pane show the same formats at screen size.
3. For these formats SumaFile's decoder goes first. Windows' thumbnail stays as the fallback, then the file icon.
4. JPG, PNG, GIF, WebP and BMP keep their existing fast path inside the file service, unchanged.
5. A file with an embedded preview (camera RAW, PSD) uses it, so its thumbnail is fast.
6. A separate program, `sumafile-media.exe`, does the decoding. The file service starts it the first time a
   thumbnail or preview needs it, and it exits when idle. Nothing runs at app startup.
7. A file that crashes or hangs the media process costs only that file: the service restarts the process, that file
   falls back to Windows' thumbnail or its icon, and other work continues.
8. Thumbnails made this way go into the existing disk thumbnail cache, like the six built-in formats.

## Non-goals (this round)

- GPU video thumbnails. That is the second project, built on this media process.
- HEIC, AVIF and JPEG 2000. They need C libraries with licensing conditions, and Windows' codecs decode HEIC and AVIF
  with hardware. They stay with Windows.
- DICOM. Its library depends on the unmaintained `encoding` crate, which `cargo audit --deny warnings` in the release
  check would refuse without a new exception. It waits until the user decides on that exception.
- Stopping a decode that is no longer wanted. The helper has no cancel message in this round (see "Calls I made").
- Developing camera RAW sensor data. RAW files show the JPEG preview the camera embedded (see Constraints).
- Animation. An animated file shows its first frame.
- Editing, converting or saving images.
- A Settings switch for the decoders. The fallbacks already cover failure.
- Moving the six built-in formats into the media process.

## Anti-goals (failure even if it "works")

- The app or the file service crashing, hanging or running out of memory because of an image file.
- A media process left running, or using CPU, when nothing is being decoded.
- A slower start: no process, thread or file read is added to startup.
- Thumbnails that got slower or changed for the six built-in formats.
- A file that crashes the media process being retried again and again.
- An LGPL or GPL library linked into SumaFile, which is MIT.

## Constraints

- **Licences.** SumaFile is MIT. Every library added must be permissive (MIT, Apache-2.0, BSD, Zlib, MPL-free).
  Oculante itself is MIT. Two libraries it uses are not usable here: `quickraw` (camera RAW, LGPL-2.1) and `dds-rs`
  (LGPL-3.0). So:
  - DDS uses `image_dds` (MIT).
  - Camera RAW shows the embedded JPEG preview, read by SumaFile's own small TIFF walker. Every Rust library that
    develops RAW sensor data is LGPL. The embedded preview is what Explorer shows too.
- **Credit.** Code ported from Oculante keeps a header naming Oculante, its author and the MIT licence, and Oculante's
  licence text ships with SumaFile.
- **No C or C++ libraries** in this round. Every decoder is pure Rust, so the Windows build needs no extra tools.
- **Startup budget unchanged** (`docs/ARCHITECTURE.md`, "Startup budget").
- **No CPU-burning helpers.** The media process works only on thumbnails and previews the user is looking at, runs at
  below-normal priority, and exits when idle.
- **Test-first.** Every behaviour change starts with a failing test.
- The Rust crates build and test on Linux in development. The C# changes need Windows CI, which the user starts from
  the Actions tab.

## Approaches considered

**Where the decoders come from**

- **A. Depend on the `oculante` crate.** Very little code. But its window toolkit is not optional, so SumaFile's 357
  packages would grow toward Oculante's 1,009, and its loader is an internal function that can change in any release.
- **B. Port its loader into a SumaFile crate. Chosen by the user.** Copy `image_loader.rs` (about 1,050 lines), replace
  its app-specific types, depend on the decoder libraries directly, and add what a file manager needs: embedded-preview
  shortcuts, limits and reduced-size output.
- **C. Ask Oculante to publish the loader as its own crate.** Cleanest in the long run, but the timing is not ours. It
  remains a possible follow-up.

**Where the decoders run**

- **In the file service.** Simplest, but complex image formats are where crashes and runaway memory come from, and
  file operations would be exposed to them.
- **In a bundled plugin with a new "thumbnail provider" plugin contract.** General, but it needs a new SDK contract,
  and plugin backends that serve the file service start at launch.
- **In a helper process the file service owns. Chosen.** The service starts `sumafile-media.exe` on first use over
  its standard input and output, restarts it when it fails, and lets it exit when idle. Thumbnails are core behaviour,
  so it ships as part of SumaFile, not as a plugin.

## Design

### 1. Crates

| Crate | Kind | Holds | Used by |
| --- | --- | --- | --- |
| `crates/sumafile-imaging` | lib | The thumbnail pipeline moved out of `simplefile-core/src/thumbnail.rs` (decode of the six formats, `fit`, `scale`, JPEG encode), and the generated list of media-process formats | core, media |
| `crates/sumafile-media-client` | lib, plus a test-only fake helper binary | The helper wire protocol, and the client that starts the helper, sends requests, enforces timeouts and handles crashes. Standard library and `serde` only | service, media tests |
| `crates/sumafile-media` | bin `sumafile-media`, plus lib | The ported decoders, the render functions, and the helper's request loop | shipped beside the service |

`simplefile-core` gains a small `media` module, shaped like `archive::engine`: a `MediaProvider` trait
(`thumbnail(path, size)`, `preview(path, max_dimension)`) and a registry the service fills at start with a provider
backed by the client. Core stays free of transport. The service registers the provider without starting the helper.

### 2. Formats

The list lives in one file, `ipc/schema/v1/media-formats.json`. A generator script writes the Rust list (in
`sumafile-imaging`) and the C# list (`SimpleFile.Core/MediaFormats.Generated.cs`), as `generate-open-with-policy.mjs`
does for the Open With policy, and the repo check fails when a generated file is stale.

| Format | Extensions | Decoder | Notes |
| --- | --- | --- | --- |
| Photoshop | `psd`, `psb` | SumaFile's own reader | A thumbnail uses the embedded JPEG thumbnail (image resource 1036) when it is at least as large as the thumbnail asked for; otherwise the flattened image. The flattened image is read for RGB, greyscale, CMYK and indexed colour, 8 and 16 bits, raw and RLE. The `psd` crate is not used: it rejects PSB and parses every layer |
| OpenEXR | `exr` | `exr` | First RGBA layer, tone-mapped to 8 bits as Oculante does |
| Radiance HDR | `hdr` | `image` | Tone-mapped the same way |
| JPEG XL | `jxl` | `jxl-oxide` | Without the optional C colour library |
| TIFF | `tif`, `tiff` | `tiff`, with Oculante's handling of 16-bit and float samples | |
| SVG | `svg` | `resvg` | Thumbnails only, drawn at the requested size. System fonts load once, on the first SVG. The preview pane keeps WinUI's own vector SVG view |
| DDS | `dds` | `image_dds` | |
| TGA, QOI, PNM | `tga`, `qoi`, `pnm`, `pbm`, `pgm`, `ppm`, `pam` | `image` | |
| Krita, OpenRaster | `kra`, `ora` | `zip` | The `mergedimage.png` stored inside |
| Camera RAW | `dng`, `nef`, `nrw`, `cr2`, `cr3`, `arw`, `srf`, `sr2`, `pef`, `srw`, `orf`, `rw2`, `raf`, `erf`, `3fr`, `mos`, `dcr`, `kdc` | SumaFile's own reader | The largest embedded JPEG preview, turned by its EXIF orientation. See the size rule below |

The decoder is picked by file extension. A file whose contents do not match fails, and the fallback takes over.

Camera RAW reader: most of these are TIFF files. The reader walks the whole IFD chain (IFD0, IFD1, IFD2 and on) and
every SubIFD, collects every JPEG it finds (JPEGInterchangeFormat, a JPEG-compressed single strip, Panasonic's
JpgFromRaw), and takes the one with the most pixels that decodes. RAF reads the preview offset from its header. CR3
reads the preview box from its ISO container. It does not read maker notes.

Size rule for camera RAW: a thumbnail uses the embedded preview when its long side is at least half the size asked
for, and is never enlarged. A preview for the preview pane needs a long side of at least 1,000 pixels. Anything
smaller, or a file with no embedded JPEG, fails, and Windows' thumbnail takes over.

What is verified: the reader is tested with synthetic containers built in the tests, because the repository has no
real camera files. By the layout of the formats it should serve DNG, NEF, CR2, CR3, ARW, RW2, RAF and PEF. Formats
that keep their only large preview in a maker note (ORF, and some ERF and SRW files) or as uncompressed pixels (MOS,
some DCR, KDC and 3FR files) fail here and fall back to Windows. Real files of each maker are for the user to check
on Windows.

### 3. The media process

- `sumafile-media.exe` sits beside `simplefile-service.exe`. The service finds it next to its own executable; the
  environment variable `SUMAFILE_MEDIA_HELPER` overrides the path for tests and development. When it is missing, the
  provider answers "unsupported" and the fallbacks apply, so a build without the helper still works.
- Started on the first request with piped standard input and output, no window, below-normal priority. On Windows it
  is put in a job object with a 3 GiB memory limit that also ends it if the service dies. It also exits when its
  input closes.
- The client ends it after 120 seconds with no request in flight, and starts it again on the next request. The
  client does this, not the helper: only the client knows that no request is on its way, so a request can never
  meet a helper that is just leaving.
- **Wire format** on the pipes, both directions: `u32 LE header length | header (UTF-8 JSON) | u32 LE body length |
  body`. Headers are at most 64 KiB and bodies at most 256 MiB.
  - `{ "id", "method": "thumbnail", "path", "size" }` → `{ "id", "ok": true, "format": "jpeg", "width", "height" }`
    with the JPEG as the body.
  - `{ "id", "method": "preview", "path", "maxDimension" }` → the same shape; `format` is `jpeg` for an opaque
    picture and `png` for one with transparency.
  - A failure is `{ "id", "ok": false, "kind": "unsupported" | "too_large" | "decode" | "io", "error": "<text>" }`.
- The client sends at most four thumbnail requests and two preview requests at a time; further calls wait in the
  client. A request's timeout therefore measures its decode, never time spent queued. The helper runs each request on
  its own thread. Replies carry the request's id and may arrive in any order.
- **Limits inside the helper:** a decode may use at most 1 GiB of pixels (width × height × channels × bytes per
  sample); beyond that the answer is `too_large`. An SVG is never drawn larger than the requested size.
- **Timeouts in the client:** 20 seconds for a thumbnail, 60 seconds for a preview. A request that times out ends the
  helper.
- **When the helper dies or is ended with requests in flight:** if one request was in flight, that file is recorded as
  bad. If several were, each is retried once with nothing else in flight, and a file that fails alone is recorded as
  bad. A bad file is remembered by path, size and modified time for the life of the service (at most 1,024 entries)
  and answers "unsupported" at once, so the fallback applies and the helper is not started for it again.

### 4. Thumbnails

- `preview::generate_thumbnail` accepts the six built-in extensions, as now, and every media-process extension. For
  the latter it asks the registered provider instead of calling the in-process pipeline. The disk cache check before
  and the cache write after are the same code for both.
- **Its own lane in the service.** A thumbnail of a media-process format waits on the helper, which is not work for
  the service's cores. It runs on a new media lane of the scheduler (four permits) instead of the background lane, so
  slow PSDs never hold the permits JPG thumbnails, folder sizes and scans use. Like background work, it waits while
  foreground work runs.
- The helper's `thumbnail` renders with the same pipeline as the built-in formats (`sumafile-imaging`): decode, turn by
  orientation, fit, scale, encode as JPEG. Transparent pictures are flattened onto white.
- Files inside archives work as they do today: the service resolves the entry to a temporary file and passes that path.
- The app: `FileListThumbnailHost.CanUseAppThumbnail` becomes the six built-ins plus the generated list. The order is
  already "app thumbnail, then Windows' thumbnail, then icon".
- **Its own gate in the app.** Media-process formats load through their own gate, two at a time, as video thumbnails
  already do. They never take one of the four slots the built-in formats and Windows' thumbnails share.

### 5. Preview pane and Quick Look

- New IPC method `render_image_preview { path, maxDimension }` with a binary result frame (tag 10) carrying the format,
  width, height and encoded bytes, and a base64 JSON form for a client that did not ask for binary frames. It runs on
  the media lane's preview permits (two), not as foreground work: a preview can wait on the helper for seconds, and
  foreground work makes every background job pause.
- `PreviewPresenter.Image`: for a media-process format other than SVG it asks the service first (`maxDimension` 2560)
  and shows the result. If that fails it continues with today's path (Windows' thumbnail, then Windows' codecs). SVG
  keeps WinUI's vector view, which stays sharp at any zoom.
- `preview.rs` and the C# preview lists classify `psd`, `psb`, `kra`, `ora` and the added RAW extensions as images.

### 6. Packaging

- `scripts/build-winui-release.ps1` builds `sumafile-media` and copies `sumafile-media.exe` beside the service. The
  required-file lists, the process lists the scripts stop before replacing files, the MSI and NSIS smoke checks and
  `scripts/check-winui-packaging.mjs` include it.
- Oculante's MIT licence text ships with SumaFile, next to the other third-party licence texts.

### 7. Tests (what must be proven)

- **Each format decodes.** A fixture per format, generated in the test where an encoder or a hand-written minimal file
  is practical (EXR, HDR, TIFF, TGA, QOI, PNM, SVG, Krita, OpenRaster, DDS, PSD, PSB, and synthetic RAW containers
  with an embedded JPEG; JPEG XL through `zune-jpegxl`, a pure-Rust encoder used only by the tests). Each test checks
  size and a few known pixels.
- **Embedded previews are used:** a PSD and a RAW container whose embedded preview differs visibly from the main image
  produce the preview's pixels.
- **Limits:** an image over the pixel budget answers `too_large`; a truncated or wrong-format file answers `decode`.
- **Client against the fake helper:** request and reply matching out of order, calls beyond the gate wait without
  timing out, timeout ends the helper, a crash with
  one request records the file as bad, a crash with several retries each alone, a bad file answers at once without
  starting the helper, an idle helper is ended and started again, a missing helper answers "unsupported".
- **Client against the real helper:** thumbnail and preview end to end for two formats.
- **Core:** a media-process extension goes to the provider and its result is written to and read from the disk cache;
  a built-in extension never reaches the provider; with no provider registered the extension is unsupported.
- **Unchanged pipeline:** the existing thumbnail tests pass after the move to `sumafile-imaging`.
- **Generated lists:** the Rust and C# lists equal the JSON.
- **C# logic:** which extensions ask for an app thumbnail, and the preview order for a media-process format, testable
  without WinUI.

## Calls I made (the user can overrule)

- The media process is a helper the file service owns, not a plugin.
- Camera RAW shows the embedded preview and DDS uses `image_dds`, to keep LGPL code out of SumaFile.
- HEIC, AVIF and JPEG 2000 stay with Windows.
- No Settings switch in this round.
- Previews are rendered to at most 2560 pixels on the long side.
- Transparent pictures are flattened onto white in thumbnails and keep their transparency in previews.
- The numbers above: four thumbnails and two previews at a time, 20 and 60 second timeouts, 120 seconds idle, 1 GiB
  of pixels, a 3 GiB process limit, 1,024 remembered bad files.

After the builder check (a reviewer read this design as the builder would):

- **Separate lanes, no cancel.** Media-process formats have their own gate in the app and their own lane in the
  service, so a slow file never delays a JPG thumbnail. There is no cancel message: after leaving a folder, the
  thumbnails already asked for (at most 128, the app's existing limit) finish two at a time at below-normal priority
  and land in the disk cache. A cancel message is the follow-up if that proves wasteful.
- **Camera RAW** uses one generic reader and the size rule in section 2. No maker-note parsing.
- **Photoshop** uses SumaFile's own reader, which also reads PSB.
- **DICOM is deferred**, to keep the security audit gate as it is.
- **SVG previews** stay with WinUI's vector view.

## Left to the builder

- The module layout inside each crate.
- How much of Oculante's loader is kept as-is for each format, as long as the ported parts carry the credit header.
- The exact shape of the C# preview change, following `PreviewPresenter`'s existing structure.
- Message wording, following existing thumbnail and preview messages.
