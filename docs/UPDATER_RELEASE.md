# SumaFile Updater Releases

Every SumaFile release publishes updater metadata, `latest-winui.json`, next
to its installers. Installed apps read it from:

```text
https://github.com/conniecombs/SumaFile/releases/latest/download/latest-winui.json
```

The flow that prepares, builds, signs and publishes a release is in
[the release runbook](../.github/RELEASE.md). This document covers what is
specific to the updater: the signing key, what a release publishes for it,
what the app verifies, and how to test it.

## One-time signing setup

The updater uses one Ed25519 key pair. The private key signs the NSIS setup
executable when a release is built. The public key is compiled into
`simplefile-service` and verifies the download.

The private key must be an Ed25519 key in PEM/PKCS#8 form. Any tool that
writes one works; with Node.js:

```powershell
New-Item -ItemType Directory -Force .secrets | Out-Null
node -e "const k=require('node:crypto').generateKeyPairSync('ed25519').privateKey.export({type:'pkcs8',format:'pem'});require('node:fs').writeFileSync('.secrets/updater-private-key.pem',k)"
```

`.secrets/` is ignored by Git. Never commit the private key.

The public key is the base64 form of the 32-byte Ed25519 public key. The
signer prints it as `publicKey` whenever it signs a file:

```powershell
$env:SIMPLEFILE_SIGNING_PRIVATE_KEY = Get-Content .secrets\updater-private-key.pem -Raw
node scripts/sign-update-payload.mjs --file=README.md --out=.secrets/probe.sig
```

Store the pair in the repository's GitHub Actions settings:

| Name | Kind | Value |
| --- | --- | --- |
| `SIMPLEFILE_SIGNING_PRIVATE_KEY` | Secret | The private key: the PEM text, the PEM with its line breaks written as `\n`, or the PEM base64-encoded |
| `SIMPLEFILE_SIGNING_PRIVATE_KEY_PASSWORD` | Secret | The key's passphrase; only for an encrypted key |
| `SIMPLEFILE_UPDATER_PUBLIC_KEY` | Variable | The base64 public key |

The names keep the `SIMPLEFILE_` prefix: the release workflow, the build
script and the service read them under these names.

`scripts/sign-update-payload.mjs` rejects a key that is not Ed25519, and fails
when `SIMPLEFILE_UPDATER_PUBLIC_KEY` is set and differs from the public key
derived from the private key.

The public key is fixed in each build. An installed app accepts only updates
signed with the private key that matches the key it was built with.

## What a release publishes for the updater

`scripts/build-winui-release.ps1` does the updater's part of a release build:

1. It builds `simplefile-service`. When `SIMPLEFILE_UPDATER_PUBLIC_KEY` is set
   in the environment, its value is compiled into the service as the public
   key.
2. It takes the size and SHA-256 of the NSIS setup executable.
3. When `SIMPLEFILE_SIGNING_PRIVATE_KEY` is set, it signs the setup with
   `scripts/sign-update-payload.mjs`: an Ed25519 signature over the file's
   bytes, base64-encoded, written beside the setup as a `.sig` file.
4. It writes `latest-winui.json` with `scripts/write-latest-winui.mjs`. The
   manifest holds the version (`<InformationalVersion>` from
   `src-winui/Directory.Build.props`; the release tag is `v` followed by it),
   the channel `stable`, and one `windows-x86_64` entry with the setup's
   download URL under
   `https://github.com/conniecombs/SumaFile/releases/latest/download/`, its
   size, SHA-256 and signature.

The release workflow passes `-RequireUpdaterSignature`. The build then fails
when the private or public key is missing, when they do not match, or when the
manifest would lack a signature, hash or size. Do not publish a release built
without it.

`npm run check:updater`, part of `npm run check`, fails when this wiring is
removed: when the manifest writer stops publishing under the GitHub release
URL, when the signer, the build script or `updater.rs` lose their signing and
verification steps, or when the version fields disagree.

## What Settings -> Updates does

