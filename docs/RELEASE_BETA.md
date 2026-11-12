# SumaFile Beta Notes

Prepared November 10, 2026. These notes describe the **Beta** branch of
SumaFile for Windows x64. It introduces the plugin system and moves three
built-in features into plugins that ship with SumaFile. A default install
keeps the features of 1.0.4; how they are built, stored and removed has
changed, so read "Upgrading" before you install a Beta build over 1.0.4.

The Beta has no version number of its own: its builds still carry 1.0.4, and
the real version is set when the branch is merged into `main`. Until then, do
not publish a Beta build as a release: the updater and the MSI would treat it
as the same version as the published 1.0.4.

## What is new

- **Plugins.** A plugin is a folder with a `plugin.json` manifest, an optional
  C# UI half and an optional backend process. Plugins add commands, context
  menus, toolbar buttons, sidebar sections, side panels, row decorations,
  settings pages and archive formats. See [Plugins](../README.md#plugins).
- **Three bundled plugins** replace the built-in code and need no approval:
  7-Zip archives (`com.sumafile.sevenzip`), FTP/SFTP connections
  (`com.sumafile.remote`) and Git (`com.sumafile.git`). Each can be disabled,
  none can be removed.
- **Settings -> Plugins.** An overview of every plugin with Install from file,
  Rescan and Open plugins folder, and one page per plugin under Plugins for its
  Enabled switch, settings and Remove.
- **Consent.** SumaFile asks before it enables a plugin it did not ship, and
  asks again when the plugin's `plugin.json`, signature state or folder
  changes, and after Install from file or Remove. A signed plugin shows
  "Signed · key <fingerprint>": the signature proves its files are unchanged
  since signing, not who published it. There is no sandbox.
- **For plugin authors:** the guide [docs/PLUGINS.md](PLUGINS.md), the starter
  [templates/sumafile-plugin](../templates/sumafile-plugin/README.md) and the
  signing tool `scripts/sign-plugin.mjs`. The SDK package and the Rust helper
  crate are not published yet.

## What changed for existing features

- **Git.** The toggle moved from the status bar to the toolbar and shows inside
  a repository. Git menu items are hidden, not greyed, where they do not apply.
  Fetch, pull and push are Transfers rows with Cancel. "Git integration" is now
  the Git plugin's Enabled setting under Settings -> Plugins -> Git; a 1.0.4
  setting of off is carried over. Read-only Git views run no hook, filter or
  external diff a repository configures; your own stage, commit, fetch, pull
  and push run them as the git command line does.
- **FTP/SFTP.** Profiles and the manager come from the plugin. Profiles saved
  by 1.0.4 are imported, read-only, on first use; saved passwords stay in
  Windows Credential Manager.
- **7-Zip.** `.7z` support and the bundled 7-Zip come from the plugin. The
  executable override is under Settings -> Plugins -> 7-Zip archives.
- **IPC.** The service's domain methods went from 97 to 75: the Git and remote
  methods moved to the plugins' own protocols.
- **Status bar.** The item count and the selection's count and size sit beside
  the folder address at the bottom left.
- **Properties.** Details are grouped under General, Attributes and the file's
  metadata. For a folder, sizes and counts are no longer measured when the
  dialog opens: **Calculate totals** counts every file and folder inside it,
  including subfolders, and adds up their size.

## Upgrading

- Plugins, their data (`plugins-data`, which holds the FTP/SFTP profiles), the
  approvals file and `plugins.log` all live in the installation folder.
  Upgrades keep them. **Uninstalling removes them**, including the FTP/SFTP
  profiles, which 1.0.4 kept. Saved passwords stay in Credential Manager under
  `SumaFile.Remote.*`.
- Git for Windows with `git.exe` on `PATH` is needed for Git status and the
  Git workbench, as before.

## Dogfood 10-Step Script

1. Install a Beta build over a disposable v1.0.4 installation.
2. Open Settings -> Plugins: the overview lists 7-Zip archives, FTP/SFTP connections and Git as enabled, and each has its own page under Plugins.
3. Browse a Git repository and a plain folder; badges, the Git menu group and the Git toolbar button appear only in the repository.
4. Open the Git workbench, stage and unstage a file, view a diff, then fetch and cancel it from the Transfers window.
5. Turn the Git plugin's Enabled setting off: badges, the menu group and the toolbar button go at once; turn it on again.
6. Open the FTP/SFTP manager; profiles saved by 1.0.4 are listed and connect with their saved passwords.
7. Create, browse and extract a `.7z` archive.
8. Install the template plugin from a `.sfplugin` file: SumaFile asks before enabling it; Remove it again.
9. Disable and enable each bundled plugin; its commands, menus and sidebar section leave and come back.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.0.4 baseline; uninstall and check the installation folder is gone.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

- The installer build and the installer smokes
  (`npm run release:build`, the NSIS, MSI and upgrade smokes) and the
  `-Counts 10000` UI smoke variants have not run for this build; run them on
  the release commit.
- Running Win2D from a plugin is not tested.
- Settings search finds the controls of a plugin's custom settings page only
  after that plugin's page has been opened once in the window.
- The SDK package and the Rust helper crate for plugin authors are not
  published yet.

Checklist before the merge into `main`: set the version, write that version's release notes, and run the repository, Rust, .NET, packaging and Windows UI checks on the release commit.
