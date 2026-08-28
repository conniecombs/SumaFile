# 2026-07-17 Markdown Preview Sanitization

Issue:
- Audit issue 4, Markdown preview rendered unsanitized HTML.

Goal:
- Keep Markdown preview useful while treating local `.md` and `.markdown` files
  as untrusted input.
- Prevent raw Markdown HTML from injecting scripts, event handlers, layout
  styles, images, or JavaScript URLs into the app surface.

## Changes Made

### Shared Markdown Renderer

Files changed:
- `frontend/src/lib/markdownPreviewSecurity.mjs`
- `frontend/src/lib/markdownPreviewSecurity.d.mts`
- `frontend/src/lib/components/preview-pane/PreviewContent.svelte`

Implementation details:
- Added `sanitize-html` and `@types/sanitize-html`.
- Added `renderSafeMarkdown(markdown)`.
- The helper renders Markdown with `marked`, then sanitizes the generated HTML.
- The preview component now calls `renderSafeMarkdown(preview.content)` instead
  of calling `marked.parse` directly.

Allowed Markdown preview tags:
- headings, paragraphs, blockquotes, lists, code/pre, emphasis/strong, tables,
  links, horizontal rules, line breaks, and deleted text.

Allowed attributes:
- Links may keep `href` and `title`.

Allowed link schemes:
- `http`
- `https`
- `mailto`

Explicitly not allowed:
- `<script>`
- inline event handlers such as `onclick` and `onerror`
- `javascript:` URLs
- inline styles
- image tags
- arbitrary layout tags such as `<div>`

### Regression Check

Files changed:
- `frontend/scripts/check-markdown-preview-safety.mjs`
- `frontend/package.json`

Implementation details:
- Added `npm --prefix frontend run check:markdown-preview-safety`.
- The check imports the same `renderSafeMarkdown` helper used by the app.
- It feeds unsafe Markdown containing script tags, event handlers, a
  `javascript:` link, an image tag, and styled layout HTML.
- It verifies safe Markdown still renders and unsafe HTML is stripped.
- The full frontend `check` script now runs this regression.

## Validation Passed

- `npm --prefix frontend run check:markdown-preview-safety`
- `npm run check`
- `cargo fmt --all -- --check`
- `cargo test --locked --all-features`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`
- Targeted search confirming Markdown preview no longer calls `marked.parse`
  directly.
