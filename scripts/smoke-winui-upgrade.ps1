param(
    [string]$PreviousInstaller,
    [string]$NewInstaller,
    [switch]$SkipWhenPreviousUnavailable,
    [switch]$KeepInstalled
)

# Installs the previous release's NSIS setup into a disposable folder under %TEMP%, seeds a user plugin, upgrades with
# the new setup without /D= (InstallDirRegKey must reuse the folder and the user plugin files must survive), launches
# both versions from that folder only, then uninstalls and requires the folder gone. Like smoke-winui-nsis.ps1 it
# refuses while SumaFile runs or a per-user install exists, and fails when the machine entries, the default install
# folder or the pre-release plugin locations changed.

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$bundleDir = Join-Path $root "dist\winui"

# Assert-BundledPlugins, the app-owned plugin state and the installer smoke helpers (guards, seed, launch, snapshots).
. (Join-Path $PSScriptRoot "lib\plugin-staging.ps1")
$timeoutSeconds = 25
$process = $null
$sentinelPath = $null
$previousDownloadDir = $null
$defaultInstallDir = Join-Path $env:LOCALAPPDATA "Programs\SumaFile-WinUI"
$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-upgrade-smoke-" + [System.Guid]::NewGuid().ToString("N"))
$installDir = Join-Path $smokeRoot "SumaFile-WinUI"

function Get-ExpectedVersion {
    $props = Get-Content -Path (Join-Path $root "src-winui\Directory.Build.props") -Raw
    if ($props -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
        return $Matches[1]
    }
    if ($props -match '<Version>([^<]+)</Version>') {
        return $Matches[1]
    }
    throw "Could not read InformationalVersion or Version from src-winui\Directory.Build.props."
}

function Get-WinUIInstall {
    @(Get-SumaFileUninstallEntries -Scope CurrentUser) | Select-Object -First 1
}

function Test-SameFolder([string]$Left, [string]$Right) {
    $a = [System.IO.Path]::GetFullPath($Left.Trim().Trim('"')).TrimEnd('\')
    $b = [System.IO.Path]::GetFullPath($Right.Trim().Trim('"')).TrimEnd('\')
    [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase)
}

function Find-WinUIExecutable($installed) {
    # Only the HKCU entry's InstallLocation, and only when it is this smoke's folder: the smoke never launches another
    # install's executable.
    if (-not $installed.InstallLocation -or -not (Test-SameFolder $installed.InstallLocation $installDir)) {
        throw "The SumaFile uninstall entry's InstallLocation is '$($installed.InstallLocation)'; expected $installDir."
    }
    $exe = Join-Path $installDir "SumaFile.exe"
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    return $null
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

function Resolve-NewInstaller {
    if ($NewInstaller) {
        $resolved = Resolve-Path -LiteralPath $NewInstaller -ErrorAction Stop
        return $resolved.Path
    }

    $installer = Get-ChildItem -Path $bundleDir -Filter "SumaFile_*_x64-winui-setup.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $installer) {
        throw "No new WinUI NSIS installer found in $bundleDir. Run 'npm run build:winui:release' first."
    }

    return $installer.FullName
}

function Resolve-PreviousInstaller {
    if ($PreviousInstaller) {
        $resolved = Resolve-Path -LiteralPath $PreviousInstaller -ErrorAction Stop
        return $resolved.Path
    }

    if ($env:SUMAFILE_PREVIOUS_INSTALLER) {
        $resolved = Resolve-Path -LiteralPath $env:SUMAFILE_PREVIOUS_INSTALLER -ErrorAction Stop
        return $resolved.Path
    }

    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        if ($SkipWhenPreviousUnavailable) {
            Write-Host "Skipping upgrade smoke: gh CLI is unavailable and no previous installer was supplied."
            return $null
        }
        throw "Pass -PreviousInstaller or install gh CLI so the latest published installer can be downloaded."
    }

    $downloadDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-previous-installer-" + [System.Guid]::NewGuid().ToString("N"))
    $script:previousDownloadDir = $downloadDir
    New-Item -ItemType Directory -Force -Path $downloadDir | Out-Null
    try {
        & $gh.Source release download --repo conniecombs/SumaFile --pattern "SumaFile_*_x64-winui-setup.exe" --dir $downloadDir --clobber
        if ($LASTEXITCODE -ne 0) {
            if ($SkipWhenPreviousUnavailable) {
                Write-Host "Skipping upgrade smoke: no published SumaFile NSIS installer could be downloaded."
                return $null
            }
            throw "gh release download failed with exit code $LASTEXITCODE."
        }

        $downloaded = Get-ChildItem -Path $downloadDir -Filter "SumaFile_*_x64-winui-setup.exe" -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $downloaded) {
            if ($SkipWhenPreviousUnavailable) {
                Write-Host "Skipping upgrade smoke: latest published release has no SumaFile NSIS installer."
                return $null
            }
            throw "Latest published release has no SumaFile NSIS installer."
        }

        return $downloaded.FullName
    }
    catch {
        if ($SkipWhenPreviousUnavailable) {
            Write-Host "Skipping upgrade smoke: $($_.Exception.Message)"
            return $null
        }
        throw
    }
}

