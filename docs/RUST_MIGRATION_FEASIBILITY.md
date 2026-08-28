# Rust Migration Feasibility Analysis

Historical note: this document evaluates a possible future replacement of the
Tauri WebView frontend with a native Rust GUI. It is not part of the current
Windows-only release process. Current release documentation lives in
`README.md`, `.github/RELEASE.md`, and `docs/UPDATER_RELEASE.md`.

## Summary

**Verdict: Feasible, but represents a substantial frontend rewrite.**

The backend is already 100% Rust. Migration to "pure Rust" means replacing the
Tauri WebView + HTML/CSS/JavaScript frontend (~7,376 LOC) with a native Rust GUI
framework. The core logic stays untouched; only the presentation layer changes.

---

## Current Architecture

| Layer | Technology | LOC |
|---|---|---|
| Backend (file ops, archive, thumbnails, git, watcher) | Rust (Tauri 2) | 1,812 |
| Frontend — JavaScript | Vanilla ES6 modules | 4,729 |
| Frontend — CSS | Custom properties, theming | 2,157 |
| Frontend — HTML | Semantic HTML5 | 490 |
| **Total** | | **9,188** |

The Rust backend in `src-tauri/src/lib.rs` already implements all business logic:
30+ Tauri commands covering directory listing, copy/move/delete, file watching,
archive creation/extraction, thumbnail generation, git status, search, and
platform-specific disk enumeration. **None of this needs to change.**

---

## What "Pure Rust" Requires

Removing Tauri's WebView means replacing the frontend with a native Rust GUI
toolkit. The backend Rust code can be reused almost unchanged — the IPC layer
(Tauri `invoke` calls) would be replaced with direct function calls.

### Rust GUI Framework Options

| Framework | Paradigm | Maturity | Styling | Notes |
|---|---|---|---|---|
| **egui** | Immediate mode | High | Custom style structs | Best fit; many desktop tools use it |
| **Iced** | Elm/MVU | Medium | CSS-like themes | Good async support, still maturing |
| **Slint** | Declarative (.slint files) | Medium | CSS-like | Commercial backing; GPL/commercial license |
| **gtk4-rs** | Native widgets | High (Linux) | GTK CSS | Linux-first; macOS/Windows support is secondary |
| **Tauri + Leptos/Yew (WASM)** | Reactive web (WASM) | Medium | Full CSS | Not truly "pure Rust" — still runs in WebView |

**Recommended: egui** for pragmatic delivery. **Iced** is a reasonable alternative
if a more structured MVU architecture is preferred.

---

## Feature-by-Feature Migration Difficulty

### Easy (direct equivalents exist)

| Feature | Current | Rust equivalent |
|---|---|---|
| File list display | DOM table rows | egui `TableBody` / Iced `Column` |
| Breadcrumb navigation | HTML spans | Sequence of egui `Button`s |
| Sorting (name/size/date/type) | JS array sort | Identical Rust sort logic |
| Keyboard shortcuts | `keydown` listeners | egui `input.key_pressed` |
| Toast notifications | JS DOM injection | egui `Window` overlays |
| Settings persistence | `localStorage` JSON | `serde_json` to config file |
| Dark/light theme | CSS variables | egui `Visuals` struct |

### Moderate (possible but requires work)

| Feature | Current | Rust equivalent | Effort |
|---|---|---|---|
| Virtual scrolling | Custom JS viewport math | egui `ScrollArea` with manual clipping | Medium |
| Grid view (icon grid) | CSS `grid` + JS columns | egui `Grid` or manual row layout | Medium |
| Folder tree sidebar | Recursive HTML rendering | egui collapsible `CollapsingHeader` | Medium |
| Tab management | JS tab state + DOM | egui `TabBar` (via `egui_dock`) | Medium |
| Progress dialogs | HTML modal + JS | egui modal `Window` with channel updates | Medium |
| Context menus | HTML positioned `<ul>` | `egui_context_menu` crate | Medium |
| Internationalisation | 190-LOC JS locale map | `rust-i18n` or `fluent-rs` crate | Medium |
| Bookmarks/recent panel | JS state + HTML list | egui side panel | Low-Medium |

### Hard (significant effort, some visual regression likely)

| Feature | Current | Rust equivalent | Notes |
|---|---|---|---|
| Drag-and-drop (file move/copy) | HTML5 native DnD | `egui-dnd` crate (limited) | DnD in native GUIs is less polished than browser |
| Adjustable icon size (slider) | CSS variable + JS slider | egui `Slider` → rerender | Virtual scroll must recalculate column count |
| Animated transitions | CSS transitions/animations | Not directly available in egui | egui is immediate-mode; no built-in animation |
| Complex CSS theming (2,157 LOC) | Full CSS cascade | egui `Style`/`Visuals` | Not 1:1; styling is less expressive |
| Thumbnail lazy loading | `IntersectionObserver` | Manual viewport intersection | Needs custom implementation |

