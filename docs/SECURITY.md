# Security Policy

## Supported Branch

Security reports for `main` and SumaFile 1.0.3 should be evaluated against the Windows release scope: local files, mapped network drives, archives, previews, metadata, Git status, cleanup tools, FTP/FTPS/SFTP sessions in the bundled FTP/SFTP plugin (`com.sumafile.remote`), updater metadata, and Windows installers.
See [v1.0.3 release notes](RELEASE_1.0.3.md) for the release boundary.

## Sensitive Files

Do not commit:

- Updater private keys.
- `.env` files or `.env.*` files.
- Local signing secrets.
- Personal settings exports.
- Paths or logs containing private user data unless redacted.

The repository ignores `.secrets/`, `*.key`, `.env`, and `.env.*`.

## Reporting

When reporting a vulnerability, include:

- A concise impact summary.
- A reproduction path using local files or a disposable test directory.
- Expected and actual behavior.
- Whether the issue affects the dev build, installed app, updater, NSIS installer, or MSI installer.

## Areas To Review Carefully

- Path validation and symlink handling before delete, rename, copy, move, folder size, and metadata operations.
- Archive virtual paths and extraction destination validation.
- Updater signature configuration and release artifact handling.
- Windows elevated PowerShell launch behavior.
- Mapped network drive naming and filesystem probing.
- File preview limits for large or untrusted files.

## Transfer recovery and remote sessions

Review ownership locks and filesystem identities before any staging/backup cleanup
or source deletion. Recovery journals contain source/destination paths and must
be redacted in reports. Unknown artifacts must not be treated as owned by name
alone. Checksum mode verifies content but is not a substitute for TLS/SSH peer validation.

FTP, FTPS and SFTP live in the bundled plugin `com.sumafile.remote`, not in the
file service. Its backend (`sumafile-remote-backend.exe`) runs in a kill-on-close
job owned by SumaFile and talks only to SumaFile over its plugin pipe. It owns
profiles (`<install folder>\plugins-data\com.sumafile.remote\remote-profiles.db`; every plugin file stays in the installation folder),
sessions, host-key trust and its own Windows Credential Manager code: secrets stay
under the `SumaFile.Remote.` targets that 1.0.4 wrote, are never returned over the
pipe, and are deleted with their profile. It imports 1.0.4 profiles read-only and
never writes the old `metadata.db` table. Plain FTP requires explicit
insecure-connection acknowledgement, and a changed SFTP host key fails with a
mismatch error instead of connecting. A backend killed mid-transfer cannot clean up:
its staged download is recovered by the next download into that folder, and an
interrupted upload's `.sumafile-partial.<nonce>` file stays on the server.

The release audit and bounded security updater share the checked-in advisory
policy in `scripts/lib/audit-policy.mjs`; existing documented exceptions remain.
Eligible patch proposals require CI and installer validation.
