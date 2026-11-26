# SumaFile 1.2.0 Release Notes

Prepared November 24, 2026 for Windows x64.

This release fixes the open medium-severity findings of the
[November 23 code audit](CODEBASE_AUDIT_2026-11-23.md) and the transfer engine
problems found in a review of that engine. It adds no features. The complete
list is in the [changelog](CHANGELOG.md).

## Transfers

- **Bandwidth limit.** Large files no longer stand still until every small file
  is done. The files being copied take turns at the limit.
- **Same-drive moves.** A moved file is no longer read back after it is renamed,
  which saves several requests per file on a network share. A file that another
  program holds open is reported as busy; before, it was copied whole and the
  source could not be deleted, leaving the file in both places.
- **Write directly to the destination.** The option no longer applies to a file
  that replaces an existing one. Such a file is copied under a temporary name
  first, so a failed or cancelled Replace leaves the existing file as it was.
- **Moves that leave files behind.** Deciding which source folders stay after a
  move with many skipped or failed files took time in proportion to folders
  times files. A tree of 8,000 folders with 80,000 skipped files took 43 seconds
  at this step in a test and now takes 0.1.
- **Memory.** The copy buffer cache no longer fills with buffers of sizes that
  are never asked for again, and a large file reserves a buffer per stream only
  when it is copied in several streams.
- **Threads.** A transfer starts a copy thread when a file needs one instead of
  thirty-two before the first file. Folder times are restored by several
  threads when a transfer created many folders.
- **Copy to other pane, Move to other pane.** The command acts on the pane that
  was the other one when it started, and is refused when both panes show the
  same folder.

## File list and keyboard

- Typing a name selects the match in the list you typed into, including a list
  of search results, and replaces a selection of several items.
- Alt+P opens the preview pane on a narrow two-pane window.
- Ctrl+1 to Ctrl+9 no longer switch tabs behind an open dialog.
- After the file service restarts, large folders are read in pages again.

## Previews and properties

- Quick Look shows a PDF with the preview pane's restrictions, and releases the
  file when it closes or moves to the next item.
- `.gitignore`, `.env`, `.npmrc` and other files named only by an extension are
  previewed as text.
- PDF titles stored as UTF-16 are shown correctly.
- Properties of a compressed tar archive decompress at most 64 MiB to count
  entries, and say so when the count covers only the start of the archive.

## Other fixes

- **Open with** applies its blocked-program list to the program a shortcut
  points to, and no longer deletes a file opened from an archive under a
  program that handed it to a window already open.
- Saving two smart folders at nearly the same time no longer loses one.
- Remote: saved connections no longer fail with "database is locked" when a new
  connections database is opened twice at the same moment.

## Manual verification

1. Set a bandwidth limit and copy a folder that holds one large file and a few
   thousand small ones. Check in the Transfers window that the large file
   advances while small files are still being copied.
2. Move a folder within one drive while one of its files is open in another
   program. The file is reported as busy, stays in the source only, and the
   rest moves.
3. In two panes on different folders, type the first letters of a file name
   into the pane that is not active. The match is selected in that pane.
   Repeat in a pane showing search results, and with several items selected.
4. Open Quick Look on a PDF, close it, and rename the file. Repeat with a
   video. Both renames succeed at once.
5. Narrow the window with two panes open until the preview pane folds to its
   strip, then press Alt+P twice. The strip goes away, then the pane opens.
6. Open Settings and press Ctrl+2. The tab behind the dialog does not change.
7. Select `.gitignore` in a repository. The preview pane shows its text.

## Upgrading

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after
active transfers finish. Choose the same installation format used for v1.1.5.
Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for
published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.2.0_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.2.0_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.2.0_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `SumaFile_1.2.0_x64-winui-setup.exe.sig` | Detached signature for the NSIS installer |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.2.0. Settings, bookmarks, profiles,
connections and plugins are kept. The app and its service always ship
together.

## Dogfood 10-Step Script

1. Install 1.2.0 over a disposable v1.1.5 installation.
2. Run the seven [Manual verification](#manual-verification) cases above.
3. Copy a folder of mixed large and small files to a removable drive with
   "Streams per large file" set to 4. The transfer finishes and the files match.
4. With "Write directly to the destination" on, copy a large file onto an
   existing one, answer Replace, and cancel part-way. The existing file is
   unchanged.
5. Move a tree onto a same-name tree, skip every conflict, and check that
   "Finishing" ends promptly and the skipped files' folders stay in the source.
6. Use **Copy to other pane** (Ctrl+Alt+C) with both panes on the same folder.
   The status bar says the other pane shows the same folder, and nothing is
   copied.
7. Stop `simplefile-service.exe` in Task Manager while a folder of more than
   10,000 files is shown, let the app reconnect, and scroll the folder. Rows
   load as you scroll.
8. Open a document inside a zip archive with **Open with** and a program that
   is already running. The document opens and stays readable.
9. Open Properties on a `.tar.gz` that holds more than 64 MiB of data. The
   panel says the count covers the start of the archive only.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release
    upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.1.5
    baseline.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

- Type-ahead, the Alt+P and Ctrl+1 to Ctrl+9 changes, and Quick Look's release
  of a file have no UI smoke test. They are covered by tests of the shared
  logic and of the source, and by the manual cases above.
- A file opened from inside an archive with **Open with** and handed to a
  running program keeps its temporary copy for a day.
- Properties of a large compressed tar archive count only the entries in the
  first 64 MiB of decompressed data.
- The items the audit still lists as open (the Remote window's file-manager
  parity, marquee selection in the Details view, dropping onto tabs, saved
  window placement, and the architectural changes to the Rust backend) are
  unchanged in this release.

Checklist before tagging `v1.2.0`: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.
