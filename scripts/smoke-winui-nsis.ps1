param(
    [switch]$KeepInstalled
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$bundleDir = Join-Path $root "dist\winui"
$installer = Get-ChildItem -Path $bundleDir -Filter "SumaFile_*_x64-winui-setup.exe" -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$expectedTitle = "SumaFile"
$props = Get-Content -Path (Join-Path $root "src-winui\Directory.Build.props") -Raw
if ($props -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
    $expectedVersion = $Matches[1]
} elseif ($props -match '<Version>([^<]+)</Version>') {
    $expectedVersion = $Matches[1]
} else {
    throw "Could not read InformationalVersion or Version from src-winui\Directory.Build.props."
}
$timeoutSeconds = 25
$process = $null

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

if (-not $installer) {
    throw "No WinUI NSIS installer found in $bundleDir. Run 'npm run build:winui:release' first."
}

$existing = Get-WinUIInstall
if ($existing) {
    throw "SumaFile is already installed at '$($existing.InstallLocation)'. Uninstall it before running this smoke test."
}

try {
    Write-Host "Installing $($installer.FullName)."
    $install = Start-Process -FilePath $installer.FullName -ArgumentList "/S" -Wait -PassThru
    if ($install.ExitCode -ne 0) {
        throw "WinUI NSIS install failed with exit code $($install.ExitCode)."
    }

    $installed = $null
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $installed = Get-WinUIInstall
    } while (-not $installed -and (Get-Date) -lt $deadline)

    if (-not $installed) {
        throw "WinUI NSIS install completed, but no uninstall registry entry was found."
    }

    if ($installed.DisplayVersion -ne $expectedVersion) {
        throw "Installed WinUI version '$($installed.DisplayVersion)' did not match expected '$expectedVersion'."
    }

    $exePath = Find-WinUIExecutable $installed
    if (-not $exePath) {
        throw "Installed SumaFile.exe was not found."
    }

    $servicePath = Join-Path (Split-Path -Parent $exePath) "simplefile-service.exe"
    if (-not (Test-Path -LiteralPath $servicePath)) {
        throw "Installed simplefile-service.exe was not found next to SumaFile.exe."
    }

    Write-Host "Installed SumaFile $($installed.DisplayVersion) at $exePath."

    $process = Start-Process -FilePath $exePath -WorkingDirectory (Split-Path -Parent $exePath) -PassThru
    $windowProcess = $null
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)

    do {
        Start-Sleep -Milliseconds 500
        $candidate = Get-Process -Id $process.Id -ErrorAction SilentlyContinue

        if ($candidate -and $candidate.MainWindowTitle -eq $expectedTitle -and $candidate.Responding) {
            $windowProcess = $candidate
            break
        }
    } while ((Get-Date) -lt $deadline)

    if (-not $windowProcess) {
        $lastProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        $lastTitle = if ($lastProcess) { $lastProcess.MainWindowTitle } else { "<process exited>" }
        throw "Installed WinUI executable did not expose '$expectedTitle' within $timeoutSeconds seconds. Last title: '$lastTitle'."
    }

    Write-Host "WinUI NSIS install smoke passed: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)'."
}
finally {
    Get-Process -Name "simplefile-service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($process) {
        $startedProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if ($startedProcess) {
            $closed = $startedProcess.CloseMainWindow()
            Start-Sleep -Seconds 2
            $startedProcess = Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue
            if ($startedProcess) {
                Stop-Process -Id $startedProcess.Id -Force
            }
            Write-Host "Closed WinUI NSIS smoke-test process $($process.Id). CloseMainWindow sent: $closed."
        }
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
}
