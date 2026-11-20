# Column picker and lazy columns

> **Status:** implemented; shipped in 1.1.4. This is a record of the design as
> planned; it was not kept up to date afterwards.

Design agreed in conversation on 2026-11-19. Implementation follows the plan in
`docs/superpowers/plans/2026-11-19-column-picker-and-lazy-columns.md`.

## Intent

In the Details view the user can only choose between five fixed column presets
in Settings, and the columns on offer are the fields the folder listing already
carries. The user wants to right-click in the viewing pane and tick the columns
they want, with more to choose from, and wants values that cost something to
compute (folder size, image dimensions, media duration) worked out on demand
instead of never or always.

## Goals

- A checklist of every column on right-click of a column header or the list
  background, with the presets kept as one-click starting points; the chosen
  set is remembered per pane.
- Three new "free" columns that the directory enumeration already returns:
  Created, Accessed, Attributes. They sort in the service like Name and Size.
- Lazy columns whose values are read per file: Dimensions, Duration, Date
  taken, Camera, Pages, plus the existing folder Size and Items, all through one
  pipeline: requested for the rows on screen (visible first, a small look-ahead),
  streamed back by the service as each value is ready, cancelled when the user
  moves on, and cached so a file is read once.
- Each lazy column is Automatic (fills as rows come on screen) or On request
  (nothing runs until "Calculate for this folder" or a click on the cell).
  Folder size defaults to On request, the rest to Automatic; Automatic is never
  used on network paths.
- Sorting by a lazy column fills the whole folder first (progress in the status
  line, cancellable, capped), then sorts in the app.
- Video duration: Rust parsers for MP4/MOV/M4V and MKV/WebM in the service; for
  anything else the app asks the Windows property system and hands the value to
  the service to cache.

## Non-goals

- Per-folder memory of the column set (Explorer-style). The set is per pane.
- Owner (Windows ACL owner): left out of this round.
- Service-side sorting by lazy columns.
- Changing the List or Tiles views.

## Anti-goals

- Reading every file in a folder because a column is visible: only the rows on
  screen (and the look-ahead), or an explicit request, start a read.
- Work on network shares that the user did not ask for.
- A first navigation that is slower than today: the free columns add no I/O,
  and lazy values never hold the listing back.

## Constraints

- The WinUI host and the Rust service talk over the v1 JSON-RPC/binary
  protocol; adding fields to `FileEntry` means the binary frames change shape
  (version bump) and the schema, generated bindings and goldens follow.
- The service is the only side with a persistent store (`metadata.db`).
- Never add a NuGet package that is not already in the lock files; new Rust
  crates are allowed (mp4, matroska; both MIT).
- The Rust core crate only builds for Windows; pure modules are tested on
  Linux through `#[path]` probes, everything else through CI.

## Approaches considered

1. **Extend the existing enrichment pass per column.** Today folder sizes and
   item counts are filled by walking the first 32 folders of a listing after a
   pause. Adding dimensions and duration the same way would mean more passes
   of the same shape, none of them aware of what is on screen, and nothing
   cached across visits. Rejected.
2. **One lazy-column pipeline modelled on thumbnails** (chosen). Thumbnails
   already request for the rows on screen, cap in-flight work, cancel on
   navigation and cache on disk. Lazy columns use the same shape with one
   streaming service call, readers in the service, a persistent cache for file
   facts and the session cache for folder facts.
3. **Windows property system for everything** (dimensions, duration, pages).
   One API covers every format Windows knows, but it runs in the app through
   COM, is slower, and cannot be cached by the service. Kept only as the
   fallback for formats the Rust parsers do not cover.

## The design

### Columns and the picker

- `ColumnLayout` gains Created, Accessed, Attributes, Dimensions, Duration,
  Date taken, Camera, Pages. Each column knows whether it is free, lazy (which
  fact kind) and which entries can have a value (dimensions: image extensions;
  duration: audio and video; pages: pdf; folder size and items: folders).
- Right-click on a header or the list background (Details view) opens a menu:
  a toggle per column, a Presets submenu, "Calculate for this folder" per
  visible lazy column, and the two size-to-fit items that exist today.
- `UiSettings` gains the visible column ids per pane and the mode per lazy
  column; a preset choice writes the visible list. Saved with the other UI
  settings.

### Free columns

- `FileEntry` gains `created`, `accessed` (same text form as `modified`) and
  `attributes` (Explorer's letters: R H S A C E O T, plus L for a reparse
  point), filled from `WIN32_FIND_DATAW` on Windows and `std::fs::Metadata`
  elsewhere. JSON: optional, omitted when empty. Binary frames: appended to the
  entry encoding; frame version 1 becomes 2, goldens updated on both sides.
- Listing views sort by `created`, `accessed` and `attributes`; the app lists
  them as service sort columns.

### Entry facts (lazy columns)

- IPC: `get_entry_facts { requestId, paths, kinds }` streams
  `entry-facts { requestId, facts: [{ path, kind, text, sort?, state }] }`
  notifications in small batches and answers when every fact is settled or the
  request is cancelled (`cancel_entry_facts { requestId }`). `state` is
  `ready`, `none` (nothing to show for this file), `unsupported` (the app may
  try its fallback) or `failed`. `put_entry_facts { facts }` lets the app cache
  what its fallback found.
- Kinds: `folder_size`, `item_count` (one walk serves both), `dimensions`
  ("6000 × 4000", sort = pixels), `duration` ("1:02:03", sort = seconds),
  `date_taken` (sort = unix ms), `camera`, `pages`.
- Readers in `simplefile-core::entry_facts`: dimensions by header-only
  `ImageReader::into_dimensions`; audio duration by lofty; MP4 family by the
  `mp4` crate, Matroska family by `matroska`; pages by lopdf under the existing
  size cap; date taken and camera by the exif crate.
- Cache: file facts in a new `entry_facts` table of `metadata.db` keyed by
  path, kind, size and modified time. Folder facts in the session cache, which
  the watcher already invalidates.
- Scheduling: facts run on the background scheduler, at most two folder walks
  and four file reads at a time, in the order requested; a cancelled request
  stops starting new facts and drops results.

### The app side

- The Details list reports its visible range to the workspace. A
  `LazyColumnController` per pane turns visible lazy columns in Automatic mode,
  plus explicit requests, into one outstanding `get_entry_facts` per pane
  (visible rows first, then the look-ahead, up to 64 paths), re-issued when the
  range or the columns change and cancelled on navigation. Results land in
  `FileEntry.Facts`; folder size and item count also set `Size` and
  `ItemCount` as today. This replaces the first-32-folders pass.
- Cells of a lazy column show "…" while the entry can have a value and none is
  known; clicking such a cell requests that one fact.
- Sorting by a lazy column: `FillFolderAsync(kind)` fills every candidate row
  (cap 5,000; above it, sort by what is known and say so in the status line),
  with progress and cancel in the status line; the pane re-sorts as the fill
  completes.
- The Windows fallback (`StorageFile.Properties.GetVideoPropertiesAsync` /
  `GetMusicPropertiesAsync`) runs in the app for `duration` answers marked
  `unsupported`, at most two at a time, and sends what it finds to
  `put_entry_facts`.

## Left to the builder

- Exact look of the menu items and the "…" cell.
- Batch sizes and look-ahead counts, within the limits above.
- Whether `attributes` sorting is lexical (chosen: yes, it is a short code).
