# SumaFile 1.0.1 Release Checklist

SumaFile 1.0.1 is a WinUI 3 + Rust IPC maintenance release focused on the
settings workspace, command-surface customization, and release-path cleanup.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.0.1_x64-winui-setup.exe` | Recommended per-user NSIS installer |
| `SumaFile_1.0.1_x64-winui.msi` | MSI deployment package |
| `SumaFile_1.0.1_x64-winui-portable.zip` | Portable payload with `SumaFile.exe` and `simplefile-service.exe` |
| `latest-winui.json` | Signed updater metadata for published releases |

## Scope

- Settings opens as a standalone resizable window with NavigationView
  categories, explicit Save/Cancel actions, and normal window close behavior.
- Settings -> Toolbar can customize the primary toolbar order, separators,
  button label mode, and command availability.
- Toolbar overflow and context-menu labels resolve through the shared command
  catalog.
- Version identity is aligned at 1.0.1 across MSBuild, Cargo, app manifest,
  README, About/Settings, and installer artifact names.

## Required Local Gates

Run these before tagging or dispatching the release workflow:

```powershell
npm run check
npm run check:winui
npm run check:rust
npm run smoke:winui
npm run release:build
```

If `release:build` fails while replacing an existing MSI, confirm no local
`msiexec` process or installer UI is holding the previous MSI before rerunning.

## Dogfood 10-Step Script

1. Launch the WinUI app from a clean payload or Debug build.
2. Open Settings and confirm it appears as its own resizable, closable window.
3. Resize the Settings window smaller and larger; confirm Shortcuts and Toolbar
   content stays usable without clipped action buttons.
4. Search settings categories and confirm selection moves to the first visible
   category when the current one is filtered away.
5. Change Settings -> Toolbar to icon-only mode, remove one command, add a
   separator, save, and confirm the main toolbar updates without restart.
6. Reopen Settings -> Toolbar, reset the toolbar, save, and confirm default
   command order returns.
7. Change one shortcut, save, confirm the shortcut works, then reset it.
8. Open Settings -> Updates and confirm the displayed current version is 1.0.1.
9. Run `smoke:winui-upgrade-from-ref` when a previous release ref is available.
10. Verify the generated `latest-winui.json` references the 1.0.1 setup
    executable when signing metadata is provided.

## Upgrade Smoke

The automated upgrade path is covered by `smoke:winui-upgrade-from-ref` and
`smoke:winui-upgrade`. For local release rehearsals, install a previous release,
build 1.0.1, then verify the updater downloads and launches the local 1.0.1
installer.

For unsigned local builds, GitHub Releases fallback is acceptable. Require a
signed test release before claiming in-app updater installation is proven.

## SimpleFile Data Import

Existing SimpleFile data import remains automatic through the compatibility
settings store identifiers. No manual import is needed for normal
SimpleFile-to-SumaFile use.

## Known Limitations

- SumaFile 1.0.1 is Windows-only and targets Windows 10 2004+ / Windows 11 x64.
- First-time installs are manual; in-app update installation requires a newer
  published release with signed `latest-winui.json` metadata.
- Account-backed storage integrations are not part of 1.0.1. Supported storage
  surfaces remain local folders, local drives, removable media, mapped network
  shares, and archives.
- RAR and 7-Zip functionality depends on optional external tooling.
