# Security Policy

## Supported Branch

Security reports for `main`, and the latest release, SumaFile 1.1.0, should be evaluated against the Windows release scope: local files, mapped network drives, archives, previews, metadata, cleanup tools, updater metadata, and Windows installers.
See [v1.0.4 release notes](RELEASE_1.0.4.md) for the release boundary.

The plugin system, new in SumaFile 1.1.0, moves Git status and the Git workbench, FTP/FTPS/SFTP sessions and 7-Zip archives into the bundled plugins `com.sumafile.git`, `com.sumafile.remote` and `com.sumafile.sevenzip`. Reports against those plugins and against the plugin host are in scope; see [Plugins](#plugins).

## Sensitive Files

Do not commit:

- Updater private keys.
- `.env` files or `.env.*` files.
- Local signing secrets.
- Personal settings exports.
- Plugin signing keys (`signing-key.pem` from `scripts/sign-plugin.mjs keygen`, or any other `*.pem`). Keep them in a
  folder outside every repository; `keygen` refuses a git work tree unless `--allow-repository` is passed.
- Paths or logs containing private user data unless redacted.

The repository ignores `.secrets/`, `*.key`, `*.pem`, `.env`, and `.env.*`.

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

## Plugins

SumaFile has no plugin sandbox. A plugin's UI half is .NET code loaded into
`SumaFile.exe`, and its backend is a separate process that runs as the
current user. An enabled plugin can do anything the user can do.

- **Consent.** SumaFile enables a plugin it did not ship only after the user
  answers **Enable plugin?**, which shows the name, publisher, version, id,
  signature state, declared `permissions` (informational, never enforced) and
  whether it runs a background process. The decision is stored in
  `<install folder>\plugin-approvals.json` with the SHA-256 of `plugin.json`,
  the signature state and the plugin folder. SumaFile asks again when any of
  the three changes, and after Install from file or Remove, which clear the
  plugin's stored approval. The three bundled plugins are trusted without a
  prompt only from the app's own `plugins` folder and only with the
  `plugin.json` hash that `plugins\bundled.json` lists.
- **Unsigned plugins.** Only `plugin.json` is hashed for consent. An unsigned
  plugin whose DLL or executable changes on disk keeps its approval. This is
  deliberate: the install folder is writable by the same user, and write
  access to it is already full control of SumaFile itself.
- **Signatures.** `signature.json` holds the SHA-256 of each plugin file and an
  Ed25519 signature over `plugin.json` and that list, checked with the
  `publisher.publicKey` in the plugin's own `plugin.json`. Settings shows a
  valid one as "Signed · key <fingerprint>". It proves that the files are
  unchanged since they were signed with that key; it does not prove who the
  publisher is, because the key comes from the plugin itself. A listed file
  that is missing or changed, or any file in the folder that `signature.json`
  does not list, makes the signature Invalid. `signature.json` is capped at
  1 MiB. The signature is checked when plugins are scanned (startup and
  Rescan), not on each load; the hashes are computed at scan time.
- **`.sfplugin` packages.** Install from file refuses, before writing
  anything: a file that is not a readable zip; a package without `plugin.json`
  at its root, with a `plugin.json` over 1 MiB, or with an invalid manifest; a
  package whose id is a bundled plugin; more than 10,000 entries or more than
  512 MiB uncompressed; and any entry with an absolute path, a drive or `:`, a
  `..` segment, a Windows device name (`CON`, `NUL`, `COM1` and the like), a
  segment ending in a dot or a space, or a path that would land outside the
  plugin folder. It extracts into a hidden staging folder and moves it into
  place.
- **Backend pipes.** The host checks that the server of a plugin's pipe is
  the backend process it launched and that it runs as the current user, so
  another process cannot pose as a plugin's backend. Backends run in a kill-on-close job owned by
  SumaFile, so they end with SumaFile.
- **Overrides.** `SUMAFILE_PLUGIN_ROOTS`, `SUMAFILE_PLUGIN_DATA_ROOT`,
  `SUMAFILE_PLUGIN_APPROVALS_PATH` and `SUMAFILE_PLUGIN_LOG_PATH` are test and
  development overrides, and release builds honour them too. Each one in
  effect is logged as a `[warn]` line in `plugins.log`. Anyone who can set the
  user's environment can already run code as that user.
- **Files and uninstall.** Every plugin file stays in the installation folder:
  `plugins\`, `plugins-data\` (each backend's data, including the FTP/SFTP
  profiles), `plugin-approvals.json` and `plugins.log`. Upgrades keep them.
  Uninstall removes all four, including user-installed plugins and the
  FTP/SFTP profiles. Saved FTP/SFTP passwords and passphrases stay in Windows
  Credential Manager under `SumaFile.Remote.*` targets; delete them there, or
  delete the profiles in SumaFile before uninstalling.

Areas to review carefully: the manifest reader's bounds, the package
installer's path checks, the signature verifier, the approval store's change
detection, and the plugin pipe's peer checks. The author guide,
[PLUGINS.md](PLUGINS.md), documents the plugin-facing rules.
