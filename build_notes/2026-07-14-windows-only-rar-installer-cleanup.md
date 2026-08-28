# 2026-07-14 Windows-Only RAR Installer Cleanup

Reason:
- The program scope is Windows only.
- The previous RAR hardening patch kept and updated non-Windows RARLab paths
  because they were already present in the module. That does not match the
  intended product scope.

Changes made:
- Removed Linux and macOS RARLab download URLs and pinned hashes from
  `src-tauri/src/rar_installer.rs`.
- Removed non-Windows cfg branches from the prepared-install/token flow.
- Removed the Unix tar.gz extraction helper.
- Kept only the Windows WinRAR installer path:
  `https://www.rarlab.com/rar/winrar-x64-723.exe`
- Kept the Windows SHA-256 pin:
  `8ff0daf3ed564cc743c0e23ff2e253997ffc74460f9673f0b6dd037b2db4ce7b`
- Kept Windows Authenticode verification for publisher `win.rar GmbH`.

Validation passed:
- `cargo fmt --all -- --check`
- `cargo test --locked --all-features`
- `cargo clippy --locked --all-targets --all-features -- -D warnings`
- `npm run check`
