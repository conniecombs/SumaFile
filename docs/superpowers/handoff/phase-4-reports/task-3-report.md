# Phase 4 Task 3 report: installers keep user plugin files on upgrade and remove them on uninstall

Status: DONE_WITH_CONCERNS (the concerns are listed at the end). Nothing is committed; all work is in the working tree.

This task serves the user's requirement "I do not want plugins stored outside of the program installation folder". Plugins, their data, the approvals file and `plugins.log` now live only in the install folder. Both installers keep them across a reinstall or upgrade and remove them on uninstall, and nothing is moved anywhere else.

## What was implemented, per file

### `packaging/winui/sumafile-winui.nsi`
- `InstallDirRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SumaFile-WinUI" "InstallLocation"`. A reinstall or upgrade without `/D=` goes to the folder the last install recorded.
- Comments:
  - In `.onInit`, the remote profiles now live in `$INSTDIR\plugins-data\com.sumafile.remote`, which a reinstall keeps; passwords stay in Credential Manager.
  - In the Install section, only the bundled plugin folders are replaced. The user's plugins, `plugins-data`, `plugin-approvals.json` and `plugins.log` survive a reinstall or upgrade, and uninstall removes them with `$INSTDIR`. The phrase "never touched" is gone.
- The Install section still removes only `$INSTDIR\plugins\com.sumafile.{sevenzip,remote}`, never `plugins` or `plugins-data` as a whole.
- In Uninstall, after `RMDir /r "$INSTDIR"`: `${If} ${FileExists} "$INSTDIR\*.*"`, then `Sleep 1500` and `RMDir /r "$INSTDIR"` once more.

