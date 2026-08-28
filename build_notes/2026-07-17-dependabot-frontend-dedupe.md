# 2026-07-17 Dependabot Frontend Dedupe

Issue fixed:
- `build_notes/2026-07-17-next-recommended-work.md` issue 8:
  `.github/dependabot.yml` still had two identical `/frontend` npm entries.

## Problem

Dependabot had duplicate weekly npm update rules for `directory: "/frontend"`.
That could create noisy or confusing dependency update behavior.

## Files Changed

### `.github/dependabot.yml`

Changes:
- Removed the duplicate `/frontend` npm update block.
- Kept one weekly `/frontend` npm rule with the existing `dependencies` and
  `npm` labels.

### `scripts/check-github-workflows.mjs`

Changes:
- Added `requireOccurrenceCount()` for exact snippet counts.
- Added a guard requiring `directory: "/frontend"` to appear exactly once in
  `.github/dependabot.yml`.

## Validation Passed

- Counting `directory: "/frontend"` occurrences in `.github/dependabot.yml`
  returned `1`.
- `npm run check:workflows` passed.
- `git diff --check` passed.
