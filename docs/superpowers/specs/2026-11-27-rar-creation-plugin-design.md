# RAR creation plugin, and opening protected RAR archives

Date: 2026-11-27. Status: design, awaiting approval.

## Intent

SumaFile users want to **make RAR archives**. Today SumaFile lists and extracts RAR files but refuses to create them
(`crates/simplefile-core/src/archive/create.rs`: "RAR creation is not supported by SumaFile").

The user wants this to come from a plugin built on [rars](https://github.com/bitplane/rars), a Rust RAR library, and
wants to **choose options for each archive** they create: a password, hidden file names, splitting into parts,
compression level, solid compression and a recovery record.

Because a password can be set, SumaFile must also **open the protected RAR files it makes**. Today it can't open any
protected archive: no password prompt exists anywhere. Producing an archive you can't open in the same app would feel
broken.

## Goals

1. Create RAR archives (RAR5 format) from the selected files and folders, from the existing **Create archive** dialog.
2. When RAR is picked, the dialog shows RAR options, chosen per archive:
   ```
   Archive name   [Photos.rar                ]
   Format         [RAR                      ▾]
   ── RAR options ───────────────────────────
   Password       [••••••••] Confirm [••••••••]
                  [ ] Also hide the file names
   Split into parts of  [      ] MB   (blank = one file)
   Compression    [Normal                   ▾]
   [x] Solid (smaller, slower to open one file)
   [ ] Recovery record  [ 3 ] %
   ```
3. Split archives are named `Photos.part1.rar`, `Photos.part2.rar` and so on.
4. Creation runs as a tracked archive operation, like creating a 7z today: progress shows, Stop works, and a failed or
   stopped create leaves no archive and no part files behind.
5. Browsing into a protected RAR, opening a file from one, extracting one, or copying out of one asks for the password.
   A wrong password asks again. If the archive hides its file names, SumaFile asks before it can list anything.
6. The password is held in memory only, never saved. SumaFile forgets it when you leave that archive.
7. Opening the first part of a split archive shows the whole set. A missing part gives a message that names it.
8. Older RAR versions keep opening as they do now.
9. The plugin ships with SumaFile, like the 7-Zip plugin. Turning it off removes RAR **creation** only; RAR files,
   protected ones included, still open.

## Non-goals (this round)

- Renaming, deleting or adding files inside an existing RAR. rars can rebuild an archive, but that is slow for big
  solid archives and raises password questions. Existing RARs stay read-only, as today.
- Passwords for ZIP and 7z. The prompt and the password store are built so those formats can use them later.
- Writing RAR 4 or older formats, or the RAR 7 large-dictionary variant. RAR5 opens in WinRAR 5 and later and in 7-Zip.
- Archive or file comments.
- A lock icon or other marker on protected entries in a listing.
- Remembering passwords between sessions, or remembering the dialog's last-used RAR options.

## Anti-goals (failure even if it "works")

- An archive the plugin writes that WinRAR or 7-Zip can't open.
- A half-written archive, or stray part files, left under the archive's name after a failure or Stop during create.
  For a crash, see design section 2.
- A password written anywhere: settings, logs, the operation journal, progress text, error messages, crash reports.
- What a protected archive holds left readable on disk after it was opened: a thumbnail of one of its entries in the
  disk thumbnail cache, or one of its folders in the saved tabs and recent places.
- A password prompt popping up from background work (thumbnails, the preview pane, lazy columns, search, folder
  sizes) instead of from something the user did.
- RAR files no longer opening because the plugin is turned off or failed to start.
- A slower start, or CPU used while idle.

## Constraints

- **New dependencies are allowed.** This work adds Rust crates (`rars` and its dependencies) and needs no new NuGet
  package.
- **The 50 ms startup budget for plugin discovery is not raised** (`docs/ARCHITECTURE.md`, "Startup budget"). A plugin
  with `archiveFormats` starts its backend at startup, after the discovery stages, so it costs a process for the whole
  session. It must use no CPU while idle.
- **No CPU-burning helpers.** Compression uses CPU only while a create the user started is running. The plugin's
  worker threads exist only for the length of a create.
- **The plugin writes with rars only.** RARLAB's unRAR licence forbids using its code to re-create the RAR compression
  algorithm. The plugin must never use the `unrar` crate to write. Using it to read archives back in tests is fine.
- **Test-first.** Every behaviour change starts with a failing test.
- SumaFile is a Windows app. The Rust crates and the C# logic tests run on Linux in development. The WinUI dialog and
  plugin host need Windows CI, which the user starts from the Actions tab.
- `rars` 0.13.1 needs Rust 1.89. The workspace toolchain is 1.98.0.

## Approaches considered

**A. rars writes and reads.** The plugin takes over `.rar` and the built-in `unrar` reader is removed, as `.7z` works
today. This drops the service's C++ RAR code and its last use of the old `winapi` crate. But rars's reader is new ("It
could use more testing at volume," says its README), so a RAR that opens in WinRAR might not open here. Turning the
plugin off would also stop all RAR files from opening.

**B. The plugin reads while it is on, and the built-in reader takes over when it is off.** Dropped: two readers to
maintain, and RAR behaves differently depending on whether the plugin is on.

**C. rars writes, SumaFile's own reader opens. Chosen.** The built-in reader stays and learns to use passwords. The
`unrar` crate already supports passwords, hidden names and split sets, and it carries RARLAB's version 7 code. The
plugin only creates `.rar` files. The core needs one rule change: a plugin may add creation for a format the core can
only read.

The user chose C because opening stays on the most compatible reader, protected archives open whether or not the
plugin is on, and the change is smaller. Tests read every archive the plugin makes back with RARLAB's own reader, which
checks rars's output independently. The cost is that the C++ code stays.

## Design

### 1. The RAR plugin (`plugins/rar/`)

- Id `com.sumafile.rar`, name "RAR archives", publisher and `version` as the other bundled plugins.
  `sdkVersion` `1.1.0` (see 4). Backend `sumafile-rar-backend.exe`, `acceptsMultipleClients: true`, permission
  `backend-process`. It has no UI assembly.
- `contributions.archiveFormats`: one entry, `.rar`, `canCreate: true`, `canList`, `canExtract` and `canModify` false,
  `engineName` "RAR", and the `createOptions` below.
- The backend is a Rust crate at `plugins/rar/backend`, added to the workspace, built and staged like
  `plugins/sevenzip/backend`, on the same plugin SDK crate (`crates/sumafile-plugin`). It depends on `rars` with the
  `write`, `encryption`, `recovery` and `parallel` features.
- It serves `archive.create` only. Every other `archive.*` method answers an error saying the RAR plugin only creates
  archives. The service never calls them, because the format does not claim them.
- The plugin walks the selected folders itself and follows SumaFile's ZIP creation rule for links: regular files and
  folders are added, and symbolic links and junctions are skipped, not followed. Entry names are relative to each
  selected item's parent, as for ZIP: selecting `C:\a\Photos` stores `Photos/...`. Modification times are kept.
- Options map to `rars::Builder` (format `rar50`):

  | Option key | Type | Label | Default | Maps to |
  | --- | --- | --- | --- | --- |
  | `password` | password | Password | empty | `.password(...)`; empty means none |
  | `hideNames` | boolean | Also hide the file names | false | `.header_encryption(true)`; needs `password` |
  | `partSizeMb` | integer, min 0 | Split into parts of (MB) | 0 | `.volume_size(...)`; 0 or blank means one file |
  | `compression` | enum: Store, Fastest, Fast, Normal, Good, Best | Compression | Normal | `.store(true)` for Store; levels 1 to 5 for the rest |
  | `solid` | boolean | Solid (smaller, slower to open one file) | true | `.solid(...)` |
  | `recovery` | boolean | Recovery record | false | with `recoveryPercent`, `.recovery_percent(...)` |
  | `recoveryPercent` | integer, 1 to 100 | % | 3 | needs `recovery` |

- Progress: rars's `WriteProgress` events become `archive.progress` notifications with the bytes done, the total
  bytes and the current file. Its `is_cancelled` reads the flag that `cancel` sets.
- Split parts are written through rars's volume sink, named `<stem>.part<N>.rar`. `<stem>` is the archive name without
  `.rar`. `N` is zero-padded to the number of digits that the total input size divided by the part size predicts, as
  WinRAR does (`part01` … `part12`). Part 1 is still found by its own name, and unrar finds later parts by counting
  up.
- Compression threads: the rars pool is built for each create and dropped afterwards. It uses at most the number of
  logical cores minus one (at least one), so the machine stays responsive while compressing.
- rars's Apache-2.0 licence text ships in the plugin folder, the way `License.txt` ships with 7-Zip.

### 2. Core: an engine may add creation to a format the core reads

- `engine.rs`: `register_engine` and `replace_engines` currently drop every format whose extension a built-in format
  handles (`is_registrable_extension`). New rule: for an extension the core reads itself, a registration keeps only
  **creation**. `canList`, `canExtract` and `canModify` are cleared, and the format is kept only when `canCreate` is
  true. Every other extension is handled as now. The rule is general, but `.rar` is the only built-in read-only format
  today.
- Lookups: `engine_for_path` keeps returning `None` for built-in formats, so listing, extracting, reading entries,
  copying out and every mutation still go to the built-in code. A new lookup, used only by creation, finds the engine
  that creates a given extension.
- `create.rs`: `"rar"` goes to that engine when one is registered. With none, the error says creating RAR archives
  needs the RAR plugin, phrased as the `.7z` placeholder note is.
- `capabilities.rs`: the RAR row reports `canCreate: true` and the creating engine while the plugin is registered,
  `false` otherwise. Its note changes to match. Listing and extraction stay `built-in-unrar`.
- **Creating through a private folder.** Today an engine writes one temporary file beside the target, which is then
  renamed into place. A split set is several files, so the core instead makes an empty private folder beside the
  target and passes the engine `archivePath` = `<private folder>\<archive name>`. When the engine succeeds, the core
  moves `<archive name>`, or the part files `<stem>.part<N>.rar` numbered from 1 with no gaps, into place, each move
  refusing to replace a file. Anything else in the folder is deleted with it. If a target name already exists, or any
  move fails, the core removes the parts it already moved and the folder, and reports which name was in the way. On
  failure or Stop, the folder and everything in it are removed. The 7-Zip plugin writes only `archivePath`, so it
  works unchanged. Its tests with the real 7za must still pass.
- Crash safety: the private folder is named like today's temporary file, `.<stem>.simplefile-new-<nonce>-<i>`, and
  is hidden. A crash during create leaves that folder behind, just as a crash leaves the hidden temporary file today.
  Nothing cleans it up at the next start in this round. The moves into place are renames on the same volume, done
  last part first and part 1 last. A crash in the middle of them can leave some later parts under their real names,
  but never a part 1 that looks complete without the rest.

### 3. Core and service: passwords for the built-in RAR reader

- **A password store in the service**, in memory only, keyed by the archive. A split set is keyed by its first part,
  so opening `Photos.part3.rar` uses the same entry as `Photos.part1.rar`. Two new IPC methods:
  - `set_archive_password { archivePath, password }` → `null`
  - `forget_archive_password { archivePath }` → `null`

  Forgetting overwrites the stored bytes. The store is never written to disk, and a service restart empties it.
- **A protected archive's contents stay off disk too** (the user's decision, 2026-11-28). An archive is protected
  when a password is stored for it, or when it asked for one. For such an archive:
  - Thumbnails of its entries are made and shown as usual, and are kept in memory only: none is written to the disk
    thumbnail cache.
  - Its folders are not written to the saved tabs or the recent places. A tab inside it is restored as the folder
    that holds the archive.
  - The temporary copy made to open or preview an entry is deleted as it is today.
- **Every built-in RAR read uses the stored password** when one is set: listing (`list.rs`), extraction (`extract.rs`,
  both paths), reading one entry, materializing an entry to a temp file, and copying or moving out through
  `copy_entry_resolved` and `move_entry_resolved`. They use `unrar::Archive::with_password` instead of
  `Archive::new`.
- **Typed password errors.** When unrar reports a missing password (`ERAR_MISSING_PASSWORD`) or a wrong one
  (`ERAR_BAD_PASSWORD`), the service answers with `ERR_APPLICATION` and
  `data: { "archivePassword": "required" | "wrong", "archivePath": "<key archive path>" }`. The app acts on that data,
  never on message text. In RAR 4 archives, which carry no password check, a CRC failure on an encrypted entry while a
  password is set counts as `"wrong"`.
- **Passwords in any language.** A password with any characters works, for example `contraseña`, `пароль` or `密码`.
  rars stores a RAR5 password as UTF-8 and a RAR 4 password as UTF-16, as WinRAR does. The `unrar` crate, though,
  passes the password to RARLAB's code as narrow text (`RARSetPassword` after `CString::new(pw).unwrap()`,
  `unrar-0.5.8/src/open_archive.rs`). RARLAB's code converts that with the system ANSI code page on Windows and
  the C locale on Linux, so today only ASCII passwords would open. The fix is the builder's choice, proven by a
  non-ASCII round-trip test on Windows CI and on Linux:
  - **Preferred:** give the service process a UTF-8 active code page (an application manifest with
    `<activeCodePage>UTF-8</activeCodePage>`, embedded at build time; a new Rust build crate is allowed). Check that
    nothing else in the service relies on the ANSI code page. The Linux tests set a UTF-8 locale.
  - **Fallback:** patch the `unrar` crate through `[patch.crates-io]` so it answers RARLAB's wide-password callback
    (`UCM_NEEDPASSWORDW`, exposed by `unrar_sys`) instead of calling `RARSetPassword`.

  A password may not contain NUL or other control characters. The dialog and the prompt refuse them, and the service
  answers an error instead of panicking.