### `packaging/winui/Product.wxs`
- `xmlns:util="http://schemas.microsoft.com/wix/UtilExtension"`.
- A preprocessor identity block. Release values: UpgradeCode `8F3C1B6A-…` (unchanged), registry key `Software\SumaFile\WinUI`, Start menu folder `SumaFile`, the existing shortcut component GUID, and a new fixed `InstallDirMemory` GUID `721B9E22-8302-4EFE-9BDD-F746571FEAE5`. `-dLifecycleSmoke=1` swaps in the lifecycle smoke's own values (see Deviations 1).
- `<Property Id="INSTALLDIR" Secure="yes">` and `<Property Id="SUMAFILE_PURGE_SOURCE" Secure="yes">`, each with `<RegistrySearch Root="HKCU" Key="$(var.ProductRegistryKey)" Name="InstallDir" Type="raw" />`.
- The purge properties, all `Sequence="execute"` and before `WixRemoveFoldersEx`, which runs at 799, before `CostInitialize` at 800. The built MSI's sequence is:
  - `SetSumaFilePurgeDataDefault` 793 and `SetSumaFilePurgeData` 794;
  - `SetSumaFilePurgeFilesDefault` 795 and `SetSumaFilePurgeFiles` 796;
  - `SetSumaFilePurgePluginsDefault` 797 and `SetSumaFilePurgePlugins` 798.

  Each conditional action carries `REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE AND SUMAFILE_PURGE_SOURCE`, and each default points under `[TempFolder]SumaFile-no-purge\`, a folder that is never created (see Deviations 2).
- Component `InstallDirMemory` (Directory `INSTALLDIR`), referenced from `MainFeature`, holds:
  - `RegistryValue … Name="InstallDir" Value="[INSTALLDIR]" KeyPath="yes"`;
  - `util:RemoveFolderEx On="uninstall"` on `SUMAFILE_PURGE_PLUGINS` and on `SUMAFILE_PURGE_DATA`;
  - `RemoveFile` `PurgePluginLog` (`plugins.log*`) and `PurgePluginApprovals` (`plugin-approvals.json*`) on `SUMAFILE_PURGE_FILES`;
  - `RemoveFolder Id="PurgeEmptyInstallDir" Property="SUMAFILE_PURGE_FILES" On="uninstall"` (see Deviations 3).

### `scripts/lib/wix-msi.ps1` (new)
- `Resolve-WixBin` returns the folder that holds `candle.exe`, `light.exe`, `heat.exe` and `WixUtilExtension.dll`, or `$null`. It searches PATH, the three paths the build listed before, then Program Files.
- `New-WinUIMsi -WixBin -PayloadDir -HarvestedWxs -IconPath -ProductVersion -WorkDir -MsiPath [-LifecycleSmoke]` runs candle and light with `-ext <WixBin>\WixUtilExtension.dll` and the existing `-sice:ICE03/38/64/91`. `-LifecycleSmoke` adds `-dLifecycleSmoke=1`.
- Helpers for the lifecycle smoke, late-bound on `WindowsInstaller.Installer` so they behave the same in Windows PowerShell and pwsh:
  - `Get-MsiPropertyValue` releases the database before it returns, so msiexec can open the file;
  - `Test-MsiProductRegistered`;
  - `Get-MsiRelatedProductCodes`.

### `scripts/build-winui-release.ps1`
- It dot-sources `lib\wix-msi.ps1`. The WiX block uses `Resolve-WixBin`, runs heat from that folder and calls `New-WinUIMsi`. The temp MSI path, the replace step and the cleanup stay as before.
- The skip message now names `WixUtilExtension.dll`.

### `scripts/lib/plugin-staging.ps1`
- `Get-FolderSnapshot` and `Assert-FolderSnapshotUnchanged`, as specified. `Get-LegacyPluginLocationSnapshot` is rebuilt on `Get-FolderSnapshot`, and Case 9 still passes.
- `New-UserPluginSeed` writes `plugins\com.example.smoke-user\{plugin.json,payload.txt}` and `plugins-data\com.example.smoke-user\data.txt`. It writes `plugin-approvals.json` as `{}` only when that file is absent.
- `Wait-FolderGone` throws `<path> still exists <n> s after uninstall: <first 10 entries>`.
- `Get-SumaFileUninstallEntries -Scope CurrentUser|Machine` returns `Key`, `KeyName`, `DisplayVersion`, `InstallLocation` and the raw `Values`. Machine scope only reads.
- `Assert-SumaFileUninstallEntriesUnchanged -Scope -Before [-IgnoreKeyNames] -Label` (addition, see Deviations 4).
- `Assert-NoSumaFileProcess` throws `Close SumaFile first: <name> (<pid>) <path> is running; the installer ends it by name.` (see Deviations 5).
- `Start-InstalledSumaFile -Exe -SmokeRoot` refuses an exe outside the smoke root; a sibling folder that only shares a name prefix counts as outside. It starts the app through `ProcessStartInfo` with these environment changes:
  - the four `SUMAFILE_PLUGIN_*` overrides removed;
  - `SIMPLEFILE_METADATA_DB` and `SIMPLEFILE_APP_DATA_DIR` under `<SmokeRoot>\app-data`;
  - `GIT_CONFIG_GLOBAL=<SmokeRoot>\gitconfig`, an empty file, and `GIT_CONFIG_NOSYSTEM=1`.
- `Wait-SumaFileWindow` and `Stop-ProcessesUnderFolder`, shared smoke plumbing. The second stops processes only by image path under a folder.

### `scripts/smoke-winui-nsis.ps1` (rewritten flow)
It follows plan steps 1-8.
- **Guards:** `Assert-NoSumaFileProcess`; refusal when an HKCU entry exists; refusal when an NSIS shortcut already exists (addition); then the machine-entry snapshot, `Get-FolderSnapshot` of the default folder and `Get-LegacyPluginLocationSnapshot`.
- **Install:** `/S /D=%TEMP%\sumafile-nsis-smoke-<guid>\SumaFile-WinUI`. `Find-WinUIExecutable` accepts only the HKCU entry's `InstallLocation` and only when it equals the smoke's folder; the default-path fallback is gone.
- **Launch:** through `Start-InstalledSumaFile`. Within 25 s it requires `plugins.log`, `plugins-data\com.sumafile.sevenzip` and its `tmp` in the install folder.
- **Close and seed:** it stops the app by path, runs `New-UserPluginSeed` and takes the state.
- **Reinstall** with `/S` alone, after checking `InstallLocation`. Then:
  - the default folder is unchanged (it fails fast if `InstallDirRegKey` regressed);
  - the machine entries are unchanged;
  - `InstallLocation` is the same;
  - the app-owned state is unchanged and `Assert-BundledPlugins` passes.
- **Uninstall:** `Wait-FolderGone 60`, then the entry must be gone, then the end assertions.
- **`finally`:** it stops processes by path, and uninstalls only an entry whose `InstallLocation` is the smoke's own folder; any other entry gets a warning and is never uninstalled. It keeps the smoke root as evidence on failure.

### `scripts/smoke-winui-msi-lifecycle.ps1` (new)
It follows plan steps 1-8.
- **Inputs:** `dist\winui\payload\SumaFile.exe`, `dist\winui\wix\harvested.wxs` and `Resolve-WixBin`.
- **Guards:** the plan's guards, plus refusals for a left-over lifecycle registry key, Start menu folder or related product.
- **Build:** v1 at the payload exe's `Major.Minor.Build` and v2 with Build + 1, both with `-LifecycleSmoke`. It reads both ProductCodes and the UpgradeCode, and requires that UpgradeCode is not the release one.
- **v1 install:** `msiexec /i v1.msi /qn INSTALLDIR="<root>\SumaFile Custom" /l*v v1.log`. It then requires the exe and `HKCU\Software\SumaFile\WinUI-LifecycleSmoke\InstallDir` = `<root>\SumaFile Custom\`.
- **Launch, close and seed** as in the NSIS smoke.
- **Upgrade:** `msiexec /i v2.msi /qn /l*v v2.log`. Then:
  - v1 is unregistered and v2 registered;
  - the default folder is unchanged;
  - the machine entries are unchanged, ignoring the two lifecycle ProductCodes;
  - the app-owned state is unchanged, the bundled plugins pass, and the remembered folder is the same.
- **Uninstall:** `msiexec /x <v2> /qn /l*v x.log`, then `Wait-FolderGone 60`. It requires no lifecycle product, key or Start menu folder and no HKCU entry left, then runs the end assertions.
- **`finally`:** it stops processes by path, runs `msiexec /x` on any lifecycle ProductCode still registered, and removes the temp root only on success.

### `scripts/smoke-winui-upgrade.ps1` (static only, not run)
- Its header describes the flow.
- It installs the previous release with `/S /D=<%TEMP%\sumafile-upgrade-smoke-<guid>\SumaFile-WinUI>`. `Find-WinUIExecutable` accepts only that folder; the fallback is removed.
- It runs the same guards as the NSIS smoke and launches through `Start-InstalledSumaFile`. `Stop-SmokeProcesses` now stops by path.
- After the previous app's launch it seeds a user plugin and takes the state, then upgrades with `/S` alone. After the upgrade it asserts the default folder, the machine entries and the app-owned state unchanged.
- It uninstalls with `Wait-FolderGone 60` and the end assertions. The cleanup uninstalls only the smoke's own folder.
- The `%APPDATA%` sentinel check is unchanged.

### `scripts/check-winui-packaging.mjs`
- **`nsisSnippets`:** `InstallDirRegKey HKCU`. A new `forbiddenNsisSnippets` list holds `never touched`, `RMDir /r "$INSTDIR\plugins"` and `RMDir /r "$INSTDIR\plugins-data"`.
- **`wxsSnippets`:** all nine the plan names, plus the release `UpgradeCode = "8F3C1B6A-…"` and the `PurgeEmptyInstallDir` `RemoveFolder` row.
- **`RemoveFolderEx` check:** it fails on `Property="SUMAFILE_PURGE_SOURCE"` with the plan's meaning. It is also stricter: it fails on any `RemoveFolderEx` whose property is not `SUMAFILE_PURGE_PLUGINS` or `SUMAFILE_PURGE_DATA`.
- **`buildSnippets`:** `WixUtilExtension` and `New-WinUIMsi`. The four `-sice` snippets moved to a new `wixMsiSnippets` list over `scripts/lib/wix-msi.ps1`, because the flags now live there (see Deviations 6).
- **`requiredFiles`, `npmSnippets` and `workflowSnippets`:** the two new files, `"smoke:winui-msi-lifecycle"`, and that script in all three workflows.
- **`installerSmokeSnippets`:** it requires the four helper names in the three installer smokes. It forbids `Programs\SumaFile-WinUI\SumaFile.exe` there, ignoring case, with `\\` and `/` normalized to `\`.

### `scripts/test-plugin-staging.ps1`: Case 10
- **The seed:** the files are exactly `plugin-approvals.json` plus the three seeded files, and the approvals file is `{}`. An existing approvals file is kept.
- **`Wait-FolderGone`:** it returns in under 2 s for a missing folder. For a folder that stays it throws within 10 s, naming the path, `1 s after uninstall` and an entry.
- **`Get-FolderSnapshot`:** it is empty for a missing folder. Its keys are `sub|sub\a.txt`, and a length change, a time change and an added file are each named.
- **`Start-InstalledSumaFile`:** it refuses an exe outside the smoke root, and one in a sibling folder that only shares the name prefix. Neither path exists, so nothing could start.

### `package.json` and the workflows
- `package.json`: `"smoke:winui-msi-lifecycle": "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/smoke-winui-msi-lifecycle.ps1"`.
- The step `Smoke WinUI MSI install, upgrade, and uninstall` (`npm run smoke:winui-msi-lifecycle`):
  - `installer-smoke.yml`: right after the NSIS install step;
  - `release-build.yml`: after `smoke:winui-upgrade`, under the same `if: inputs.full_installer_smoke`;
  - `release.yml`: after `smoke:winui-upgrade`.

### Docs
- **Spec §11.4:**
  - The phase 2 NSIS bullet no longer promises an untouched `%LOCALAPPDATA%` user root.
  - The phase 3 bullet places the remote profiles in `$INSTDIR\plugins-data\com.sumafile.remote`.
  - A new "Phase 4 installers" part covers NSIS, WiX (the remembered folder, the four-item purge, the empty-folder `RemoveFolder` and the no-purge defaults), `wix-msi.ps1`, both smokes, the lifecycle identity, the guards and the upgrade smoke's pending CI proof.
- **Spec §13:** an installer lifecycle bullet.
- **Review notes, "Phase 4 rulings":** an Installers entry. It records the local proofs, the NSIS upgrade from the previous release as pending until the first CI run of `smoke:winui-upgrade`, and the lifecycle identity deviation for review.

## TDD evidence

RED: the checks and Case 10 were written first.
```
> npm run check:winui-packaging
WinUI packaging check failed: packaging/winui/sumafile-winui.nsi must include InstallDirRegKey HKCU.
WinUI packaging check failed: packaging/winui/sumafile-winui.nsi must not contain never touched; ...
WinUI packaging check failed: packaging/winui/Product.wxs must include xmlns:util="http://schemas.microsoft.com/wix/UtilExtension".
... (every new wxs/build/wix-msi/npm/workflow/installer-smoke snippet, plus)
WinUI packaging check failed: scripts/smoke-winui-nsis.ps1 must not launch Programs\SumaFile-WinUI\SumaFile.exe; installer smokes run only the copy they installed.
WinUI packaging check failed: missing scripts/smoke-winui-msi-lifecycle.ps1
WinUI packaging check failed: scripts/smoke-winui-upgrade.ps1 must not launch Programs\SumaFile-WinUI\SumaFile.exe; ...

> powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1
The term 'New-UserPluginSeed' is not recognized as the name of a cmdlet, function, script file, or operable program.
```

GREEN:
```
> npm run check:winui-packaging
WinUI packaging surface is wired.
> powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1
Plugin staging tests passed.
```

Each new forbidden check was probed: a temporary edit was made and the check run, and every file was restored and the check passed again afterwards. Each probe was reported:
- `RMDir /r "$INSTDIR\plugins"` and `RMDir /r "$INSTDIR\plugins-data"` in the NSIS script;
- `RemoveFolderEx Property="SUMAFILE_PURGE_SOURCE"` (the plan's message) and `Property="INSTALLDIR"` (the stricter rule);
- `$env:LOCALAPPDATA/Programs/SumaFile-WinUI/SumaFile.exe` in the upgrade smoke, caught through the forward-slash normalization.

All seven edited or new PowerShell scripts parse with 0 errors.

### WiX behaviour verified before the full smokes
A scratch WiX-only run built lifecycle v1/v2 MSIs from the existing payload, installed v1 into `<temp>\SumaFile Custom`, seeded the four item kinds plus an unrelated file, upgraded and uninstalled, without launching the app. It showed two things:
- The first Product.wxs left `SUMAFILE_PURGE_PLUGINS` and `SUMAFILE_PURGE_DATA` unset outside an uninstall, as the plan says. WiX 3.14 does not skip those: `WixRemoveFoldersEx: Error 0x80070057: Missing folder property: SUMAFILE_PURGE_PLUGINS`, `returned actual error code 1603 but will be translated to success due to continue marking`. That led to Deviations 2. Afterwards the v1 and v2 logs read `Path is a junction; skipping: R:\Temp\SumaFile-no-purge\plugins\` with `Return value 1`, and no error.
- The upgrade kept all 11 app-owned entries. The uninstall purged them and kept only the unrelated file, so the folder stayed: the purge never takes the user's other files.

## Full-suite results
- `npm run check:winui-packaging`: `WinUI packaging surface is wired.`
- `npm run check:workflows`: `Workflow triggers, job gates, permissions, actions and packaging contracts are valid.`
- `npm run test:maintenance`: 66 tests, 66 pass.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-plugin-staging.ps1`: `Plugin staging tests passed.`
- `npm run check`: exit 0 (86 domain methods; `WinUI packaging surface is wired.`).
- `npm run release:build`: exit 0 twice. The second run used the final Product.wxs. Each run included:
  - `check:winui`, `Passed! - Failed: 0, Passed: 1159`;
  - the NSIS and MSI builds with `WixUtilExtension`;
  - `Assert-Payload`;
  - `WinUI startup smoke passed`, the file-ops smoke (`Payload 7-Zip plugin smoke passed.`, `Payload remote plugin smoke passed.`) and `WinUI MSI artifact smoke passed`.
