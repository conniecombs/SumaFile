# SumaFile 1.2.0 Release Notes

Prepared November 24, 2026 for Windows x64.

This release fixes the open medium-severity findings of the
[November 23 code audit](CODEBASE_AUDIT_2026-11-23.md), and the ways a
transfer could lose a file or stop for good that two reviews of the transfer
engine found, and most of what those reviews found about speed and about
what a copy keeps of a file. What is still open is listed in the
[roadmap](ROADMAP.md#transfers). The release adds no features. The complete list is in the
[changelog](CHANGELOG.md).

## Transfers

- **Bandwidth limit.** Large files no longer stand still until every small file
  is done. The files being copied take turns at the limit, and tasks running
  at the same time share it: two tasks limited to 10 MB/s use 10 together,
  not 20.
- **What a copy keeps.** A copied file keeps its alternate data streams, among
  them the mark Windows puts on a downloaded file, so a copied download still
  asks before it is run. Files and folders keep their Hidden and System
  attributes, and folders their creation time. A file whose name is 255
  characters long can be copied; names longer than about 214 characters
  failed.
- **Moves between drive letters.** A move from a `subst` letter into its
  folder's drive, from a mapped drive to its share's path, or between two
  letters of one volume renames the files instead of copying them.
- **Same-drive moves.** A moved file is no longer read back after it is renamed,
  which saves several requests per file on a network share. A file that another
  program holds open is reported as busy; before, it was copied whole and the
  source could not be deleted, leaving the file in both places.
- **Write directly to the destination.** The option no longer applies to a file
  that replaces an existing one. Such a file is copied under a temporary name
  first, so a failed or cancelled Replace leaves the existing file as it was.
- **Interrupted moves.** A moved file whose final rename did not survive a
  power loss lay under its `.sumafile-partial` name with its source already
  deleted, and the next transfer into the folder deleted it. Such a file is
  now kept, and gets its final name when it is complete. A file replaced by a
  move within one drive is recorded first, so a stop between its two renames
  is undone by the next transfer into the folder.
- **Retries.** After a failed write, a failed flush or a checksum mismatch a
  file is copied again from its first byte instead of being continued. A
  retried move compares the two files in full before it deletes a source
  whose copy it finds at the destination. A retried item that cannot be
  started fails by itself, and two conflicts with the same destination can no
  longer both be answered with Replace.
- **Recovery.** What an interrupted transfer of 1.1.5 or earlier left is
  recognised and cleaned up. One damaged line in a record no longer keeps
  recovery away from the other files the record names, and a record that
  names nothing is deleted after a day.
- **Errors inside the service.** An internal error while one file is copied
  fails that file and the task carries on; it could stop the task for good.
- **Logs.** `service.log` gets the service's warnings, such as each retried
  try with its reason. `operations.jsonl` records, for each transfer, its
  duration, bytes and files and every item that failed, and is kept to 4 MiB.
- **Fewer calls per file.** A small file copied into a new folder costs 15
  calls to Windows instead of 22, one of them the look for its streams: the source is opened once, a copy no longer
  reads the source's file ID, a new folder is not searched for the file's
  name first, and the temporary file is renamed through the handle it was
  written through. The gain in speed has not been measured on Windows yet.
  Creating one folder's record no longer holds up the files of other folders.
- **Files at once after a retry.** A file retried again and again halves the
  number of files copied at once a single time, instead of in every second it
  is retried, and a number that retries forced down is not remembered for the
  route.
- **Progress.** Skipped files and files already at the destination count
  toward the percentage but no longer raise the speed. A file waiting for a
  later retry round keeps its bytes counted. After three seconds without a
  byte transferred the speed reads "Calculating speed" instead of the last
  value. "Finishing" is shown for the last file and the folders' times only.
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

- **Undo.** An Undo that fails can be taken out of the undo history with a
  button in its message, so that earlier steps can be undone. Undo no longer
  fails on an item that is already gone, and undoing a transfer into an
  existing folder handles 500 files with each request instead of one.
- **Pack into folder** and **Unpack folder here** remove a folder only when
  it is empty. Undoing a pack, or unpacking a folder with a file that could
  not be moved out, deleted the folder with everything left in it.
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
8. Find two files with the same name in different folders (a search does
   it), select both, and move them into a folder that already has a file of
   that name. Answer the first conflict with Replace and "do this for all".
   The second file is asked about again, without Replace.
9. Copy a file, delete the copy, and press Ctrl+Z. The step is undone without
   an error. Then copy a file, open the copy in a program that locks it, and
   press Ctrl+Z: the message has a **Remove from undo history** button, and
   after using it Ctrl+Z undoes the step before.

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
2. Run the nine [Manual verification](#manual-verification) cases above.
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

- Recovery after a power loss was tested by putting files and records into
  the state such a loss leaves, not by cutting power. That records written by
  1.1.5 are recognised was established from the code of both versions, not
  by upgrading over an interrupted transfer.
- A copy does not keep a file's Hidden and System attributes or its alternate
  data streams, and a file whose name is longer than about 220 characters
  cannot be copied. See the [roadmap](ROADMAP.md#transfers).
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
