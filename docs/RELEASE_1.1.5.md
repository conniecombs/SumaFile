# SumaFile 1.1.5 Release Notes

Prepared November 22, 2026 for Windows x64.

## Folder copies and moves

Pasting `Photos` beside an existing `Photos` folder now fills that folder automatically.
The same rule applies to drag and drop, transfers between panes, and nested folders.
Destination-only files stay in place. If both trees contain `2025/family.jpg`, SumaFile
asks what to do with that file. Replace, Skip and Keep both apply to the individual
conflict unless you select **Do this for all remaining conflicts**.

Keep both on a file creates a numbered sibling even when its size and modification
time match the incoming file. A file and a folder competing for a path can be skipped
or kept under separate names.

## Undo and Redo

Undo removes additions from a merged copy and returns moved files to their source
paths. It preserves the existing destination folder, files that were already there,
and any new folder that still contains other items. Redo uses the recorded paths.
Replacing a file does not retain its previous contents for Undo.

## Manual verification

1. Copy a populated folder onto an empty same-name folder. Check that the copy
   starts without a prompt. Undo and confirm the original empty folder remains.
2. Repeat with an unrelated destination file and a nested file of the same name on
   both sides. Exercise Skip, Replace and Keep both in separate copies. Check the
   contents of both files, including a Keep both case with matching size and date.
3. Add an unrelated file inside a newly copied subfolder, then Undo and Redo the
   merge. Confirm the unrelated file survives both operations.
4. Move a tree containing empty subfolders into an existing tree. Undo and Redo,
   checking the source paths and the destination-only files.

See the [transfer reference](TRANSFER_TUNING.md#when-the-destination-exists) for details.

## Upgrading

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after
active transfers finish. Choose the same installation format used for v1.1.4.
Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for
published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.1.5_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.1.5_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.1.5_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `SumaFile_1.1.5_x64-winui-setup.exe.sig` | Detached signature for the NSIS installer |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.1.5. Settings, bookmarks, profiles,
connections and plugins are kept. The app and its service always ship
together; a transfer result now carries the list of changes a merge made.

## Dogfood 10-Step Script

1. Install 1.1.5 over a disposable v1.1.4 installation.
2. Run the four [Manual verification](#manual-verification) cases above with copy and paste.
3. Repeat the first case by dragging the folder onto the other pane, and again with **Copy to other pane** (Ctrl+Alt+C): each merges without a prompt.
4. Copy a folder onto a file of the same name: Skip leaves the file as it was, and Keep both creates a numbered folder beside it.
5. Copy a large folder into a same-name folder, cancel part-way, then Undo: the files added before the cancel are removed and the existing folder and its files stay.
6. Undo a merged copy and open the Recycle Bin: the added files are there. Redo puts them back.
7. Undo a merged move: the files return to their source paths, and the destination-only files stay.
8. Merge a folder of 100,000 or more files with long names into a same-name folder, then Undo and Redo it: both finish. A change list whose paths pass 4 MiB is sent to the app in parts, which this exercises.
9. Answer one conflict with Replace, then Undo the copy: the replaced file keeps the new content, and only the additions are removed.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.1.4 baseline.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

- Undo cannot bring back a file that a merge replaced: its previous content is
  not kept.
- If an Undo or Redo step fails, the steps before it stay done; running the
  command again resumes at the failed step.
- No UI smoke performs a folder merge or its Undo. The merge and Undo
  behaviour was verified by the Rust and C# tests in CI.

Checklist before tagging `v1.1.5`: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.

