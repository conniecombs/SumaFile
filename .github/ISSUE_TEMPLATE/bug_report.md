---
name: Bug Report
about: Report a reproducible bug or unexpected behavior
title: "[Bug] "
labels: bug
assignees: ""
---

## Environment

SumaFile is currently supported on Windows only. Please include the
Windows-specific details below so the issue can be reproduced against the
supported release target.

| Field | Value |
|-------|-------|
| **Windows version/build** | <!-- e.g. Windows 11 24H2 build 26100 --> |
| **SumaFile version** | <!-- from Settings > About, or the release page --> |
| **Install/source** | <!-- e.g. NSIS installer, MSI installer, portable zip, `npm run dev:winui` --> |
| **Affected storage** | <!-- e.g. local NTFS path, mapped network drive, UNC path, removable drive, FTP/SFTP connection --> |
| **Plugin involved** | <!-- Git, FTP/SFTP, 7-Zip, a third-party plugin, or none --> |
| **Rust/.NET version** | <!-- For local/dev builds only; otherwise write N/A --> |

---

## Steps to Reproduce

1.
2.
3.

---

## Expected Behavior

<!-- What you expected to happen -->

## Actual Behavior

<!-- What actually happened. Include error messages, screenshots, or log output if available. -->

---

## Additional Context

<!-- Anything else that might be relevant: file types involved, directory size, network drives, etc. -->

<!-- Logs, with personal paths removed (docs/SUPPORT.md says what each holds):
     %LOCALAPPDATA%\SumaFile\startup.log, service.log and operations.jsonl,
     and plugins.log in the installation folder. For a failed copy or move, the
     failed line of operations.jsonl is the most useful item. -->
