# SumaFile 1.1.4 Release Notes

Prepared November 20, 2026. This Windows x64 release is a hotfix for copies
that failed after every file had been written, and it carries the Details view
column work that was already merged: a column picker, three new columns that
come with every listing, and columns read per row on demand. Settings, plugins
and file formats are unchanged since 1.1.3. The app and its service always
ship together; between them the binary listing frame is now version 2 and
there are three new IPC methods.

## Transfers (the hotfix)

- **Copies into a folder reached through a middle breadcrumb failed after
  writing every file.** The breadcrumb bar joined a drive root and its first
  folder as `V:\\sync` (two separators), and so did the path-join helper for a
  drive root. Clicking that crumb put the pane, every folder entered from it
  and every copy into them on the doubled path. In the failure this was
  diagnosed from, files were written under such a path, but the rename that
  moves a finished copy into place was refused ("The parameter is incorrect"),
  so each file was reported failed after its last byte and its
  `.sumafile-partial` file stayed in the destination. Both joins now give
  `V:\sync`, and the transfer service collapses repeated separators in every
  source and destination before it plans anything, so no other source of such
  a path can reach the rename.
- **A file that fails for good keeps its bytes in the progress readout.**
  Before, the bytes of a file that failed after its last byte left the
  readout, so the bar ran backwards as files failed. Now they stay, and the
  final error report counts what was written. A file set aside for a later
  retry round is not resumed: its partial file is removed and the retry copies
  the file again from the start. While such a file waits for its round its
  bytes are out of the readout, and they return when the retry starts; see
  Known Limitations.
- **The last step of a copy is more patient and says more.** The rename that
  publishes a fully copied file waits out a file held open by a virus scanner
  or indexer with the whole retry budget instead of three attempts. When
  Replace fails while moving the existing file aside, the error also names
  the rename refusal that came first. A retry no longer fails on the
  ownership record an earlier attempt's failed cleanup left behind: it
  recovers that attempt's partial and takes the record over.

## Details view columns

- **Column picker.** Right-click a column header, or open "Columns" from the
  list background's menu in the Details view: a toggle per column, the presets
  as starting points, the name column always on. Each pane keeps its own
  choice, saved with the layout.
- **Created, Accessed and Attributes** come with every listing and sort like
  the other columns. Attributes shows Explorer's letters (R H S A C E O T, and
  L for a reparse point).
- **Columns read on demand.** Dimensions (images), Duration (audio and video),
  Date taken and Camera (photos), Pages (PDF), and a folder's Size and Items
  are read per row. A cell shows "…" while the row can have a value that has
  not been read; click it to read that one value. The service reads image
  headers, MP3, FLAC, WAV, M4A, OGG, Opus and AAC audio, MP4, MOV, MKV and
  WebM containers, EXIF and PDF page trees, keeps each value in metadata.db by
  the file's size and modified time, and never reads more than two folder
  trees and four files at a time. AVI, WMV, MPEG and ASF durations come from
  the Windows property system, two at a time, and are kept too.
- **When they are read.** Each on-demand column reads as rows appear (the
  default for all but folder size) or only when asked: "Read as rows appear"
  in the column's header menu. "Calculate … for this folder" reads a column
  for the whole folder with its progress in the status line and a "Stop
  calculating" item. Sorting by a column that reads as rows appear reads the
  whole folder first, 5,000 rows at most; above that the status line says how
  many were left unread. The "folder sizes" setting makes the Size column read
  folder sizes as rows appear, as before.

## Upgrading

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after
active transfers finish. Choose the same installation format used for v1.1.3.
Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for
published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.1.4_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.1.4_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.1.4_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `SumaFile_1.1.4_x64-winui-setup.exe.sig` | Detached signature for the NSIS installer |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.1.4. Settings, bookmarks, profiles,
connections and plugins are kept. A saved column layout from 1.1.3 loads as it
was; the new columns are off until you turn them on.

If a copy failed in 1.1.3 or earlier with "The parameter is incorrect", its
destination folder may still hold `.name.<id>.sumafile-partial` files and
small `.sumafile-partial.state` files next to them. SumaFile lists them only
when hidden files are shown. Copy any file into that folder with 1.1.4 first:
a transfer into a folder starts by cleaning up what earlier transfers left
there. Then delete by hand what is still there, as described under
[Deleting leftovers by hand](https://github.com/conniecombs/SumaFile/blob/main/docs/TRANSFER_TUNING.md#deleting-leftovers-by-hand).
See also Known Limitations.

## Dogfood 10-Step Script

1. Install 1.1.4 over a disposable v1.1.3 installation.
2. Open a folder two levels below a drive root, click the first folder's breadcrumb, enter the folder again, and copy a large file into it with Replace: it completes, and the path box never shows two backslashes after the drive letter.
3. Copy several large files at once and watch the progress readout: bytes done does not fall as files finish.
4. Copy a file onto one that another program holds open: the copy waits and retries instead of failing in three seconds, and the error, if it does fail, names the cause.
5. Right-click a column header: toggle Created, Accessed and Attributes on, sort by each, and restart the app: the choice is kept per pane.
6. In a folder of photos turn on Dimensions, Date taken and Camera: the cells on screen fill in, scrolling fills the next rows, and a second visit is immediate.
7. In a folder of videos turn on Duration: MP4 and MKV fill in from the service, an AVI or WMV a moment later from Windows.
8. Click a folder's "…" in Size: that one folder is measured. Use "Calculate folder sizes for this folder", then "Stop calculating": the status line follows, and what was read stays.
9. Sort a folder of PDFs by Pages: the status line reads the whole folder, then the order is right.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.1.3 baseline.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

- Partial files left behind by the failed copies of earlier versions may
  stay. Those copies ran the usual clean-up after each failed file and the
  partial files stayed all the same; why was not established. The same
  clean-up runs again at the start of the next transfer into the folder.
  Delete by hand what it leaves (see Upgrading).
- While a file waits for a later retry round, its bytes are out of the task's
  bytes done, so the readout falls by that amount until the retry starts. Only
  a file that failed for good keeps its bytes.
- Every folder in a default Details view shows "…" in Size until it is clicked
  or the "folder sizes" setting is on, because folder size is read on request
  by default.
- A sort by an on-demand column fills at most 5,000 rows; in a larger folder
  the rest sort as unknown until read.
- Nothing is read automatically for on-demand columns on a network path; a
  click on a cell still reads that one value.
- Whether `MoveFileExW` refuses a doubled separator on every Windows version
  and filesystem was not established; the failure was diagnosed from one
  machine's operations journal and the fix removes the doubled path at both
  ends.
- The Release workflow built the installers on the release commit and its
  smokes passed: packaged file operations, NSIS install, launch and uninstall,
  NSIS upgrade from the latest published release, and MSI install, upgrade
  and uninstall. The in-app update was not exercised.
- The column picker, the "…" cells, the header menu, the status line and the
  Windows duration fallback were verified by unit tests, a type-check build
  and CI, not on a Windows desktop by hand.

Checklist before tagging `v1.1.4`: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.
