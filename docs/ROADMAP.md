# Roadmap

This roadmap tracks the Windows-focused SumaFile release.

## Current Release Priorities

- Keep local file navigation fast and reliable.
- Preserve Windows drive labels, mapped network share names, and native drive types.
- Keep folder openings inside SumaFile unless the user explicitly opens a file externally.
- Maintain dual-pane, tabs, Quick Access, bookmarks, recent locations, search, smart folders, previews, metadata, checksums, labels, and archive workflows.
- Keep updater metadata and Windows installer outputs reliable.

## Near-Term Work

- Broaden smoke coverage for settings startup, updater metadata, MSI artifacts, and NSIS install/uninstall.
  Installer package smoke runs nightly and on demand via `.github/workflows/installer-smoke.yml`.
- Add targeted tests around mapped network drive display names.
- Improve large-folder progress and cancellation visibility.
  Transfer progress now shows bytes/total, rate, ETA, and cancelling state.
- Continue tightening archive path validation and extraction safety.
- Expand file metadata support while keeping preview limits conservative.

## Release Work

- Windows x64 CI artifact build.
- NSIS and MSI installer verification.
- Updater JSON and signature verification.
- Documentation sweep for local-file, archive, preview, metadata, Git, cleanup, and Windows installer behavior.

## Out Of Scope For This Branch

- App-managed provider integrations.
- Provider-backed mount management.
- Linux-only desktop integration internals.
- macOS or Linux installer targets.
