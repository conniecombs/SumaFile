# SumaFile 1.0.4 UX Recommendations (spec)

Source: design review of the shipping WinUI 3 host on 2026-11-06 (toolbar, panes,
sidebar, context menus, dialogs, transfers, settings, shortcuts, preview, Git,
FTP/SFTP manager), cross-checked against `docs/UI_AUDIT_2026-11-05.md`. Items
marked (audit X) were logged there and deferred.

## Change

1. **Tab as pane switch.** Bare Tab runs `pane.switch` only when a file list has
   focus; elsewhere it traverses focus. Bare Space and Backspace get the same guard.
   Alt+1/Alt+2 and Ctrl+Shift+Left/Right stay the documented chords.
2. **Omnibar mode.** Return to Path mode when a search or filter ends (clear,
   Escape, or the active pane navigates elsewhere). In Path mode an un-rooted
   word is first resolved against the current folder; if that folder does not
   exist it becomes a Filter and the status bar says
   "No folder named 'X' here. Filtering instead." Bare `C:` normalizes to `C:\`.
   The prefix hint "Path, or >command ?search ~filter" is permanent (placeholder
   and tooltip). Mode toggles are keyboard focusable; focus returns to the text box
   after a click. Automation name "Address and search".
3. **Context menus** (audit F11, F23). Grey unavailable items; hide only
   inapplicable ones. "Delete" is a direct item (Delete) with "Delete permanently"
   (Shift+Delete) as a sibling; no "Delete:" submenu. "Copy path" and
   "Bookmark folder" move to the top level; "Send to" keeps only the cross-pane
   items. Sentence case everywhere ("Delete permanently", "Extract to folder").
4. **Cross-pane toolbar buttons** are disabled in single-pane mode with tooltip
   "Open the second pane (F6) to copy there"; the info bar is removed.
5. **Transfers window** never activates itself when auto-created; it is shown by
   the Status Center entry, the Transfers command, or after a job has run about
   three seconds. The tuning line, Options button and active-file list sit inside
   a collapsed "Details" expander. Status Center rows show a progress bar.
6. **Transfer options** (audit T25): Mode, "Bandwidth limit (MB/s, 0 = unlimited)"
   and "Verify copies with a checksum (slower)" visible; everything else under an
   "Advanced" expander with plain labels and no flag names. Seed from real defaults;
   write only changed fields. Settings gets a "Transfers" row that opens the dialog.
   Failures are described by `TransferFailureText.Describe` in one sentence with
   the raw text under Details. "backend service"/"IPC" become "SumaFile's file service".
7. **Conflict dialog**: RadioButtons "Replace the existing file" / "Skip this file"
   / "Keep both (saves as X)", default Skip; Primary "Continue" (default button),
   Close "Stop transfer"; note "Replaced files cannot be undone."; checkbox
   "Do this for all remaining conflicts"; "Newer"/"Larger" badges on the cards.
8. **Git** is lazy: enrichment, the status-bar button and the context submenu
   appear only inside a repository. Settings toggle moves to a "Developer" group
   with the description "Shows Git status for files inside repositories and
   enables the Git workbench." Discard lists paths (first five + "and N more")
   and adds "N untracked files will be deleted from disk, not moved to the
   Recycle Bin." when relevant; the Discard button uses the critical style.
9. **Remote manager**: plain-FTP consent is under Protocol, shown only for FTP,
   text "Connect without encryption. Passwords and files are sent in plain text.";
   Save is disabled with inline text "Confirm the unencrypted connection to save
   this profile"; the Rust fallback message says the same. "Insecure" badge in
   list and sidebar. Untrusted SFTP key -> dialog "Trust this server?" with host,
   port, fingerprint, "Trust and connect"/"Cancel"; mismatch -> warning whose only
   non-cancel action is "Replace trusted key", default Cancel. "Credential target"
   is hidden (tooltip only). Sidebar section becomes "Connections" with an
   open-in-window glyph per row and empty state "No connections. Open the manager
   to add one."
10. **One noun per concept**: "Labels" (sidebar, "Set label...", "Clear label
    filter", picker "Choose a label" / "None (remove label)", Settings "Show
    labels"); "sidebar" in all copy; "Profiles" for workspace profiles and
    "Connections" for FTP/SFTP; pane captions "Left · Active" / "Right";
    "Show rendered view" for the preview switch with a Source | Rendered segmented
    control in the preview header; "Large files and duplicates" for Disk cleanup.
11. **Settings and shortcut defaults**: settings search indexes control headers
    and jumps to the owning section; profiles no longer snapshot
    `EnableGitIntegration` or `KeepFoldersOnTop`; Light theme added
    (`NormalizeTheme` returns "light"); defaults Ctrl+N = new tab, new text
    document unbound, Backspace = Back, terminal = Ctrl+` (F4 free), Settings =
    Ctrl+, , help on F1 and Ctrl+/ (main keyboard and numpad); "Ask before moving
    to Recycle Bin"; Start location "Home folder" / "Restore last session (tabs
    and panes)" / "Custom folder…"; sentence case for all Settings labels.