- `npm run smoke:winui-installer`, twice: `WinUI NSIS install smoke passed: the reinstall kept the user plugin and its data; uninstall left nothing in R:\Temp\sumafile-nsis-smoke-<guid>\SumaFile-WinUI.` The run showed:
  - `plugins.log` and `plugins-data\com.sumafile.sevenzip\tmp` in the install folder;
  - the reinstall without `/D=` back in the same folder;
  - an unchanged app-owned state of 13 entries;
  - the folder gone after the uninstall.
- `npm run smoke:winui-msi-lifecycle`:
  - The first run failed: `…\SumaFile Custom still exists 60 s after uninstall: (empty)` (Deviations 3).
  - After the fix it passed twice, once against the second `release:build`: `WinUI MSI lifecycle smoke passed: the upgrade kept the user plugin and its data; uninstall left nothing in R:\Temp\sumafile-msi-lifecycle-<guid>\SumaFile Custom.`
- **Afterwards, the machine state is clean:**
  - no HKCU "SumaFile" uninstall entry, and no product under the lifecycle UpgradeCode;
  - the only related product of the release UpgradeCode is still 1.0.4 `{B5350282-…}`, and the only machine entry is its own;
  - `HKCU\Software\SumaFile` holds only 1.0.4's `WinUI\StartMenuShortcut`;
  - no smoke folder is left under `R:\Temp`, and no `SumaFile-no-purge` folder exists.

  Both smokes also asserted the default folder `%LOCALAPPDATA%\Programs\SumaFile-WinUI` and the pre-release plugin locations unchanged.

