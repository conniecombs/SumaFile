# SumaFile 1.1.2 Release Notes

Prepared November 18, 2026. This Windows x64 release makes new thumbnails
faster. Nothing else changed since 1.1.1: file formats, plugins, settings and
the IPC contract are the same.

## Thumbnails

- **Photos are decoded at reduced size.** When a thumbnail is much smaller
  than the picture, a JPEG is decoded straight to a half, a quarter or an
  eighth of its size. The file service used to decode the whole photo and
  then shrink it, and decoding was about 80% of the time a new thumbnail
  took. The reduced decode keeps at least two source pixels for each
  thumbnail pixel, so the thumbnail is still an average of the picture.
- **Scaling uses SIMD.** Every 8-bit image is scaled with SSE4.1 or AVX2
  where the processor has them: a Lanczos filter for sources up to
  2.5 megapixels, a box average above that.
- **What you will notice.** The first visit to a folder of photos fills in
  its thumbnails about twice as fast. A folder you have visited before is
  unchanged: its thumbnails already come from the disk cache.

Time for one new 128-pixel thumbnail, one thread, on generated test photos
(a Linux server processor; times on your machine will differ):

| Image | 1.1.1 | 1.1.2 |
| --- | --- | --- |
| 24-megapixel JPEG | 209 ms | 89 ms |
| 12-megapixel JPEG | 111 ms | 47 ms |
| 12-megapixel progressive JPEG | 222 ms | 173 ms |
| 2-megapixel JPEG | 17 ms | 10 ms |
| 12-megapixel PNG | 120 ms | 99 ms |
| 2-megapixel PNG | 20 ms | 17 ms |

Unchanged: the size of a thumbnail, the formats that have one, the disk
cache and what is in it, and the result for CMYK and 16-bit JPEGs, damaged
files, pictures smaller than the thumbnail and images deeper than 8 bits per
channel.

Two Rust crates are new: `fast_image_resize` 6.1 and `jpeg-decoder` 0.3
(without its worker threads), both MIT or Apache-2.0.

## Upgrading

Windows 10 version 2004 or later, or Windows 11, x64. Close SumaFile after
active transfers finish. Choose the same installation format used for v1.1.1.
Use the [Releases page](https://github.com/conniecombs/SumaFile/releases) for
published files.

| Artifact | Use |
| --- | --- |
| `SumaFile_1.1.2_x64-winui-setup.exe` | Per-user install or upgrade |
| `SumaFile_1.1.2_x64-winui.msi` | Windows Installer deployment |
| `SumaFile_1.1.2_x64-winui-portable.zip` | Extract the complete archive into a fresh folder and launch SumaFile.exe |
| `SumaFile_1.1.2_x64-winui-setup.exe.sig` | Detached signature for the NSIS installer |
| `latest-winui.json` | Signed updater manifest used by the application |

Open About after upgrading and check for 1.1.2. Settings, bookmarks, profiles,
connections and plugins are kept.

## Dogfood 10-Step Script

1. Install 1.1.2 over a disposable v1.1.1 installation.
2. In Settings -> Storage & cache, point the thumbnail cache at an empty folder so every thumbnail is made afresh.
3. Open a folder of camera or phone photos in a thumbnail view: the thumbnails fill in visibly faster than in 1.1.1.
4. Compare a few thumbnails with the photos: same picture, same orientation as 1.1.1, no colour shift.
5. Open a folder of PNG screenshots, and one with GIF, WebP and BMP files: each has its thumbnail.
6. Open a folder with very small images (icons): they look as they did in 1.1.1.
7. Select a photo and open the preview pane, then Properties: both show the picture.
8. Leave the folder and come back: the thumbnails appear at once from the cache.
9. Put a file that is not a picture but is named `.jpg` in the folder: it gets no thumbnail and nothing else is disturbed.
10. Verify MSI, NSIS and portable startup and exercise both the latest-release upgrade and `npm run smoke:winui-upgrade-from-ref` against the v1.1.1 baseline.

## SimpleFile Data Import

No manual import is needed for normal SimpleFile-to-SumaFile use. SumaFile keeps the existing application data identifier, `com.simplefile.desktop`.

## Known Limitations

- PNG, GIF, WebP and BMP have no reduced-size decode: they gain only the
  faster scaling, 10% to 20%.
- A progressive JPEG gains about 20%, not half: most of its decoding cannot
  be skipped.
- The measurements are from generated photos on a Linux server processor,
  not from real photos on Windows.
- The installer build and the installer smokes have not run for this build;
  run them on the release commit.
- The thumbnail change was verified by unit tests and CI, not on a Windows
  desktop by hand.

Checklist before tagging `v1.1.2`: run the repository, Rust, .NET, packaging and Windows UI checks on the release commit. Require a signed test release before claiming in-app updater installation is proven.
