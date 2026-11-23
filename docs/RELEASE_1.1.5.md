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
