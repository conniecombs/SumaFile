# Feature Opportunities

This branch should prioritize Windows-native file-manager improvements.

## Candidate Work

- Richer mapped network drive display and error states. (Done: offline/stale
  badges, probe timeout, refresh, reconnect dialog.)
- Better progress messaging for long local transfers. (Done: bytes/total,
  rate, ETA, cancelling feedback, size preflight.)
- More archive formats and safer extraction previews.
- More metadata panels for media and document files. (Properties now covers PDF, audio tags, MP4-family video, and Office package props via `get_file_metadata`; further preview-pane surfaces remain open.)
- Better keyboard navigation through dual-pane workflows. (Done: Tab/Alt+1/2
  pane focus, Ctrl+Alt+C/M cross-pane transfer, active-pane chrome + status.)
- Named workspace profiles for panes, tabs, views, columns, sorting, chrome
  visibility, widths, Git visibility, and queue preference. (Done: built-in
  Standard/Developer/Photos/Transfer/Minimal profiles plus save, apply,
  duplicate, rename, overwrite, export, reset, and delete.)
- Stronger installer smoke coverage for NSIS and MSI. (Nightly + manual
  workflow: `.github/workflows/installer-smoke.yml`.)

## Non-Goals

- Provider-backed browsing or mount management.
- Linux-only desktop integration work.
- macOS or Linux release artifacts.
