# Feature Opportunities

This branch should prioritize Windows-native file-manager improvements.

## Candidate Work

- Richer mapped network drive display and error states. (Done: offline/stale
  badges, probe timeout, refresh, reconnect dialog.)
- Better progress messaging for long local transfers. (Done: bytes/total,
  rate, ETA, cancelling feedback, size preflight.)
- More archive formats and safer extraction previews. (Done: metadata summaries now cover ZIP/TAR families and keep archive fields grouped in preview/properties surfaces; remaining opportunity is interactive archive preview actions.)
- More metadata panels for media and document files. (Done: preview now groups image/audio/video/PDF/Office plus data/archive/font/ebook/email/calendar/contact/certificate fields into focused sections with fixture coverage; remaining opportunity is richer per-format visual smoke coverage.)
- Rendered previews for Markdown/data documents. (Done: preview exposes a persisted opt-in rendered view for Markdown, HTML, JSON/JSONL, CSV/TSV, XML/XAML, YAML, TOML, AsciiDoc, and reStructuredText using a sanitized WebView document.)
- Broader image and video preview support. (Done: image previews are automatic across common, modern, and RAW-oriented extensions; video playback remains a persisted opt-in while still showing frame controls/poster extraction by default.)
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
