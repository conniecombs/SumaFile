# SumaFile 1.0.4 Release Notes

Prepared November 6, 2026. This Windows x64 release is a user-interface
release: it changes how menus, the omnibar, dialogs, transfers, settings and
the FTP/SFTP manager present themselves, without changing file formats, the
IPC protocol or the transfer engine. It implements the
[1.0.4 UX recommendations](superpowers/specs/2026-11-06-ux-recommendations.md)
that followed the [2026-11-05 UI audit](UI_AUDIT_2026-11-05.md).

## Menus, toolbar and keyboard

- Context menus grey out commands that do not apply to the selection instead
  of hiding them. Delete and Delete permanently are direct items. Copy path
  and Bookmark folder sit at the top level. The More (⋯) menu keeps toolbar
  overflow, Toolbar, a Tools submenu, Profiles and the pane-close commands.
- Copy to other pane and Move to other pane are disabled, with an explanatory
  tooltip, until the second pane is open.
- Tab switches panes only while a file list has focus; elsewhere it moves
  keyboard focus as in any Windows app. Space and Backspace follow the same rule.
- Default shortcuts: Ctrl+N opens a new tab, Backspace goes back, Ctrl+` opens
  a terminal, Ctrl+, opens Settings, Ctrl+/ shows the shortcut list, and Alt+P
  shows or hides the preview pane. Existing custom shortcuts are kept.
- Tabs have a context menu (Close, Close other tabs, Close tabs to the right,
  Duplicate tab, Open in other pane, Copy path). The last tab of a pane in
  dual-pane mode no longer shows a close glyph.
- Icon sizes are Small, Medium, Large, Extra large and Huge. Saved 128 px and
  192 px sizes map to the nearest option.
- The View options flyout no longer duplicates the second-pane and Profiles
  controls; view defaults are saved through one "Remember this view for" choice.

## Omnibar

- When a search or filter ends, or the active pane navigates elsewhere, the
  omnibar returns to Path mode and shows the folder path again.
- In Path mode a bare name is resolved against the current folder. If no such
  folder exists, the pane is filtered instead and the status bar says so.
- The prefix hint (`>` command, `?` search, `~` filter) stays visible, and the
  mode buttons can be reached with the keyboard.

## Dialogs and transfers

- The conflict dialog offers Replace, Skip and Keep both as one choice list,
  defaults to Skip, and finishes with Continue. It states that replaced files
  cannot be undone and marks the newer or larger file.
- The Transfers window no longer opens itself for a quick copy; it appears
  after a few seconds or from the Status Center. Tuning details sit under a
  Details expander. Status Center rows show progress and an Undo action.
- Transfer options show mode, bandwidth limit and checksum verification;
  everything else is under Advanced with plain-language labels. Settings has a
  Transfer options row.
- Transfer failures read as one sentence with the raw error under Details.
- Moving to the Recycle Bin defaults to the confirming button and names the item.
- Advanced Rename no longer asks a second time after its preview; renames can
  still be undone with Ctrl+Z. Template tokens are explained in a flyout.
- Disk cleanup is now Large files and duplicates, says how many results are
  shown, and offers Reveal and Move to Recycle Bin per row.
- The Duplicate Checker hides hash and network settings under Advanced and
  replaces Keep first with Keep the copy in the current folder.
- Advanced Rename, Duplicate Checker and Transfers windows close with Escape;
  Advanced Rename and Duplicate Checker run their primary action with Ctrl+Enter.
- Quick Look is a lightbox: Left and Right move between files, Space closes.
- F2 renames in place in the Details view.

## Preview, Git and connections

- The preview pane collapses only when panes are cramped, not when the
  selection clears. Source and Rendered are a switch in the preview header.
- Git controls and Git status appear only inside a repository. The Git toggle
  lives in a Developer group in Settings. Discard lists the affected paths and
  warns when untracked files will be deleted from disk.
- FTP/SFTP profiles are listed as Connections. Plain FTP needs an explicit
  unencrypted-connection confirmation shown under the protocol choice, and such
  connections carry an Insecure badge in the manager and the sidebar. A new
  SFTP server key opens a Trust this server prompt; a changed key opens a
  warning. The credential target is no longer an editable field.
- Settings search finds individual settings, a Light theme is available,
  labels use sentence case, and Start location choices are Home folder,
  Restore last session and Custom folder. Applying a workspace profile no
  longer changes the Git or folders-on-top settings.
- Tags are called Labels everywhere, the side menu is called the sidebar, and
  workspace profiles are called Profiles.

## Installation and upgrade from v1.0.3

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after
active transfers finish. Choose the same installation format used for v1.0.3.
Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for
published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.0.4_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.0.4_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.0.4_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `SumaFile_1.0.4_x64-winui-setup.exe.sig` | Detached signature for the NSIS installer |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.0.4. Existing settings, bookmarks,
profiles and connections are kept. Custom keyboard shortcuts are kept; only
unchanged defaults move to the new chords.

## Dogfood 10-Step Script

1. Upgrade a disposable v1.0.3 installation and verify About reports 1.0.4.
2. Right-click a file and a folder with nothing on the clipboard; Paste is greyed, not missing.
3. Open the second pane, focus the sidebar, press Tab; focus moves without switching panes.
4. Search a folder, press Escape, then type a bare folder name in the omnibar.
5. Copy a file over an existing one; choose Keep both with Enter.
6. Paste a small file and confirm the Transfers window does not take focus.
7. Open Settings, search for "hidden", switch the theme to Light.
8. Browse a Git repository and a plain folder; the Git button appears only in the repository.
9. Connect an SFTP profile to a new server and accept the trust prompt.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.0.3 baseline.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

The WinUI host was changed without a Windows build in the authoring
environment; the Windows CI run on the release commit is the compile and
smoke gate. Inline rename covers the Details view only. Quick Look navigation
follows the active pane's current sort order.

Release checklist: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.
