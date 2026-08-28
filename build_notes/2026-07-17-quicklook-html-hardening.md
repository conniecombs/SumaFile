# 2026-07-17 QuickLook HTML Hardening

Issue partially fixed:
- `build_notes/2026-07-17-next-recommended-work.md` issue 4:
  live frontend APIs still accepted raw HTML strings.

## Problem

QuickLook still accepted `legacyContent?: Node | string | null`. When callers
passed a string, `QuickLookModal.svelte` assigned it to `innerHTML`. Current app
flows use typed preview data instead of `legacyContent`, so the string path was
unused compatibility surface with unnecessary injection risk.

## Files Changed

### `frontend/src/lib/components/quick-look/QuickLookModal.svelte`

Changes:
- Removed the `legacyContent` prop.
- Removed the effect that wrote string `legacyContent` to `contentElement.innerHTML`.
- Rendered preview states directly from typed `preview` data.

### `frontend/src/lib/components/quick-look.ts`

Changes:
- Removed `legacyContent` from `QuickLookProps`.

### `frontend/scripts/check-html-sink-safety.mjs`

Changes:
- Added an active-source raw HTML sink allowlist.
- Excluded generated Svelte audit bundles from the live-source scan.
- Required QuickLook source and wrapper files to stay free of `legacyContent`
  and `innerHTML`.
- Preserved explicit allowlist entries for existing reviewed sinks:
  - Static legacy overlay markup.
  - Generic modal body HTML.
  - Sanitized markdown/code preview HTML.

### `frontend/package.json`

Changes:
- Added `check:html-sink-safety`.
- Added `check:html-sink-safety` to the frontend `check` chain.

## Deliberately Not Changed

- Generic modal HTML strings remain in `showHtmlDialog` and `ModalBody.svelte`.
  They are now allowlisted and should be handled in a separate, broader modal
  hardening pass.
- Generated Svelte audit bundles may still contain older compiled QuickLook
  snapshot text. They are not live runtime source and are excluded from the new
  active-source sink guard.

## Validation Passed

- Active frontend source search for `legacyContent` returned no matches outside
  generated Svelte audit bundles.
- Active frontend source search for `contentElement.innerHTML` returned no
  matches outside generated Svelte audit bundles.
- `npm --prefix frontend run check:html-sink-safety` passed.
- `npm --prefix frontend run check:svelte` passed.
- `npm run check` passed.
- `git diff --check` passed.
