# SumaFile 1.1.1 Release Notes

Prepared November 17, 2026. This Windows x64 release changes how folders are
listed: the file service keeps each pane's folder sorted and filtered, the app
reads it in pages, and a change in a folder arrives as the rows that moved. It
also fixes hidden files, keeps the FTP/SFTP manager's selection, and turns the
folder comparison into a table. File formats, plugins and settings are
unchanged since 1.1.0.

## Folder listings

- **The service sorts and filters.** The app tells the file service the sort
  column, the filter text and the hidden-file setting; the service keeps the
  folder in that order. Clicking a column header, typing in the filter box or
  toggling hidden files no longer sorts or filters anything in the app, and
  never reads the folder from disk again.
- **A folder opens in pages.** The first 512 rows arrive already in order.
  The rest is read behind them and the complete list is published once, so
  the scroll bar, Select all and type-ahead cover the whole folder as before.
- **Changes arrive as rows, not as a new listing.** A file created, deleted,
  renamed or changed in the folder you are looking at inserts, removes or
  updates its own row. The list keeps its scroll position and selection.
  Refresh, and the refresh after a copy, move, rename or delete, ask the
  service what changed and apply only that.
- **Going back is instant.** A folder you visited opens from the session
  cache at once. A cached listing older than ten seconds is shown and then
  checked against the disk; what differs is corrected in place.
- **Names sort as before**: by the Windows culture's rules, ignoring case.

## Fixes and other changes

- **Hidden files.** With "Show hidden files" off, files and folders with the
  Windows Hidden or System attribute but no leading dot were still listed.
  They are now hidden, and shown dimmed when hidden files are on. If you
  relied on seeing them (for example `AppData` in your user folder), turn on
  Show hidden files.
- **FTP/SFTP manager.** The local and remote lists keep your selection when
  they refresh after a transfer, rename, delete or Refresh. A transfer asked
  for while another operation runs says so in the status line.
- **Folder metrics comparison.** With two or more folders selected, the
  comparison is a table: Files, Folders and Size per folder, counted through
  every subfolder, and a Total row.
- **IPC.** Four methods (`open_listing_view`, `get_listing_view_range`,
  `refresh_listing_view`, `close_listing_view`), one event
  (`listing-view-changed`) and one binary frame are new: 75 domain methods
  become 79. No existing method or type changed.

## Upgrading

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after
active transfers finish. Choose the same installation format used for v1.1.0.
Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for
published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.1.1_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.1.1_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.1.1_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `SumaFile_1.1.1_x64-winui-setup.exe.sig` | Detached signature for the NSIS installer |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.1.1. Settings, bookmarks, profiles,
connections and plugins are kept.

## Dogfood 10-Step Script

1. Install 1.1.1 over a disposable v1.1.0 installation.
2. Open a folder with a few thousand files: the first rows appear at once and the count settles on the full folder.
3. Click Name, Size, Date modified and Type, each twice: the order changes at once and folders stay on top.
4. Type in the filter box, clear it, and toggle Show hidden files: the list follows each change.
5. Scroll to the middle, select a file, then create, rename and delete other files in the folder from Windows Explorer: each row appears, moves or goes while the scroll position and selection stay.
6. Copy a file into the folder and press F5: only the new row appears.
7. Go into a subfolder and Back, twice: the parent opens at once each time.
8. With Show hidden files off, check that `desktop.ini` and other Hidden files are not listed; turn it on and check that they are dimmed.
9. Sort by Path and by the Git status column in a repository: the list still sorts.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.1.0 baseline.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

- The whole folder is still read into the app, in pages behind the first
  one. The app does not yet leave rows unread until they scroll into view.
- A folder that is not the active pane's is not watched: its rows update on
  the next refresh, as before.
- Sorting by Path, Items, the plugin status column, or Size while folder
  sizes are shown is done by the app over the complete list.
- The installer build, the installer smokes and the `-Counts 10000` UI smoke
  variants have not run for this build; run them on the release commit.
- The listing changes were verified by unit tests and CI, not on a Windows
  desktop by hand.

Checklist before tagging `v1.1.1`: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.