- **Listing says what is protected.** Archive listing entries gain `encrypted: bool` in the core model and the IPC
  schema. RAR fills it from unrar's `is_encrypted`, and other formats report `false` for now. The app uses it to ask
  before an extract starts (see 5).
- **Split sets.** Opening any part opens the set from its first part (unrar's `as_first_part`). When a part is missing,
  the error names the missing file.
- **Never in logs.** Password values are redacted from any request logging, and never appear in an error message, the
  journal or progress text.

### 4. Plugin SDK 1.1: create options

- Manifest: an `archiveFormats` entry may carry `createOptions`, an array of option declarations. The fields are
  `key`, `type`, `label`, and optional `description`, `default`, `options` (enum only), `min`, `max` (integer only)
  and `dependsOn`. `key` follows the settings key grammar. `type` is one of `boolean`, `integer`, `enum` or
  `password`. `dependsOn` names another option of the same format: the control is disabled until that option is on,
  or not empty for a password. The manifest reader validates these with the same error style as `settings`.
- `register_archive_engine` carries each format's `createOptions` to the service, and `get_archive_capabilities`
  returns them on that format's row. The dialog reads them from capabilities, which it already uses for formats.
- `create_archive` gains `options?: { [key]: boolean | number | string }`. The service checks the values against the
  declared options (types, enum members, `min` and `max`, `dependsOn`) and passes them to the engine as
  `archive.create`'s new `options` param. A built-in format, or an engine format with no declared options, accepts
  only an absent or empty `options`.
- Version: plugin SDK `1.0.0` → `1.1.0`, in `PluginSdk.Version` and `PLUGIN_SDK_VERSION`. 1.0 plugins keep loading.
  A plugin built against 1.1 fails to load in a 1.0 host, as the SDK rules already say. Update `docs/PLUGINS.md`, its
  doc test (`scripts/tests/plugins-doc.test.mjs`), the IPC schema in `ipc/schema/v1` with regenerated bindings
  (`scripts/generate-ipc-bindings.mjs`, `scripts/check-ipc-schema.mjs`), and the plugin template where it states
  the SDK version.

### 5. App

- **Create archive dialog** (`CreateArchiveDialog`): below Format, it draws the picked format's `createOptions`. A
  boolean is a check box, an integer a number box, an enum a drop-down, and a password a password box with a Confirm
  box beside it. **Create** is disabled while the two password boxes differ. `dependsOn` disables a control. The
  section is hidden for formats with no options. Values start at their defaults each time the dialog opens. The
  options are passed through `CreateArchiveTrackedAsync` to `create_archive`.
- **Password prompt**: a small dialog that says "*Photos.rar* is password protected", with a password box and Open and
  Cancel buttons. After a wrong password it says the password was wrong and asks again. Cancel stops the action and
  says nothing more.
- **When it asks**: only on something the user did. That means browsing into the archive, opening or previewing a
  file from it on request, extracting it, copying or moving out of it, and Properties on an entry. After a correct
  password, the action runs again.
  - Browsing into an archive with hidden names fails to list with `"required"`, so the prompt comes from that error.
    An archive with visible names lists without a password. There, the app prompts when the listing has any
    `encrypted` entry, so browsing in asks as the user approved.
  - Cancel at the browse prompt leaves you where you were. You don't enter the archive. Extraction asks **before** it starts. The app uses the listing's `encrypted` flag
  to see whether any entry is protected, so a RAR5 extract never stops halfway for a password. Background work (thumbnails, the preview pane's automatic load, lazy columns, search, folder sizes) never
  prompts. It shows nothing for those entries until a password is set.
- **When it forgets**: when no pane or tab shows a path inside that archive and no running operation reads from it,
  the app calls `forget_archive_password`.
- `ArchivePaths` and the dialog's built-in format list stay as they are. RAR appears in the dialog only when
  capabilities report it can be created.

### 6. Packaging

- Add `com.sumafile.rar` to the bundled plugin lists in `scripts/lib/plugin-staging.ps1`, to the cargo build in
  `scripts/build-plugins.ps1`, and to `bundled.json` generation. Update the packaging checks
  (`scripts/check-winui-packaging.mjs` and the staging and smoke scripts that list each bundled plugin's files).

### 7. Tests (what must be proven)

- **Round trip with the independent reader.** The plugin's tests create archives with each option, and with
  combinations (password with hidden names, password with split parts, solid with recovery, Store and Best). The core's
  built-in RAR code (unrar) then reads every one back, with the password where one was set, and compares names, sizes,
  modification times and contents with the sources.
- Engine rule: a `.rar` registration keeps creation only. Listing and extraction stay built-in with an engine
  registered. Creation falls back to the "needs the RAR plugin" error after unregistering.
- Private-folder create: a single file and a split set land in place. An existing `Photos.part2.rar` makes the create
  fail and leaves nothing new. Stop and engine failure leave nothing. A stray file in the folder is not moved. The
  7-Zip plugin still creates.
- Password store: required, wrong and correct passwords for protected entries, for hidden names, for split sets keyed
  by any part, and for a RAR 4 archive. Forget clears it. Values never appear in error text.
- Contents off disk: after thumbnails of a protected archive's entries were made, the disk thumbnail cache holds none
  of them; the saved tabs and recent places hold no path inside it; an unprotected archive is cached and saved as
  today.
- Options validation in the service: unknown key, wrong type, out of range, enum not in the list, `dependsOn` not met,
  and options sent to ZIP.
- Manifest reader: valid and invalid `createOptions`.
- C# logic behind the dialog and the prompt flow. That covers which controls appear, Create disabled on mismatched
  passwords, and when to prompt, retry and forget. It must be testable without WinUI so it runs on Linux.

## Calls I made (the user can overrule)

- RAR5 only. Defaults are Compression Normal, Solid on, Recovery off at 3 %, one file, and no password.
- The options are declared by the plugin and drawn by the dialog. RAR knowledge stays out of the app, and 7-Zip can
  later add passwords the same way.
- The password goes to the service once and is held there, instead of travelling with every call. Every RAR read
  path then picks it up without each IPC method changing.
- Links inside selected folders are skipped, as ZIP creation does.
- Part numbers are zero-padded to the predicted part count.
- Compression uses at most the number of cores minus one.
- Opening a later part opens the whole set.
- Defaults reset each time the dialog opens. Nothing about the options is remembered.
- A crash during create may leave a hidden temporary folder, as it leaves a hidden temporary file today. No startup
  cleanup for it this round.

## Left to the builder

- The plugin's internal module layout, modelled on `plugins/sevenzip/backend`.
- How core RAR functions get at the store (a module in `simplefile-core` that the service fills, or a parameter
  threaded through). The rule is that no RAR read path may ignore it.
- How the app finds the user action that hit a password error, and re-runs it.
- Wording of messages, following the existing archive messages' style.
- Whether rars needs a minimum part size, and the message when the chosen size is below it.
