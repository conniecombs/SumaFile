# SumaFile 1.1.3 Release Notes

Prepared November 18, 2026. This Windows x64 release makes photos upright and
their thumbnails faster, counts a folder's contents as soon as Properties
opens, and hardens the release and dependency-update automation. File formats,
plugins, settings and the IPC contract are unchanged since 1.1.2.

## Thumbnails

- **Photos are upright.** A portrait photo from a phone or camera stores the
  sensor's landscape picture plus an orientation tag. Thumbnails ignored the
  tag and showed such photos on their side in the file grid, the preview pane
  and Properties. All three are upright now. Thumbnails cached by earlier
  versions are made afresh once (the cache key carries a format version), so
  the first visit to each folder of photos after upgrading is a cold one.
- **The camera's preview is the thumbnail.** Most camera JPEGs embed a small
  preview. When it is at least as large as the thumbnail asked for and has
  the picture's shape, it is used and the photo is not decoded at all: under
  1 ms instead of about 80 ms for a 24-megapixel photo (one thread, Linux
  server processor), so the grid of a photo folder fills in almost at once. A
  preview padded to another shape, damaged, or smaller than the thumbnail is
  skipped; one with its own orientation tag follows that tag. The preview
  pane and Properties ask for larger sizes than cameras embed, so they still
  decode the photo at reduced size as in 1.1.2.

## Properties

- A folder's Properties counts its files, folders and size through every
  subfolder as soon as it opens, instead of after a "Calculate totals" click.
  The one button stops the walk, and starts it again once stopped or failed;
  closing the dialog stops it too.

## CI, releases and maintenance

- A release run stops at validation when its version is already published,
  so the installer, signature and updater manifest people installed are
  never replaced. A draft can still be rebuilt.
- Dependabot's NuGet updates failed the locked restore (NU1004) because
  Dependabot regenerates the lock file of the project it bumps but not those
  of the projects that reference it. The `nuget-locks` maintenance recipe
  repairs that: it applies the pull request's package versions to current
  `main`, restores every lock file and proposes a superseding pull request,
  which the merge gate verifies by replaying the restore. The three packages
  `docs/PLUGINS.md` pins for plugin authors leave the grouped update, major
  versions of `System.*` and `Microsoft.Extensions.*` are ignored, and
  `npm run check:dotnet-packages` holds the project files to the same rule.
- `System.Drawing.Common` is back on the .NET 10 line (10.0.11); the 11.0.0
  bump had mixed the next .NET's runtime packages into the app build.
- The Windows UI smoke waits for the state each step needs instead of fixed
  delays in five places, reports managed memory figures that compare between
  runs, and has a `thumbnails` scenario: a folder of camera-like photos with
  embedded previews and portrait orientation tags, timed for the viewport,
  cold and warm, with every portrait checked to come out upright.

## Upgrading

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after
active transfers finish. Choose the same installation format used for v1.1.2.
Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for
published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.1.3_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.1.3_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.1.3_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `SumaFile_1.1.3_x64-winui-setup.exe.sig` | Detached signature for the NSIS installer |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.1.3. Settings, bookmarks, profiles,
connections and plugins are kept.

## Dogfood 10-Step Script

1. Install 1.1.3 over a disposable v1.1.2 installation.
2. Open a folder of phone or camera photos in a thumbnail view: portraits are upright, and the thumbnails fill in almost at once.
3. Select a portrait photo and open the preview pane, then Properties: both show it upright.
4. Open a folder of PNG screenshots, and one with GIF, WebP and BMP files: each has its thumbnail.
5. Open Properties on a folder with many subfolders: the count starts at once; press Stop, then Count, and let it finish.
6. Open Properties on a folder you cannot read fully: the reason is shown with Try again.
7. Leave a photo folder and come back: the thumbnails appear at once from the cache.
8. Start a Release run on the merged commit before bumping the version: it stops at validation with "already published".
9. Watch the next Dependabot NuGet pull request: if it fails the locked restore, a `Maintenance: nuget-locks` pull request appears and merges.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.1.2 baseline.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

- Orientation of PNG and WebP files that carry Exif is handled by the image
  library's own reading and has not been checked by hand.
- The preview pane loads large images directly through WinUI's image
  control, not through the thumbnail code; whether it honours orientation for
  a photo too large for the shell thumbnail has not been checked.
- The `nuget-locks` recipe has been run by hand against a failing Dependabot
  pull request, but not yet by the workflow itself.
- The installer build and the installer smokes have not run for this build;
  run them on the release commit.
- The thumbnail, Properties and smoke changes were verified by unit tests and
  CI, not on a Windows desktop by hand.

Checklist before tagging `v1.1.3`: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.