No C# or Rust changed in this task.

## Deviations
1. **The lifecycle smoke's MSIs use their own identity (`-dLifecycleSmoke=1`).** This machine's SumaFile 1.0.4 is a per-user MSI (`{B5350282-…}`, `AssignmentType 0`) with the release UpgradeCode `8F3C1B6A-…`. It shares the release's `ApplicationShortcut` component `{B7E2C914-…}`, whose key path is `HKCU\Software\SumaFile\WinUI\StartMenuShortcut`. Its ARP entry is the HKLM `WOW6432Node` key the plan calls per machine. With the release identity, the plan's v1 install would have major-upgraded 1.0.4 away (`FindRelatedProducts` finds it) and retargeted its Start menu shortcut, breaking "other installs are never touched".
   - Product.wxs therefore carries a preprocessor block. The release values are unchanged. `LifecycleSmoke` swaps in:
     - UpgradeCode `187C13BB-A9BA-4FD5-A6BA-9EBC2F99B3EE`;
     - registry key `Software\SumaFile\WinUI-LifecycleSmoke`;
     - Start menu folder `SumaFile Lifecycle Smoke`;
     - component GUIDs `9CC78BB1-…` (shortcut) and `DC3A2594-…` (`InstallDirMemory`).
   - Everything else is the same XML, including the remembered folder and the purge.
   - The smoke reads the remembered folder from the lifecycle key, not from `Software\SumaFile\WinUI`.
   - It also refuses on a left-over lifecycle product, key or Start menu folder.
