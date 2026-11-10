#requires -Version 5.1
# The WinUI MSI's install, major upgrade and uninstall with a user plugin in the install folder. It builds v1 and v2
# MSIs from the release payload (dist\winui\payload and its harvest dist\winui\wix\harvested.wxs, so run
# 'npm run release:build' first), installs v1 per user into a disposable folder deliberately not named SumaFile-WinUI,
# launches it, seeds a user plugin, upgrades to v2 without INSTALLDIR (the remembered folder must be reused and the
# user plugin files kept), then uninstalls and requires the folder gone.
#
# The two MSIs are built with Product.wxs's LifecycleSmoke identity (its own UpgradeCode, registry key, Start menu
# folder and component GUIDs; the purge and the remembered folder are the same XML), so the smoke never upgrades
# away, retargets or removes a SumaFile MSI the user has installed: 1.0.4 shares the release UpgradeCode. It refuses
# while SumaFile runs, a per-user install exists or a lifecycle product is registered, and it fails when the machine
# entries, the default install folder or the pre-release plugin locations changed.
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$payloadDir = Join-Path $root "dist\winui\payload"
$harvestedWxs = Join-Path $root "dist\winui\wix\harvested.wxs"
$iconPath = Join-Path $root "packaging\winui\icon.ico"
$releaseUpgradeCode = "{8F3C1B6A-4D2E-4E91-9C77-2A6B5E1D0C44}"
# Product.wxs's LifecycleSmoke ProductRegistryKey and StartMenuFolderName.
$lifecycleRegistryKey = "HKCU:\Software\SumaFile\WinUI-LifecycleSmoke"
$lifecycleStartMenuFolder = Join-Path ([Environment]::GetFolderPath('Programs')) "SumaFile Lifecycle Smoke"
$timeoutSeconds = 25
$defaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\SumaFile-WinUI"

. (Join-Path $PSScriptRoot "lib\plugin-staging.ps1")
. (Join-Path $PSScriptRoot "lib\wix-msi.ps1")

function Get-RememberedInstallDir {
    $values = Get-ItemProperty -LiteralPath $lifecycleRegistryKey -ErrorAction SilentlyContinue
    if ($values -and $values.PSObject.Properties['InstallDir']) { return [string]$values.InstallDir }
    return $null
}

function Invoke-Msiexec([string]$Arguments, [string]$Label) {
    Write-Host "msiexec $Arguments"
    $run = Start-Process -FilePath "msiexec.exe" -ArgumentList $Arguments -Wait -PassThru
    if ($run.ExitCode -ne 0) { throw "$Label failed with msiexec exit code $($run.ExitCode); see the log named above." }
}

function Wait-PathsExist([string[]]$Paths, [int]$Seconds, [string]$Because) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $missing = @($Paths | Where-Object { -not (Test-Path -LiteralPath $_) })
        if ($missing.Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "$Because Missing after $Seconds s: $($missing -join ', ')"
}

# 1. Inputs and guards.
$payloadExe = Join-Path $payloadDir "SumaFile.exe"
if (-not (Test-Path -LiteralPath $payloadExe -PathType Leaf)) { throw "No WinUI payload at $payloadExe. Run 'npm run release:build' first." }
if (-not (Test-Path -LiteralPath $harvestedWxs -PathType Leaf)) { throw "No WiX harvest at $harvestedWxs. Run 'npm run release:build' first." }
$wixBin = Resolve-WixBin
if (-not $wixBin) { throw "WiX v3.14.1 or later (candle, light, heat and WixUtilExtension.dll) was not found." }

Assert-NoSumaFileProcess
$existing = @(Get-SumaFileUninstallEntries -Scope CurrentUser)
if ($existing.Count -gt 0) {
    throw "SumaFile is already installed for this user at '$($existing[0].InstallLocation)' ($($existing[0].Key)). Uninstall it before running this smoke test."
}
if (Test-Path -LiteralPath $lifecycleRegistryKey) {
    throw "$lifecycleRegistryKey exists, left by an earlier lifecycle run; uninstall that product or remove the key first."
}
if (Test-Path -LiteralPath $lifecycleStartMenuFolder) {
    throw "$lifecycleStartMenuFolder exists, left by an earlier lifecycle run; remove it first."
}
$machineBefore = @(Get-SumaFileUninstallEntries -Scope Machine)
$defaultBefore = Get-FolderSnapshot -Path $defaultInstallDir
$legacy = Get-LegacyPluginLocationSnapshot

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-msi-lifecycle-" + [System.Guid]::NewGuid().ToString("N"))
$wixDir = Join-Path $smokeRoot "wix"
$installDir = Join-Path $smokeRoot "SumaFile Custom"
$productCodes = @()
$process = $null
$passed = $false