12. **Preview**: `ShouldCollapse` ignores empty selection (cramped-width only);
    Settings switch "Collapse the preview when panes are cramped"; status-bar
    button is a strict toggle of `PreviewVisible`; `preview.toggle` defaults to
    Alt+P. Quick Look becomes a lightbox: wide, name line only, Left/Right move
    to the adjacent file, Space/Esc close, same text budget as the pane; the
    folder-metrics branch is removed (folders open Properties).
13. **Icon sizes**: 16 Small, 32 Medium, 48 Large, 96 Extra large, 256 Huge;
    persisted 128/192 migrate to the nearest option.

## Add

- Tab context menu: Close, Close other tabs, Close tabs to the right, Duplicate
  tab, Open in other pane, Copy path. Close glyph hidden on a pane's last tab in
  dual-pane mode.
- Smart folders: palette command `save-smart-folder` "Save search as smart
  folder…", a button in the search banner, empty state "Run a search, then save
  it here to run it again later.", delete confirmation defaulting to Cancel.
- Undo surfacing: "Undid: Move 3 items"; history button "Undo: Move 3 items";
  Undo button on the completion receipt and Status Center row; "Nothing to undo
  (replaced files can't be restored)" after a Replace transfer.
- Escape (cancel) and Ctrl+Enter (primary) accelerators for secondary windows
  via `SecondaryWindowChrome.Apply`.
- Large files and duplicates: footer "Showing first 50 of 312 large files",
  per-row Reveal in folder and Move to Recycle Bin, link to the Duplicate Finder.
- Inline rename in Details view (F2): in-place TextBox, extension excluded from
  the initial selection, `PathRules` validation, Esc cancels, Enter commits.

## Remove

- The second "Rename N items" confirmation after Advanced Rename.
- The dual-pane row and the Profiles block from the View options flyout; the
  three "Save view defaults" buttons become one dropdown "Remember this view for:
  Everywhere / This folder / This folder and subfolders"; no "Current" in headers.
- Selection-level items from the pane More menu; it keeps overflowed toolbar
  items, Toolbar, a Tools submenu (FTP/SFTP manager, Operation history, Large
  files and duplicates, Terminal), Profiles and the pane-close items.
- Stale palette entries: "Focus find in folder" -> "Search this folder",
  "Focus filter list" -> "Filter this folder".
- Duplicate Checker: partial hash and network mode under "Advanced"; "Keep first"
  -> "Keep the copy in the current folder" (fallback shortest path, sorted).
- Hard-coded `#AA0000` trash button in favour of the critical button style.

## Version

Bump to 1.0.4 through `npm run release:prepare -- 1.0.4 --write`, add
`docs/RELEASE_1.0.4.md`, a CHANGELOG entry, and README updates (shortcuts table,
"Profiles", "More (⋯) -> Tools -> FTP/SFTP Manager", labels).