2. **No purge property is ever left unset.** `SUMAFILE_PURGE_PLUGINS` and `SUMAFILE_PURGE_DATA` get the same two-step pattern the plan gives `SUMAFILE_PURGE_FILES`: an unconditional default under `[TempFolder]SumaFile-no-purge\`, then the purge value under the condition. The plan assumed `RemoveFolderEx` skips an empty property, but WiX 3.14 errors instead (see the WiX evidence above).
3. **The empty install folder needs a `RemoveFolder` row.** The plan expected Windows Installer to remove the emptied install folder "as it does today". On the first full lifecycle run it did not: `SumaFile Custom still exists 60 s after uninstall: (empty)`. In `x.log` the only `FolderRemove` ops are the Start menu folder and the purge folders. MSI removes a folder only when it is empty at the moment its last file goes, and the install folder still held `plugins\` and `plugins-data\` then. Before phase 4 the app wrote nothing there.
   - The fix is `<RemoveFolder Id="PurgeEmptyInstallDir" Property="SUMAFILE_PURGE_FILES" On="uninstall" />` in `InstallDirMemory`. `RemoveFolder` deletes only an empty folder, and outside an uninstall its property points at the never-created folder.
   - The WiX-only rerun proved that a custom folder holding another file keeps that file, and so the folder.
   - The packaging check pins the exact element.
4. **`Assert-SumaFileUninstallEntriesUnchanged` (new helper), and the lifecycle smoke compares machine entries without its own ProductCodes.** Windows Installer writes a per-user MSI's ARP entry under HKLM `WOW6432Node`, and the lifecycle products appear there between install and uninstall. The final comparison, after the uninstall, is exact.
5. **The process guard also covers `SimpleFile` and `SimpleFile.App`.** The NSIS `.onInit` also kills `SimpleFile.exe`, `SimpleFile.App.exe` and `simplefile.exe` by name, so the guard refuses on those as well.
6. **Packaging check details.** The four `-sice` snippets moved from `buildSnippets` to a `wixMsiSnippets` list over `scripts/lib/wix-msi.ps1`, where the flags now live. The plan says `dist\wix\harvested.wxs`; the real harvest path is `dist\winui\wix\harvested.wxs` (`$distRoot\wix`), and the smoke uses it.
7. **Small additions:**
   - the NSIS and upgrade smokes refuse when `SumaFile.lnk` already exists in the Start menu or on the Desktop, which their install would replace and their uninstall delete;
   - the cleanup uninstalls only an entry whose `InstallLocation` is the smoke's own folder, so a regressed `InstallDirRegKey` can never make the cleanup delete another folder;
   - the smoke root is kept as evidence when a run fails;
   - `New-WinUIMsi` passes the extension as the full DLL path (`-ext <WixBin>\WixUtilExtension.dll`), which is the same extension.

## Concerns
- **The NSIS upgrade from the previous published release is unproven locally.** `smoke-winui-upgrade.ps1` was updated and checked statically only, as the plan requires; it downloads with `gh`. The review notes record that it waits for its first CI run.
- **The lifecycle identity (Deviations 1) is a deliberate departure from the plan's text.** The reviewer should confirm it. Without it, the plan's smoke would have uninstalled this machine's SumaFile 1.0.4.
- **The release MSI's `ApplicationShortcut` component** is shared with 1.0.4 and unchanged. A real upgrade from 1.0.4 to this MSI is a normal major upgrade, as before.
- **Error 1926 in the MSI logs.** Each lifecycle `msiexec` logs `Error 1926. Could not set file security for file 'R:\Config.Msi\…rbf'`. It comes from the rollback folder on drive `R:` in this environment, and every exit code was 0. It is unrelated to this change.
- **Smokes still writing to `%LOCALAPPDATA%\SumaFile`.** The installed app still writes `startup.log`, `service.log` and `startup-timing.log` there during the installer smokes. The plan accepts this: they are the app's own files, not plugin files.
