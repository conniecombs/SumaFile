param(
    [switch]$KeepInstalled
)

# Installs the NSIS setup into a disposable folder under %TEMP%, launches that copy, checks that the app's plugin files
# land in its install folder, seeds a user plugin, reinstalls without /D= (InstallDirRegKey must bring it back to the
# same folder and the user plugin files must survive), then uninstalls and requires the folder gone. It never touches
# another SumaFile install: it refuses while SumaFile runs or a per-user install exists, and it fails when the machine
# entries, the default install folder or the pre-release plugin locations changed.

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$bundleDir = Join-Path $root "dist\winui"

# Assert-BundledPlugins, the app-owned plugin state and the installer smoke helpers (guards, seed, launch, snapshots).
. (Join-Path $PSScriptRoot "lib\plugin-staging.ps1")
$installer = Get-ChildItem -Path $bundleDir -Filter "SumaFile_*_x64-winui-setup.exe" -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$props = Get-Content -Path (Join-Path $root "src-winui\Directory.Build.props") -Raw
if ($props -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
    $expectedVersion = $Matches[1]
} elseif ($props -match '<Version>([^<]+)</Version>') {
    $expectedVersion = $Matches[1]
} else {
    throw "Could not read InformationalVersion or Version from src-winui\Directory.Build.props."
}
$timeoutSeconds = 25
$defaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\SumaFile-WinUI"

function Get-WinUIInstall {
    @(Get-SumaFileUninstallEntries -Scope CurrentUser) | Select-Object -First 1
}

function Wait-WinUIInstall {
    $installed = $null
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    do {
        $installed = Get-WinUIInstall
        if ($installed) { return $installed }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "WinUI NSIS install completed, but no uninstall registry entry was found."
}

function Test-SameFolder([string]$Left, [string]$Right) {
    $a = [System.IO.Path]::GetFullPath($Left.Trim().Trim('"')).TrimEnd('\')
    $b = [System.IO.Path]::GetFullPath($Right.Trim().Trim('"')).TrimEnd('\')
    [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase)
}

function Find-WinUIExecutable($installed, [string]$InstallDir) {
    # Only the HKCU entry's InstallLocation, and only when it is this smoke's folder: the smoke never launches another
    # install's executable.
    if (-not $installed.InstallLocation -or -not (Test-SameFolder $installed.InstallLocation $InstallDir)) {
        throw "The SumaFile uninstall entry's InstallLocation is '$($installed.InstallLocation)'; expected $InstallDir."
    }
    $exe = Join-Path $InstallDir "SumaFile.exe"
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Installed SumaFile.exe was not found at $exe." }
    return $exe
}

function Invoke-Uninstall($installed) {
    $uninstallCommand = $installed.Values.QuietUninstallString
    if (-not $uninstallCommand) {
        $uninstallCommand = $installed.Values.UninstallString
    }

    $uninstallerPath = $null
    if ($uninstallCommand -match '^\s*"([^"]+)"') {
        $uninstallerPath = $Matches[1]
    } elseif ($uninstallCommand) {
        $uninstallerPath = ($uninstallCommand -split "\s+", 2)[0]
    }

    if (-not $uninstallerPath -or -not (Test-Path -LiteralPath $uninstallerPath)) {
        throw "Could not find SumaFile uninstaller. UninstallString: '$uninstallCommand'."
    }

    $uninstall = Start-Process -FilePath $uninstallerPath -ArgumentList "/S" -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "WinUI NSIS uninstall failed with exit code $($uninstall.ExitCode)."
    }
}

function Wait-UninstallEntryGone {
    $deadline = (Get-Date).AddSeconds(60)
    while (Get-WinUIInstall) {
        if ((Get-Date) -ge $deadline) { throw "The SumaFile uninstall entry is still registered 60 s after uninstall." }
        Start-Sleep -Milliseconds 500
    }
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

if (-not $installer) {
    throw "No WinUI NSIS installer found in $bundleDir. Run 'npm run build:winui:release' first."
}

# 1. Guards. The installer ends SumaFile by image name, and its shortcuts and uninstall key are per user, so it may
# only run where neither a SumaFile process nor a per-user install exists. A machine-wide entry (such as the 1.0.4
# product) never makes it refuse: it is only snapshot.
Assert-NoSumaFileProcess
$existing = @(Get-SumaFileUninstallEntries -Scope CurrentUser)
if ($existing.Count -gt 0) {
    throw "SumaFile is already installed for this user at '$($existing[0].InstallLocation)' ($($existing[0].Key)). Uninstall it before running this smoke test."
}
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) "SumaFile.lnk"),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) "SumaFile.lnk")
) | Where-Object { Test-Path -LiteralPath $_ }
if (@($shortcuts).Count -gt 0) {
    throw "The NSIS installer replaces and its uninstaller deletes $($shortcuts -join ', '); remove or move them before running this smoke test."
}
$machineBefore = @(Get-SumaFileUninstallEntries -Scope Machine)
$defaultBefore = Get-FolderSnapshot -Path $defaultInstallDir
$legacy = Get-LegacyPluginLocationSnapshot

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-nsis-smoke-" + [System.Guid]::NewGuid().ToString("N"))
$installDir = Join-Path $smokeRoot "SumaFile-WinUI"
$process = $null
$passed = $false

