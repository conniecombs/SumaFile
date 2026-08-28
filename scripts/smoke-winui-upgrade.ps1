param(
    [string]$PreviousInstaller,
    [string]$NewInstaller,
    [switch]$SkipWhenPreviousUnavailable,
    [switch]$KeepInstalled
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$bundleDir = Join-Path $root "dist\winui"
$expectedTitle = "SumaFile"
$timeoutSeconds = 25
$process = $null
$sentinelPath = $null
$previousDownloadDir = $null

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
    Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*" -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -eq "SumaFile" } |
        Select-Object -First 1
}

function Find-WinUIExecutable($installed) {
    $candidates = @()
    if ($installed.InstallLocation) {
        $installLocation = $installed.InstallLocation.Trim().Trim('"')
        $candidates += Join-Path $installLocation "SumaFile.exe"
    }

    $candidates += Join-Path $env:LOCALAPPDATA "Programs\SumaFile-WinUI\SumaFile.exe"

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }

    return $null
}

function Invoke-Uninstall($installed) {
    $uninstallCommand = $installed.QuietUninstallString
    if (-not $uninstallCommand) {
        $uninstallCommand = $installed.UninstallString
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
        [Parameter(Mandatory = $true)][string]$Label
    )

    Write-Host "$Label $Path."
    $install = Start-Process -FilePath $Path -ArgumentList "/S" -Wait -PassThru
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
        throw "Installed SumaFile.exe was not found."
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

    $script:process = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path -Parent $ExePath) -PassThru
    $windowProcess = $null
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)

    do {
        Start-Sleep -Milliseconds 500
        $candidate = Get-Process -Id $script:process.Id -ErrorAction SilentlyContinue
        if ($candidate -and $candidate.MainWindowTitle -eq $expectedTitle -and $candidate.Responding) {
            $windowProcess = $candidate
            break
        }
    } while ((Get-Date) -lt $deadline)

    if (-not $windowProcess) {
        $lastProcess = Get-Process -Id $script:process.Id -ErrorAction SilentlyContinue
        $lastTitle = if ($lastProcess) { $lastProcess.MainWindowTitle } else { "<process exited>" }
        throw "Installed WinUI executable did not expose '$expectedTitle' within $timeoutSeconds seconds. Last title: '$lastTitle'."
    }

    Write-Host "SumaFile launch check passed: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)'."
}

function Stop-SmokeProcesses {
    Get-Process -Name "simplefile-service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($script:process) {
        $startedProcess = Get-Process -Id $script:process.Id -ErrorAction SilentlyContinue
        if ($startedProcess) {
            $closed = $startedProcess.CloseMainWindow()
            Start-Sleep -Seconds 2
            $startedProcess = Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue
            if ($startedProcess) {
                Stop-Process -Id $startedProcess.Id -Force
            }
            Write-Host "Closed WinUI upgrade smoke-test process $($script:process.Id). CloseMainWindow sent: $closed."
        }
        $script:process = $null
    }
}

$expectedVersion = Get-ExpectedVersion
$newInstallerPath = Resolve-NewInstaller
$previousInstallerPath = Resolve-PreviousInstaller
if (-not $previousInstallerPath) {
    Clear-PreviousDownload
    exit 0
}

$existing = Get-WinUIInstall
if ($existing) {
    throw "SumaFile is already installed at '$($existing.InstallLocation)'. Uninstall it before running this smoke test."
}

try {
    Invoke-Installer -Path $previousInstallerPath -Label "Installing previous SumaFile"
    $previous = Assert-Installed
    Write-Host "Previous SumaFile $($previous.Registry.DisplayVersion) installed at $($previous.ExePath)."

    $appData = Join-Path $env:APPDATA "com.simplefile.desktop"
    New-Item -ItemType Directory -Force -Path $appData | Out-Null
    $sentinelPath = Join-Path $appData "upgrade-smoke-sentinel.txt"
    $sentinel = [System.Guid]::NewGuid().ToString("N")
    Set-Content -LiteralPath $sentinelPath -Value $sentinel -NoNewline -Encoding UTF8

    Assert-Launches -ExePath $previous.ExePath
    Stop-SmokeProcesses

    Invoke-Installer -Path $newInstallerPath -Label "Upgrading SumaFile"
    $upgraded = Assert-Installed -ExpectedVersion $expectedVersion
    Write-Host "Upgraded SumaFile $($upgraded.Registry.DisplayVersion) installed at $($upgraded.ExePath)."

    if (-not (Test-Path -LiteralPath $sentinelPath)) {
        throw "Upgrade removed persisted app data sentinel at $sentinelPath."
    }
    $after = Get-Content -LiteralPath $sentinelPath -Raw
    if ($after.Trim() -ne $sentinel) {
        throw "Upgrade changed persisted app data sentinel."
    }

    Assert-Launches -ExePath $upgraded.ExePath
    Write-Host "WinUI NSIS upgrade smoke passed."
}
finally {
    Stop-SmokeProcesses
    if ($sentinelPath -and (Test-Path -LiteralPath $sentinelPath)) {
        Remove-Item -LiteralPath $sentinelPath -Force -ErrorAction SilentlyContinue
    }

    if (-not $KeepInstalled) {
        $installed = Get-WinUIInstall
        if ($installed) {
            Write-Host "Uninstalling SumaFile $($installed.DisplayVersion)."
            Invoke-Uninstall $installed
            Write-Host "Uninstalled SumaFile."
        }
    } else {
        Write-Host "Keeping installed SumaFile because -KeepInstalled was supplied."
    }

    Clear-PreviousDownload
}
