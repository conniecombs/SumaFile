# Security Policy

## Supported Branch

Security reports for this branch should be evaluated against the current Windows-focused release scope: local files, mapped network drives, archives, previews, metadata, Git status, cleanup tools, updater metadata, and Windows installers.

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