---

## What Stays the Same

The following Rust dependencies are **framework-agnostic** and carry over
without modification:

- `notify` — file watching
- `trash` — safe deletion
- `walkdir` — recursive traversal
- `zip`, `tar`, `flate2`, `unrar` — archive support
- `image` + `base64` — thumbnail generation
- `chrono` — date formatting
- `tokio`, `parking_lot` — async runtime and locking
- `glob` — pattern matching
- `serde` / `serde_json` — serialization
- `libc` / `winapi` — platform disk space APIs

The `AppState` struct, all `FileEntry`/`DirectoryListing`/`ProgressUpdate` types,
and the file operation functions in `lib.rs` are all reusable directly.

---

## Binary and Runtime Impact

| Metric | Current (Tauri) | Pure Rust (egui) | Direction |
|---|---|---|---|
| Binary size | ~8–15 MB + system WebView | ~5–10 MB (self-contained) | Smaller |
| Startup time | WebView init + JS parse | Near-instant | Faster |
| Memory usage | WebView overhead (~50–100 MB) | ~20–40 MB | Lower |
| Rendering | GPU-accelerated (WebView) | GPU via wgpu/glow (egui) | Comparable |
| CSS flexibility | Full CSS | Limited style structs | Regression |

---

## Effort Estimate

| Area | Estimated new LOC | Notes |
|---|---|---|
| Main window + layout | ~500 | Top bar, sidebar, file area, status bar |
| File list (list + grid views) | ~600 | Virtual scroll, sort, selection |
| Folder tree | ~300 | Recursive expand/collapse |
| Dialogs (create, rename, delete, extract, progress) | ~400 | egui modal windows |
| Context menu | ~200 | Right-click actions |
| Settings panel | ~200 | Preferences, theme toggle |
| Bookmarks + recent locations | ~150 | Side panel items |
| Drag-and-drop | ~200 | With `egui-dnd` |
| i18n glue | ~150 | Replace 190-LOC JS module |
| Tab management | ~200 | With `egui_dock` |
| **Total estimate** | **~2,950 LOC** | Replaces ~7,376 LOC of JS/HTML/CSS |

The pure Rust UI will be more concise than the web frontend because Rust GUI
frameworks handle layout more declaratively and CSS boilerplate disappears.

---

## Risks

1. **Drag-and-drop quality** — HTML5 DnD is more mature than any Rust GUI DnD
   implementation. Users may notice regressions in drag behaviour.

2. **Visual polish** — 2,157 lines of CSS encode spacing, typography, icons, and
   theming. Reproducing that fidelity in egui style structs is possible but tedious.

3. **egui ecosystem completeness** — Some widgets (tabs with close buttons, split
   panes, column-resize handles) require third-party crates (`egui_dock`,
   `egui-dnd`) that are less battle-tested than browser primitives.

4. **Iced maturity** — If choosing Iced, `async` command integration is solid but
   the widget library is still smaller than browser DOM.

5. **Accessibility** — Web accessibility (screen readers, ARIA) is lost. Native
   GUI accessibility in Rust frameworks is currently limited.

---

## Recommended Approach

1. **Keep `lib.rs` entirely** — no changes to the backend.
2. **Drop Tauri** — replace with `eframe` (egui's application shell) or `winit`
   for the window loop.
3. **Adopt egui** for the UI with `egui_dock` (tabs) and `egui-dnd` (drag-drop).
4. **Migrate incrementally**: start with the file list view, then add the tree,
   then dialogs, then drag-drop.
5. **Use `rust-i18n`** to replace the JS locale map.
6. **Retain all existing Cargo dependencies** — they are already framework-agnostic.

### Alternative: Tauri + Leptos (WASM)

If retaining CSS-level visual polish is a priority, replacing JavaScript with
Leptos/Yew compiled to WASM keeps the WebView but eliminates JS. This is a
smaller migration with less visual regression, though it is not "pure Rust" in
the binary sense (the WebView runtime is still present).

---

## Conclusion

| Dimension | Rating |
|---|---|
| Technical feasibility | **High** — backend already done, all deps reusable |
| UI feature parity | **Medium** — most features possible, drag-drop and CSS polish regress |
| Overall effort | **Moderate** — ~3,000 new LOC replacing ~7,400 LOC of web frontend |
| Risk level | **Low–Medium** — main risks are DnD quality and visual polish |

Migration is well within reach. The biggest payoff is eliminating the WebView
dependency (smaller binary, faster startup, lower memory). The main cost is
rebuilding the UI layer and accepting that some CSS-driven polish will not
translate directly to a native GUI framework.
