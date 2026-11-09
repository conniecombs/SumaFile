# 7-Zip Windows x64 Console Bundle

SumaFile bundles the x64 console files from the official 7-Zip Extra package:

- Version: 26.03 (`7za.exe` reports `7-Zip (a) 26.03 (x64)`)
- Release date: 2026-09-03
- Binaries: https://github.com/ip7z/7zip/releases/download/26.03/7z2603-extra.7z
- Upstream download page: https://www.7-zip.org/download.html

These files ship in the bundled SumaFile 7-Zip plugin, in the release payload
folder `plugins\com.sumafile.sevenzip`, beside the plugin's `plugin.json` and
`sumafile-sevenzip-backend.exe`. That folder holds:

- `plugin.json`
- `sumafile-sevenzip-backend.exe`
- `7za.exe`
- `7za.dll`
- `7zxa.dll`
- `License.txt`
- `SUMAFILE_BUNDLE.md` (this file)

`readme.txt` from the Extra package stays in `third_party/7zip/win-x64` and is
not shipped. Keep `License.txt` with the binary files in SumaFile release
artifacts.

## Source

`third_party/7zip/source/7z2606-src.tar.xz` is the 7-Zip 26.06 source. It was
added with `tools/sevenz-evaluation` and is not the source of these binaries.
The matching 26.03 source is the one published with the upstream 26.03
release.