try {
    New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

    # 2. Install into the disposable folder. /D= must come last and unquoted, in one argument string.
    Write-Host "Installing $($installer.FullName) into $installDir."
    $install = Start-Process -FilePath $installer.FullName -ArgumentList "/S /D=$installDir" -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "WinUI NSIS install failed with exit code $($install.ExitCode)."
    }

    # 3. Version, bundled plugins, and a launch whose default plugin locations land in the install folder.
    $installed = Wait-WinUIInstall
    if ($installed.DisplayVersion -ne $expectedVersion) {
        throw "Installed WinUI version '$($installed.DisplayVersion)' did not match expected '$expectedVersion'."
    }
    $exePath = Find-WinUIExecutable $installed $installDir
    if (-not (Test-Path -LiteralPath (Join-Path $installDir "simplefile-service.exe"))) {
        throw "Installed simplefile-service.exe was not found next to SumaFile.exe."
    }
    Write-Host "Installed SumaFile $($installed.DisplayVersion) at $exePath."

    Assert-BundledPlugins -Root (Join-Path $installDir "plugins")
    if (Test-Path -LiteralPath (Join-Path $installDir "tools\7zip")) {
        throw "The NSIS install must not contain tools\7zip; 7-Zip ships as the bundled plugin."
    }
    Write-Host "Installed SumaFile carries the bundled plugins."

    $process = Start-InstalledSumaFile -Exe $exePath -SmokeRoot $smokeRoot
    $windowProcess = Wait-SumaFileWindow -Process $process -TimeoutSeconds $timeoutSeconds
    Write-Host "Installed SumaFile shows its window: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)'."
    $sevenZipData = Join-Path (Join-Path $installDir "plugins-data") "com.sumafile.sevenzip"
    Wait-PathsExist @(
        (Join-Path $installDir "plugins.log"),
        $sevenZipData,
        (Join-Path $sevenZipData "tmp")
    ) $timeoutSeconds "The installed app's plugin log, data folder and backend temp folder must be in its install folder."
    Write-Host "The installed app keeps plugins.log and plugins-data (with the backend's tmp) in $installDir."

    # 4. Close the app and stop everything that runs from the install folder.
    $stopped = @(Stop-ProcessesUnderFolder -Path $installDir -MainProcess $process)
    $process = $null
    Write-Host "Closed the installed app; stopped by path: $(if ($stopped.Count) { $stopped -join ', ' } else { 'nothing left' })."

    # 5. A user's own plugin and its data, beside what the app wrote.
    New-UserPluginSeed -InstallDir $installDir
    $before = Get-AppOwnedPluginState -InstallDir $installDir
    Write-Host "Seeded a user plugin; app-owned plugin state: $(@($before.Keys).Count) entries."

    # 6. Reinstall without /D=: InstallDirRegKey reads the entry's InstallLocation.
    $installed = Get-WinUIInstall
    if (-not $installed -or -not (Test-SameFolder $installed.InstallLocation $installDir)) {
        throw "Before the reinstall the uninstall entry's InstallLocation is '$($installed.InstallLocation)'; expected $installDir."
    }
    Write-Host "Reinstalling $($installer.FullName) without /D=."
    $reinstall = Start-Process -FilePath $installer.FullName -ArgumentList "/S" -Wait -PassThru
    if ($reinstall.ExitCode -ne 0) {
        throw "WinUI NSIS reinstall failed with exit code $($reinstall.ExitCode)."
    }
    # A regressed InstallDirRegKey would have installed into the default folder: stop here and name what changed.
    Assert-FolderSnapshotUnchanged -Path $defaultInstallDir -Before $defaultBefore -Label "The reinstall"
    Assert-SumaFileUninstallEntriesUnchanged -Scope Machine -Before $machineBefore -Label "The reinstall"
    $installed = Wait-WinUIInstall
    if (-not (Test-SameFolder $installed.InstallLocation $installDir)) {
        throw "After the reinstall the uninstall entry's InstallLocation is '$($installed.InstallLocation)'; expected $installDir."
    }
    Assert-AppOwnedPluginStateUnchanged -InstallDir $installDir -Before $before
    Assert-BundledPlugins -Root (Join-Path $installDir "plugins")
    Write-Host "The reinstall went to $installDir and kept the user plugin, its data, the approvals and the plugin log."

    if ($KeepInstalled) {
        Write-Host "Keeping installed SumaFile at $installDir because -KeepInstalled was supplied; the uninstall checks did not run."
        $passed = $true
        return
    }

    # 7. Uninstall: $INSTDIR goes whole, user plugin files included.
    Write-Host "Uninstalling SumaFile $($installed.DisplayVersion)."
    Invoke-Uninstall $installed
    Wait-FolderGone -Path $installDir -TimeoutSeconds 60
    Wait-UninstallEntryGone
    Write-Host "Uninstalled SumaFile; $installDir is gone."

    # 8. Nothing outside the smoke changed.
    Assert-SumaFileUninstallEntriesUnchanged -Scope Machine -Before $machineBefore -Label "The NSIS smoke"
    Assert-FolderSnapshotUnchanged -Path $defaultInstallDir -Before $defaultBefore -Label "The NSIS smoke"
    Assert-LegacyPluginLocationsUnchanged -Before $legacy

    Write-Host "WinUI NSIS install smoke passed: the reinstall kept the user plugin and its data; uninstall left nothing in $installDir."
    $passed = $true
}
finally {
    $stopped = @(Stop-ProcessesUnderFolder -Path $installDir -MainProcess $process)
    if ($stopped.Count -gt 0) { Write-Host "Stopped by path under ${installDir}: $($stopped -join ', ')." }

    if (-not $KeepInstalled) {
        # Uninstall only this smoke's own install: an entry pointing anywhere else would make the uninstaller remove
        # that folder.
        $installed = Get-WinUIInstall
        if ($installed -and (Test-SameFolder $installed.InstallLocation $installDir)) {
            Write-Host "Uninstalling SumaFile $($installed.DisplayVersion) from $installDir."
            try {
                Invoke-Uninstall $installed
                Wait-FolderGone -Path $installDir -TimeoutSeconds 60
            } catch {
                Write-Warning "Cleanup uninstall failed: $($_.Exception.Message)"
            }
        } elseif ($installed) {
            Write-Warning "A SumaFile uninstall entry points at '$($installed.InstallLocation)', not this smoke's $installDir; it was left installed. Check it before uninstalling."
        }
        if ($passed -and (Test-Path -LiteralPath $smokeRoot)) {
            Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $passed) { Write-Host "WinUI NSIS install smoke did not pass. Evidence (app data, logs): $smokeRoot" }
}