function Clear-PreviousDownload {
    if ($script:previousDownloadDir -and (Test-Path -LiteralPath $script:previousDownloadDir)) {
        Remove-Item -LiteralPath $script:previousDownloadDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    $script:previousDownloadDir = $null
}


function Invoke-Installer {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Label,
        [string]$Arguments = "/S"
    )

    Write-Host "$Label $Path ($Arguments)."
    $install = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "$Label failed with exit code $($install.ExitCode)."
    }
}

function Assert-Installed {
    param([string]$ExpectedVersion)

    $installed = $null
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $installed = Get-WinUIInstall
    } while (-not $installed -and (Get-Date) -lt $deadline)

    if (-not $installed) {
        throw "No SumaFile uninstall registry entry was found after install."
    }

    if ($ExpectedVersion -and $installed.DisplayVersion -ne $ExpectedVersion) {
        throw "Installed WinUI version '$($installed.DisplayVersion)' did not match expected '$ExpectedVersion'."
    }

    $exePath = Find-WinUIExecutable $installed
    if (-not $exePath) {
        throw "Installed SumaFile.exe was not found in $installDir."
    }

    $servicePath = Join-Path (Split-Path -Parent $exePath) "simplefile-service.exe"
    if (-not (Test-Path -LiteralPath $servicePath)) {
        throw "Installed simplefile-service.exe was not found next to SumaFile.exe."
    }

    return [pscustomobject]@{
        Registry = $installed
        ExePath = $exePath
        ServicePath = $servicePath
    }
}

function Assert-Launches {
    param([Parameter(Mandatory = $true)][string]$ExePath)

    $script:process = Start-InstalledSumaFile -Exe $ExePath -SmokeRoot $smokeRoot
    $windowProcess = Wait-SumaFileWindow -Process $script:process -TimeoutSeconds $timeoutSeconds
    Write-Host "SumaFile launch check passed: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)'."
}

function Stop-SmokeProcesses {
    # By path under the disposable install folder only, never by name.
    $stopped = @(Stop-ProcessesUnderFolder -Path $installDir -MainProcess $script:process)
    if ($stopped.Count -gt 0) { Write-Host "Stopped by path under ${installDir}: $($stopped -join ', ')." }
    $script:process = $null
}

$expectedVersion = Get-ExpectedVersion
$newInstallerPath = Resolve-NewInstaller
$previousInstallerPath = Resolve-PreviousInstaller
if (-not $previousInstallerPath) {
    Clear-PreviousDownload
    exit 0
}

# Guards: the installers end SumaFile by image name and own per-user shortcuts and an HKCU uninstall key, so the smoke
# runs only where neither a SumaFile process nor a per-user install exists. Machine-wide entries are only snapshot.
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
$passed = $false