try {
    # 2. v1 at the payload's numeric version and v2 with the third field + 1, both with the lifecycle identity.
    $versionInfo = (Get-Item -LiteralPath $payloadExe).VersionInfo
    $v1Version = "{0}.{1}.{2}" -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
    $v2Version = "{0}.{1}.{2}" -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, ($versionInfo.FileBuildPart + 1)
    $v1Msi = Join-Path $wixDir "v1.msi"
    $v2Msi = Join-Path $wixDir "v2.msi"
    New-WinUIMsi -WixBin $wixBin -PayloadDir $payloadDir -HarvestedWxs $harvestedWxs -IconPath $iconPath `
        -ProductVersion $v1Version -WorkDir (Join-Path $wixDir "obj1") -MsiPath $v1Msi -LifecycleSmoke
    New-WinUIMsi -WixBin $wixBin -PayloadDir $payloadDir -HarvestedWxs $harvestedWxs -IconPath $iconPath `
        -ProductVersion $v2Version -WorkDir (Join-Path $wixDir "obj2") -MsiPath $v2Msi -LifecycleSmoke

    $v1Code = Get-MsiPropertyValue -MsiPath $v1Msi -Name "ProductCode"
    $v2Code = Get-MsiPropertyValue -MsiPath $v2Msi -Name "ProductCode"
    $upgradeCode = Get-MsiPropertyValue -MsiPath $v1Msi -Name "UpgradeCode"
    if (-not $v1Code -or -not $v2Code -or $v1Code -eq $v2Code) { throw "The lifecycle MSIs have ProductCodes '$v1Code' and '$v2Code'." }
    if ($upgradeCode -eq $releaseUpgradeCode -or $upgradeCode -ne (Get-MsiPropertyValue -MsiPath $v2Msi -Name "UpgradeCode")) {
        throw "The lifecycle MSIs must share their own UpgradeCode, never the release one; v1 has '$upgradeCode'."
    }
    foreach ($code in @($v1Code, $v2Code)) {
        if (Test-MsiProductRegistered -ProductCode $code) { throw "Lifecycle product $code is already registered." }
    }
    $related = @(Get-MsiRelatedProductCodes -UpgradeCode $upgradeCode)
    if ($related.Count -gt 0) {
        throw "A lifecycle product from an earlier run is still registered: $($related -join ', '). Uninstall it with msiexec /x first."
    }
    $productCodes = @($v1Code, $v2Code)
    Write-Host "Built lifecycle MSIs v1 $v1Version ($v1Code) and v2 $v2Version ($v2Code), UpgradeCode $upgradeCode."

    # 3. Install v1 into a custom folder name. No trailing backslash before the closing quote.
    Invoke-Msiexec "/i `"$v1Msi`" /qn INSTALLDIR=`"$installDir`" /l*v `"$(Join-Path $smokeRoot 'v1.log')`"" "The v1 install"
    if (-not (Test-MsiProductRegistered -ProductCode $v1Code)) { throw "v1 ($v1Code) is not registered after its install." }
    $exePath = Join-Path $installDir "SumaFile.exe"
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw "v1 did not install SumaFile.exe into $installDir." }
    $remembered = Get-RememberedInstallDir
    if ($remembered -ne "$installDir\") {
        throw "After the v1 install $lifecycleRegistryKey\InstallDir is '$remembered'; expected '$installDir\'."
    }

    # 4. Bundled plugins, a launch whose plugin files land in the install folder, then a user plugin.
    Assert-BundledPlugins -Root (Join-Path $installDir "plugins")
    $process = Start-InstalledSumaFile -Exe $exePath -SmokeRoot $smokeRoot
    $windowProcess = Wait-SumaFileWindow -Process $process -TimeoutSeconds $timeoutSeconds
    Write-Host "Installed SumaFile shows its window: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)'."
    $sevenZipData = Join-Path (Join-Path $installDir "plugins-data") "com.sumafile.sevenzip"
    Wait-PathsExist @(
        (Join-Path $installDir "plugins.log"),
        $sevenZipData,
        (Join-Path $sevenZipData "tmp")
    ) $timeoutSeconds "The installed app's plugin log, data folder and backend temp folder must be in its install folder."
    $stopped = @(Stop-ProcessesUnderFolder -Path $installDir -MainProcess $process)
    $process = $null
    Write-Host "Closed the installed app; stopped by path: $(if ($stopped.Count) { $stopped -join ', ' } else { 'nothing left' })."
    New-UserPluginSeed -InstallDir $installDir
    $before = Get-AppOwnedPluginState -InstallDir $installDir
    Write-Host "Seeded a user plugin; app-owned plugin state: $(@($before.Keys).Count) entries."

    # 5. Major upgrade to v2 without INSTALLDIR: AppSearch reads the remembered folder.
    $remembered = Get-RememberedInstallDir
    if ($remembered -ne "$installDir\") {
        throw "Before the upgrade $lifecycleRegistryKey\InstallDir is '$remembered'; expected '$installDir\'."
    }
    Invoke-Msiexec "/i `"$v2Msi`" /qn /l*v `"$(Join-Path $smokeRoot 'v2.log')`"" "The v2 upgrade"
    if (Test-MsiProductRegistered -ProductCode $v1Code) { throw "v1 ($v1Code) is still registered after the upgrade." }
    if (-not (Test-MsiProductRegistered -ProductCode $v2Code)) { throw "v2 ($v2Code) is not registered after the upgrade." }
    Assert-FolderSnapshotUnchanged -Path $defaultInstallDir -Before $defaultBefore -Label "The upgrade"
    Assert-SumaFileUninstallEntriesUnchanged -Scope Machine -Before $machineBefore -IgnoreKeyNames $productCodes -Label "The upgrade"
    Assert-AppOwnedPluginStateUnchanged -InstallDir $installDir -Before $before
    Assert-BundledPlugins -Root (Join-Path $installDir "plugins")
    $remembered = Get-RememberedInstallDir
    if ($remembered -ne "$installDir\") { throw "After the upgrade $lifecycleRegistryKey\InstallDir is '$remembered'; expected '$installDir\'." }
    Write-Host "The upgrade went to $installDir and kept the user plugin, its data, the approvals and the plugin log."

    # 6. Uninstall: the four app-owned items are purged, then Windows Installer removes the empty folder.
    Invoke-Msiexec "/x $v2Code /qn /l*v `"$(Join-Path $smokeRoot 'x.log')`"" "The uninstall"
    Wait-FolderGone -Path $installDir -TimeoutSeconds 60
    foreach ($code in $productCodes) {
        if (Test-MsiProductRegistered -ProductCode $code) { throw "Lifecycle product $code is still registered after the uninstall." }
    }
    if (Test-Path -LiteralPath $lifecycleRegistryKey) { throw "$lifecycleRegistryKey is still there after the uninstall." }
    if (Test-Path -LiteralPath $lifecycleStartMenuFolder) { throw "$lifecycleStartMenuFolder is still there after the uninstall." }
    $leftUser = @(Get-SumaFileUninstallEntries -Scope CurrentUser)
    if ($leftUser.Count -gt 0) { throw "A per-user SumaFile uninstall entry is left: $($leftUser[0].Key)." }
    Assert-SumaFileUninstallEntriesUnchanged -Scope Machine -Before $machineBefore -Label "The MSI lifecycle smoke"
    Assert-FolderSnapshotUnchanged -Path $defaultInstallDir -Before $defaultBefore -Label "The MSI lifecycle smoke"
    Assert-LegacyPluginLocationsUnchanged -Before $legacy

    # 7.
    Write-Host "WinUI MSI lifecycle smoke passed: the upgrade kept the user plugin and its data; uninstall left nothing in $installDir."
    $passed = $true
}
finally {
    # 8. Stop what runs from the folder, remove any lifecycle product still registered, and drop the temp root.
    $stopped = @(Stop-ProcessesUnderFolder -Path $installDir -MainProcess $process)
    if ($stopped.Count -gt 0) { Write-Host "Stopped by path under ${installDir}: $($stopped -join ', ')." }
    foreach ($code in $productCodes) {
        if (Test-MsiProductRegistered -ProductCode $code) {
            Write-Host "Removing lifecycle product $code."
            $cleanup = Start-Process -FilePath "msiexec.exe" -ArgumentList "/x $code /qn" -Wait -PassThru
            if ($cleanup.ExitCode -ne 0) { Write-Warning "msiexec /x $code exited with $($cleanup.ExitCode)." }
        }
    }
    if ($passed) {
        Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
    } elseif (Test-Path -LiteralPath $smokeRoot) {
        Write-Host "WinUI MSI lifecycle smoke did not pass. Evidence (MSI logs, app data): $smokeRoot"
    }
}
