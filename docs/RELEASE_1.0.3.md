# SumaFile 1.0.3 Release Notes

Prepared November 6, 2026. This Windows x64 release brings one address, search and filter box, lets a transfer finish the rest of a selection when a file fails, and keeps the file browser responsive while slower operations run. The [source comparison](https://github.com/conniecombs/SumaFile/compare/v1.0.2...ead317bd7a0f549b3fc98cb219cc91225e599735) covers all 20 commits between the v1.0.2 tag and the application code used for this release.

## Browsing and previews

- The omnibar handles navigation, commands, search and filtering. Filter mode narrows the active pane as you type. Search mode offers content search and cancellation. Each pane shows its active filter or search until you clear it or return to the folder.
- Switching modes preserves typed text. Path suggestions discard stale results. Details views preserve multi-selection across refreshes, and both panes use the same view and command routing.
- Preview and Git controls announce whether their panels are open. Closing media preview releases the playing source. Background transfers leave keyboard focus where you are working.
- Settings prompts before discarding edits. Secondary windows respect display scaling and theme, and the command palette and menus reflect customized shortcuts. File and archive operations that can take time now offer cancellable progress dialogs where supported.

## Copy, move and recovery

- One queue serves each selection. Files can begin copying while the rest of a directory tree is still being discovered. An individual failure includes the source, destination and reason. The transfer continues with other files. Retry sends only the affected paths back to the service.
- Folder merges resolve conflicts per child. Replacing a file with a directory or a directory with a file reports a conflict. During a move, skipped items stay at the source, and the result distinguishes completed, skipped and failed items. A failed undo of a multi-item move can resume.
- Same-volume moves try a rename first. For streamed transfers, Checksum enables hashing. The new default per-job buffer budget is 256 MiB. Multi-stream copying is opt-in and limited to eligible local fixed volumes. Busy files have bounded retries, and the tuner backs off after unsuccessful concurrency increases.
- Copied files inherit destination-folder permissions. The Windows copy path applies timestamps and attributes through the open destination handle. An identity check can now verify a staging file while it is open for writing.
- Native FTP, FTPS and SFTP transfers update totals during the job, and Checksum mode controls the extra hashing work. The remote provider still transfers one file at a time. See [Transfer tuning](https://github.com/conniecombs/SumaFile/blob/v1.0.3/docs/TRANSFER_TUNING.md).

## Service, storage and safety

- Slow filesystem, Git, network and updater calls run outside the IPC request loop. A malformed JSON request receives a parse error and subsequent valid requests can continue. Session setup limits unauthenticated frame sizes and repeated authentication failures.
- Duplicate and cleanup scans can report progress without blocking the service. Cleanup respects exclusions on long paths. Trash operations report individual failures, and restoration checks for new files at the destination.
- ZIP round trips preserve file modification times. Encrypted ZIP entries can be listed, and unsupported extraction is rejected before writing files. Selective 7z extraction stages and validates its output. Text previews recognize UTF-16 and UTF-8 byte-order marks.
- All tags assigned to a file survive the Rust, IPC and WinUI round trip. Git status requests share a short-lived cache, read-only queries disable repository-configured hooks and filesystem monitors, and Git commands have timeouts.
- SFTP keepalives continue between requests. The updater bounds downloads by declared size and verifies the downloaded file on disk. Thumbnail cache cleanup checks its ownership marker. NuGet retains vulnerability auditing while tolerating an unreachable audit source during offline restore.
- The [UI audit](https://github.com/conniecombs/SumaFile/blob/v1.0.3/docs/UI_AUDIT_2026-11-05.md) and [backend audit](https://github.com/conniecombs/SumaFile/blob/v1.0.3/docs/RUST_BACKEND_AUDIT_2026-11-05.md) identify the individual fixes, regressions covered, and deferred items.

## Installation and upgrade from v1.0.2

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after active transfers finish. Choose the same installation format used for v1.0.2. Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.0.3_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.0.3_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.0.3_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.0.3. Existing settings, bookmarks and layouts use the same application identity. Upgraded profiles can retain earlier transfer settings. Check **Transfers → Transfer defaults** to see the active values.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Dogfood 10-Step Script

1. Upgrade a disposable v1.0.2 installation and verify About reports 1.0.3.
2. Open both panes, navigate using the omnibar, and verify each retains its location.
3. Filter one pane, run a content search, and return to the folder.
4. Select multiple files in Details and confirm the selection survives a refresh.
5. Open a media preview, collapse it, and confirm playback stops.
6. Copy a folder into an existing folder. Resolve a nested conflict and verify unrelated files reach their destinations.
7. Move a selection with a skipped file. Verify source contents and the completion receipt.
8. Retry a failed transfer item and check that completed files are left alone.
9. List an archive, extract selected files, and verify their contents. Check that tags persist after reopening the app.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the first SumaFile baseline.

## Known Limitations

Multi-file moves commit individual items, so cancelling can leave some items already moved. Same-volume replace uses a backup swap and has a crash-recovery gap between renames. Native remote transfers run one stream per session. Transfer throughput on physical NAS and cloud-mounted drives depends on the device and provider.

Release checklist: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.