try {
    New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
    # /D= must come last and unquoted, in one argument string.
    Invoke-Installer -Path $previousInstallerPath -Label "Installing previous SumaFile" -Arguments "/S /D=$installDir"
    $previous = Assert-Installed
    Write-Host "Previous SumaFile $($previous.Registry.DisplayVersion) installed at $($previous.ExePath)."

    $appData = Join-Path $env:APPDATA "com.simplefile.desktop"
    New-Item -ItemType Directory -Force -Path $appData | Out-Null
    $sentinelPath = Join-Path $appData "upgrade-smoke-sentinel.txt"
    $sentinel = [System.Guid]::NewGuid().ToString("N")
    Set-Content -LiteralPath $sentinelPath -Value $sentinel -NoNewline -Encoding UTF8

    Assert-Launches -ExePath $previous.ExePath
    Stop-SmokeProcesses

    # A user's own plugin and its data in the install folder; the upgrade must keep them.
    New-UserPluginSeed -InstallDir $installDir
    $before = Get-AppOwnedPluginState -InstallDir $installDir

    # No /D=: InstallDirRegKey reads the entry's InstallLocation, which the previous installer wrote.
    if (-not (Test-SameFolder $previous.Registry.InstallLocation $installDir)) {
        throw "Before the upgrade the uninstall entry's InstallLocation is '$($previous.Registry.InstallLocation)'; expected $installDir."
    }
    Invoke-Installer -Path $newInstallerPath -Label "Upgrading SumaFile"
    Assert-FolderSnapshotUnchanged -Path $defaultInstallDir -Before $defaultBefore -Label "The upgrade"
    Assert-SumaFileUninstallEntriesUnchanged -Scope Machine -Before $machineBefore -Label "The upgrade"
    $upgraded = Assert-Installed -ExpectedVersion $expectedVersion
    Write-Host "Upgraded SumaFile $($upgraded.Registry.DisplayVersion) installed at $($upgraded.ExePath)."
    Assert-AppOwnedPluginStateUnchanged -InstallDir $installDir -Before $before
    Write-Host "The upgrade kept the user plugin, its data and the approvals."

    # 1.0.4 shipped tools\7zip; the upgrade must remove it and install the bundled plugin.
    $upgradedDir = Split-Path -Parent $upgraded.ExePath
    Assert-BundledPlugins -Root (Join-Path $upgradedDir "plugins")
    if (Test-Path -LiteralPath (Join-Path $upgradedDir "tools\7zip")) {
        throw "The upgrade left tools\7zip in $upgradedDir; 7-Zip ships as the bundled plugin."
    }
    Write-Host "Upgraded SumaFile carries the bundled plugins and no tools\7zip."

    if (-not (Test-Path -LiteralPath $sentinelPath)) {
        throw "Upgrade removed persisted app data sentinel at $sentinelPath."
    }
    $after = Get-Content -LiteralPath $sentinelPath -Raw
    if ($after.Trim() -ne $sentinel) {
        throw "Upgrade changed persisted app data sentinel."
    }

    Assert-Launches -ExePath $upgraded.ExePath
    Stop-SmokeProcesses

    if (-not $KeepInstalled) {
        Write-Host "Uninstalling SumaFile $($upgraded.Registry.DisplayVersion)."
        Invoke-Uninstall $upgraded.Registry
        Wait-FolderGone -Path $installDir -TimeoutSeconds 60
        Assert-SumaFileUninstallEntriesUnchanged -Scope Machine -Before $machineBefore -Label "The upgrade smoke"
        Assert-FolderSnapshotUnchanged -Path $defaultInstallDir -Before $defaultBefore -Label "The upgrade smoke"
        Assert-LegacyPluginLocationsUnchanged -Before $legacy
        Write-Host "Uninstalled SumaFile; $installDir is gone."
    }
    Write-Host "WinUI NSIS upgrade smoke passed."
    $passed = $true
}
finally {
    Stop-SmokeProcesses
    if ($sentinelPath -and (Test-Path -LiteralPath $sentinelPath)) {
        Remove-Item -LiteralPath $sentinelPath -Force -ErrorAction SilentlyContinue
    }

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
            Write-Host "Uninstalled SumaFile."
        } elseif ($installed) {
            Write-Warning "A SumaFile uninstall entry points at '$($installed.InstallLocation)', not this smoke's $installDir; it was left installed. Check it before uninstalling."
        }
        if ($passed -and (Test-Path -LiteralPath $smokeRoot)) {
            Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    } else {
        Write-Host "Keeping installed SumaFile at $installDir because -KeepInstalled was supplied."
    }

    Clear-PreviousDownload
}
