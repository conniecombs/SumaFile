# Design specifications, plans and review notes

This folder holds the design specifications (`specs/`), implementation plans
(`plans/`) and review notes (`notes/`) written while features were built. They
are kept as records of what was planned and decided, and they are not
maintained: each file describes the design as it stood when it was written,
its paths and names may no longer exist, and the task boxes in the plans were
not kept up to date. For what SumaFile does today, read the
[README](../../README.md), the [changelog](../CHANGELOG.md) and the code.

Every file has a status line under its title. The table lists the files by
date; the versions are SumaFile versions as recorded in the changelog.

| File | Covers | State |
| --- | --- | --- |
| [plans/2026-09-18-winui-option3-performance.md](plans/2026-09-18-winui-option3-performance.md) | Lazy drive probes, the virtualized Details list and a startup budget rule | Implemented in part; shipped in 1.0.1. Tasks 1 and 2 shipped. Task 3 was not carried out as written: the startup budget still allows a drive refresh after a minimum delay. |
| [specs/2026-09-21-ftp-sftp-manager-design.md](specs/2026-09-21-ftp-sftp-manager-design.md) | Design of the FTP/SFTP manager | Implemented; shipped in 1.0.1. Superseded in 1.1.0, when the feature moved into the bundled FTP/SFTP plugin (`plugins/remote`): the paths and IPC methods it names no longer exist. |
| [plans/2026-09-21-ftp-sftp-manager.md](plans/2026-09-21-ftp-sftp-manager.md) | Plan for the FTP/SFTP manager, 7 tasks | Implemented; shipped in 1.0.1. Superseded in 1.1.0 like its design. |
| [specs/2026-11-06-ux-recommendations.md](specs/2026-11-06-ux-recommendations.md) | The 1.0.4 UX changes: menus, shortcuts, omnibar, dialogs, transfers, settings | Implemented; shipped in 1.0.4. |
| [plans/2026-11-06-ux-recommendations.md](plans/2026-11-06-ux-recommendations.md) | Plan for the 1.0.4 UX changes, 6 tasks | Implemented; shipped in 1.0.4. |
| [specs/2026-11-07-plugin-system-design.md](specs/2026-11-07-plugin-system-design.md) | Design of the plugin system and of moving Git, FTP/SFTP and 7-Zip into bundled plugins | Implemented through phase 5; shipped in 1.1.0. Where it and the code or [PLUGINS.md](../PLUGINS.md) differ, the code is right. |
| [plans/2026-11-07-plugin-system-phase-1.md](plans/2026-11-07-plugin-system-phase-1.md) | Phase 1: plugin SDK, plugin host, consent and Settings > Plugins, 17 tasks | Implemented; shipped in 1.1.0. The plugin, plugin-data and approval locations under `%LOCALAPPDATA%` that it names were moved into the installation folder in phase 4. |
| [plans/2026-11-08-plugin-system-phase-2-sevenzip.md](plans/2026-11-08-plugin-system-phase-2-sevenzip.md) | Phase 2: the 7-Zip plugin, 13 tasks | Implemented; shipped in 1.1.0. The plugin, plugin-data and approval locations under `%LOCALAPPDATA%` that it names were moved into the installation folder in phase 4. |
| [plans/2026-11-09-plugin-system-phase-3-remote.md](plans/2026-11-09-plugin-system-phase-3-remote.md) | Phase 3: the FTP/SFTP plugin and the `sumafile-transfer` crate, 15 tasks | Implemented; shipped in 1.1.0. The plugin, plugin-data and approval locations under `%LOCALAPPDATA%` that it names were moved into the installation folder in phase 4. |
| [plans/2026-11-09-plugin-system-phase-4-git.md](plans/2026-11-09-plugin-system-phase-4-git.md) | Phase 4: installation-folder locations, panels, row decorations and the Git plugin, 16 tasks | Implemented; shipped in 1.1.0. |
| [plans/2026-11-10-plugin-system-phase-5-third-party.md](plans/2026-11-10-plugin-system-phase-5-third-party.md) | Phase 5: plugin template, author guide, packaging dry runs and the signing tool, 8 tasks | Implemented; shipped in 1.1.0. As planned, the SDK package and the Rust helper crate are not published. |
| [notes/2026-11-07-plugin-system-phase-1-review-notes.md](notes/2026-11-07-plugin-system-phase-1-review-notes.md) | Review findings, rulings and closures for the plugin system | Closed record. Despite its name it covers phases 1 to 5, all shipped in 1.1.0. |
| [specs/2026-11-19-column-picker-and-lazy-columns-design.md](specs/2026-11-19-column-picker-and-lazy-columns-design.md) | Design of the Details view column picker, the three new free columns and the columns read on demand | Implemented; shipped in 1.1.4. |
| [plans/2026-11-19-column-picker-and-lazy-columns.md](plans/2026-11-19-column-picker-and-lazy-columns.md) | Plan for the column picker and on-demand columns, 11 tasks | Implemented; shipped in 1.1.4. |