**Check for updates** calls `check_for_update`. The service reads the manifest
and compares its version with the running app's version, number by number.
With equal numbers, a release is newer than its own prerelease and a
prerelease is never offered over its release. When the manifest is not newer,
Settings reports that the latest version is installed.

When it is newer, the service also reports whether the update is installable
in the app. It is installable only when the manifest's entry for this platform
has a trusted URL, a signature, a 64-character hex SHA-256 and a non-zero
size, and this build has a public key. Settings then offers **Download and
install**. Otherwise it offers **Open download page**, which opens the
repository's releases page.

**Download and install** calls `install_update`, which is fail-closed:

1. It reads the manifest again and requires a newer version.
2. It requires the package URL to start with
   `https://github.com/conniecombs/SumaFile/releases/`, to end with
   `_x64-winui-setup.exe`, and to contain no backslash or space.
3. It requires the signature, SHA-256 and size from the manifest, and a public
   key compiled into this build.
4. It downloads the setup into the temporary folder and stops as soon as more
   bytes arrive than the manifest declares.
5. It compares the byte count and the SHA-256 with the manifest.
6. It reads the downloaded file back from disk and verifies the Ed25519
   signature against the compiled-in public key.
7. Only then does it start the setup, silently (`/S`).

A failed download or verification deletes the downloaded file. Any failure
returns its error; Settings shows it and switches its button to **Open
download page**.

The setup ends the running SumaFile processes and installs the new version. It
does not start SumaFile again.

### Install kinds

The updater does not check how the running copy was installed. For every kind
it downloads the NSIS setup and runs it the same way. Run without a target
folder, the setup installs into the folder a previous NSIS install recorded in
the registry, or else into `%LOCALAPPDATA%\Programs\SumaFile-WinUI`.

| Running copy | Result of an in-app update |
| --- | --- |
| NSIS install | Upgraded in place, in its recorded folder |
| MSI install | The NSIS setup still runs. With no earlier NSIS install it writes into `%LOCALAPPDATA%\Programs\SumaFile-WinUI`, which is also the MSI's default folder, and adds its own uninstall entry; the MSI's registration with Windows Installer is not updated. To upgrade an MSI install as an MSI, install the new `.msi`: its major upgrade reuses the folder the MSI remembered |
| Portable zip | The portable folder is not updated. The setup creates or upgrades a per-user NSIS installation and ends the running portable copy. To update a portable copy, extract the new zip |

## Testing

The service reads two overrides each time it loads the manifest. Set them in
the environment SumaFile is started from; the service inherits it.

| Variable | Effect |
| --- | --- |
| `SIMPLEFILE_UPDATE_MANIFEST_JSON` | The manifest JSON itself; takes precedence |
| `SIMPLEFILE_UPDATE_MANIFEST_PATH` | The path of a manifest file; read when the JSON variable is not set |

The overrides replace only the manifest. The package URL in it must still pass
the trusted-URL check, and a download must still match its size, hash and
signature under the public key compiled into the build.

A local updater-enabled build needs NSIS, both keys in the environment and the
signature switch:

```powershell
$env:SIMPLEFILE_SIGNING_PRIVATE_KEY = Get-Content .secrets\updater-private-key.pem -Raw
$env:SIMPLEFILE_UPDATER_PUBLIC_KEY = '<base64 public key>'
npm run release:build -- -RequireUpdaterSignature
```

`release:build` runs the payload startup smoke, the packaged file-operation
smoke and the MSI extract-and-launch smoke. It does not install the NSIS
setup: `npm run smoke:winui-installer` and `npm run smoke:winui-upgrade` do,
and the [release runbook](../.github/RELEASE.md#local-packaging) lists what
each needs.

No smoke test runs `install_update` end to end. The upgrade smokes run the
same NSIS setup silently over an NSIS install, but they do not go through the
updater's download and verification, and none covers an in-app update of an
MSI install or a portable copy. Proving the in-app path needs a signed test
release.
