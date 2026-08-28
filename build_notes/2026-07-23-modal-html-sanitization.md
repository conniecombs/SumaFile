# 2026-07-23 Modal HTML Sanitization

Issue fixed:
- `build_notes/2026-07-17-next-recommended-work.md` issue 4:
  generic modal HTML strings remained a reviewed raw-HTML boundary after the
  QuickLook string path was removed.

## Problem

`showHtmlDialog()` inserted `bodyHtml` with `innerHTML`, and
`ModalBody.svelte` rendered the same kind of content with `{@html}`. Current
call sites mostly escaped dynamic text, but the shared renderer still trusted
every future caller to do the right thing.

## Files Changed

### `frontend/src/lib/modalHtmlSecurity.mjs`

Changes:
- Added `sanitizeModalHtml()` with an allowlist for SimpleFile modal markup:
  form controls, labels, lists, tables, metadata blocks, classes, IDs, selected
  ARIA/data attributes, and narrowly constrained inline style values.
- Blocks scripts, event-handler attributes, unsafe URL-bearing elements, and
  unsafe inline style properties.

### `frontend/src/lib/app/core.ts`

Changes:
- `showHtmlDialog()` now sanitizes `bodyHtml` before assigning to
  `innerHTML`.

### `frontend/src/lib/components/modal-body/`

Changes:
- `ModalBody.svelte` renders sanitized HTML.
- `modal-body.ts` also sanitizes before mounting the Svelte component.

### `frontend/scripts/check-html-sink-safety.mjs`

Changes:
- Updated the allowlist to accept only sanitized modal HTML sinks.
- Added behavior checks that prove `sanitizeModalHtml()` strips scripts, event
  handlers, unsafe URL-bearing elements, and unsafe style properties while
  preserving reviewed modal form/table markup.

### Documentation

Changes:
- Updated README verification notes.
- Updated security, UI/backend review, and Svelte migration boundary docs.

## Validation Passed

Commands:

```powershell
npm --prefix frontend run check:html-sink-safety
npm run check
npm run check:rust
cargo tauri build --ci --config tauri.local.conf.json --bundles nsis
npm run smoke:release
git diff --check
```

The NSIS build produced:

```text
src-tauri/target/release/bundle/nsis/SimpleFile_1.1.0_x64-setup.exe
```
