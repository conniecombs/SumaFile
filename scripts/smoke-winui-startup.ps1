$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$payloadDir = Join-Path $root "dist\winui\payload"
$exePath = Join-Path $payloadDir "SumaFile.exe"
$servicePath = Join-Path $payloadDir "simplefile-service.exe"
$expectedTitle = "SumaFile"
$timeoutSeconds = 25
$startupTimingLog = Join-Path $env:LOCALAPPDATA "SumaFile\startup-timing.log"
$payloadPluginsDir = Join-Path $payloadDir "plugins"
$sevenZipPluginId = "com.sumafile.sevenzip"
$sevenZipRegisteredLine = "[$sevenZipPluginId] [info] Archive engines registered: .7z"
$remotePluginId = "com.sumafile.remote"
# Written at discovery (Task 3), so it does not depend on whether the user's sidebar shows the Connections section.
$remoteDiscoveredLine = "[$remotePluginId] [info] Discovered: Enabled"
$pluginRegistrationTimeoutSeconds = 20

# Get-AppOwnedPluginItems and the pre-release plugin location checks.
. (Join-Path $PSScriptRoot "lib\plugin-staging.ps1")

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "WinUI payload executable not found at $exePath. Run 'npm run build:winui:release' first."
}
if (-not (Test-Path -LiteralPath $servicePath)) {
    throw "WinUI payload is missing simplefile-service.exe at $servicePath."
}
if (-not (Test-Path -LiteralPath (Join-Path $payloadDir "resources.pri"))) {
    throw "WinUI payload is missing resources.pri."
}
if (-not (Test-Path -LiteralPath (Join-Path $payloadDir "MainWindow.xbf"))) {
    throw "WinUI payload is missing MainWindow.xbf."
}
if (-not (Test-Path -LiteralPath (Join-Path $payloadPluginsDir "bundled.json"))) {
    throw "WinUI payload is missing plugins\bundled.json."
}
# The payload is the install folder of this run; it must start, and stay, free of app-owned plugin files.
$payloadAppOwned = @(Get-AppOwnedPluginItems -InstallDir $payloadDir)
if ($payloadAppOwned.Count -gt 0) {
    throw "WinUI payload already contains app-owned plugin files before the startup smoke: $($payloadAppOwned -join ', '). Rebuild the payload."
}

$startupTimingOffset = 0
if (Test-Path -LiteralPath $startupTimingLog) {
    $startupTimingOffset = (Get-Item -LiteralPath $startupTimingLog).Length
}

function Get-NewStartupTimingLines {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Offset
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite
    )
    try {
        if ($Offset -gt $stream.Length) {
            $Offset = 0
        }

        $stream.Seek($Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            $text = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    if ([string]::IsNullOrWhiteSpace($text)) {
        return @()
    }

    return $text -split "`r?`n" | Where-Object { $_ }
}

# Prove bundled trust: scan only the payload's plugins folder, and keep approvals in a disposable file that must stay
# free of the bundled plugins. The install root is the payload's plugins folder, so its bundled.json applies.
$approvalsDir = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-startup-smoke-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $approvalsDir | Out-Null
$approvalsPath = Join-Path $approvalsDir "plugin-approvals.json"
# Never the user's data, and nothing written into the payload, which is this run's install folder: plugin backends
# write under a disposable data root, the plugin log is a fresh disposable file (so this smoke reads it from the
# start), and the service and the remote backend read and seed a disposable metadata.db, so the remote plugin
# imports no real profiles and writes no plugin.com.sumafile.remote.* keys into the real settings.
# startup-timing.log is the app's own and stays in %LOCALAPPDATA%\SumaFile, where this smoke reads it.
$pluginsLog = Join-Path $approvalsDir "plugins.log"
$smokeAppDataDir = Join-Path $approvalsDir "app-data"
New-Item -ItemType Directory -Force -Path $smokeAppDataDir | Out-Null
$smokeEnvironment = [ordered]@{
    SUMAFILE_PLUGIN_ROOTS = $payloadPluginsDir
    SUMAFILE_PLUGIN_APPROVALS_PATH = $approvalsPath
    SUMAFILE_PLUGIN_DATA_ROOT = Join-Path $approvalsDir "plugins-data"
    SUMAFILE_PLUGIN_LOG_PATH = $pluginsLog
    SIMPLEFILE_METADATA_DB = Join-Path $smokeAppDataDir "metadata.db"
    SIMPLEFILE_APP_DATA_DIR = $smokeAppDataDir
}
$previousEnvironment = @{}
foreach ($name in $smokeEnvironment.Keys) {
    $previousEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name, "Process")
}
$process = $null
$windowProcess = $null
$smokeCompleted = $false
# The app never reads, writes or deletes the pre-release plugin locations under %LOCALAPPDATA%\SumaFile.
$legacyPluginLocations = Get-LegacyPluginLocationSnapshot

try {
    foreach ($name in $smokeEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $smokeEnvironment[$name], "Process")
    }
    $process = Start-Process -FilePath $exePath -WorkingDirectory $payloadDir -PassThru

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
        throw "WinUI executable did not expose '$expectedTitle' within $timeoutSeconds seconds. Last title: '$lastTitle'."
    }

    $startupReady = $false
    do {
        Start-Sleep -Milliseconds 250
        $lines = @(Get-NewStartupTimingLines -Path $startupTimingLog -Offset $startupTimingOffset)
        if ($lines | Where-Object { $_ -like "* MainWindow.Connect.failed *" }) {
            $failure = ($lines | Where-Object { $_ -like "* MainWindow.Connect.failed *" } | Select-Object -Last 1)
            throw "WinUI backend startup failed before ready: $failure"
        }

        if ($lines | Where-Object { $_ -like "* MainWindow.Connect.ready *" }) {
            $startupReady = $true
            break
        }

        $candidate = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        if (-not $candidate) {
            throw "WinUI process exited before backend startup reached ready."
        }
    } while ((Get-Date) -lt $deadline)

    if (-not $startupReady) {
        throw "WinUI backend startup did not reach ready within $timeoutSeconds seconds."
    }

    $service = Get-Process -Name "simplefile-service" -ErrorAction SilentlyContinue
    if (-not $service) {
        throw "WinUI host started but simplefile-service.exe was not running."
    }

    Write-Host "WinUI startup smoke passed: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)', backend ready."

    # The bundled 7-Zip plugin must be trusted through bundled.json, start, and register .7z with the service.
    $registered = $false
    $registrationDeadline = (Get-Date).AddSeconds($pluginRegistrationTimeoutSeconds)
    do {
        $pluginLines = @(Get-NewStartupTimingLines -Path $pluginsLog -Offset 0)
        if ($pluginLines | Where-Object { $_.Contains($sevenZipRegisteredLine) }) {
            $registered = $true
            break
        }

        if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
            throw "WinUI process exited before the bundled 7-Zip plugin registered its archive engine."
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $registrationDeadline)

    if (-not $registered) {
        $recent = @(Get-NewStartupTimingLines -Path $pluginsLog -Offset 0 | Select-Object -Last 10) -join [Environment]::NewLine
        throw "$pluginsLog did not record '$sevenZipRegisteredLine' within $pluginRegistrationTimeoutSeconds seconds. Last plugins.log lines:$([Environment]::NewLine)$recent"
    }

    if (Test-Path -LiteralPath $approvalsPath) {
        $approvals = [System.IO.File]::ReadAllText($approvalsPath)
        if ($approvals.Contains($sevenZipPluginId)) {
            throw "The bundled 7-Zip plugin was written to the approval store at ${approvalsPath}; it must be trusted through bundled.json alone."
        }
    }

    Write-Host "Bundled 7-Zip plugin trusted and registered."

    # The bundled remote plugin must be discovered from the payload and Enabled through bundled.json alone.
    $remoteDiscovered = $false
    $remoteDeadline = (Get-Date).AddSeconds($pluginRegistrationTimeoutSeconds)
    do {
        $pluginLines = @(Get-NewStartupTimingLines -Path $pluginsLog -Offset 0)
        if ($pluginLines | Where-Object { $_.Contains($remoteDiscoveredLine) }) {
            $remoteDiscovered = $true
            break
        }

        if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
            throw "WinUI process exited before plugins.log recorded the bundled remote plugin as Enabled."
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $remoteDeadline)

    if (-not $remoteDiscovered) {
        $recent = @(Get-NewStartupTimingLines -Path $pluginsLog -Offset 0 | Where-Object { $_.Contains("[$remotePluginId]") } | Select-Object -Last 10) -join [Environment]::NewLine
        throw "$pluginsLog did not record '$remoteDiscoveredLine' within $pluginRegistrationTimeoutSeconds seconds. Last $remotePluginId lines:$([Environment]::NewLine)$recent"
    }

    if (Test-Path -LiteralPath $approvalsPath) {
        $approvals = [System.IO.File]::ReadAllText($approvalsPath)
        if ($approvals.Contains($remotePluginId)) {
            throw "The bundled remote plugin was written to the approval store at ${approvalsPath}; it must be trusted through bundled.json alone."
        }
    }

    Write-Host "Bundled remote plugin trusted."
    $smokeCompleted = $true
}
finally {
    foreach ($name in $smokeEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }

    Get-Process -Name "simplefile-service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $startedProcess = if ($process) { Get-Process -Id $process.Id -ErrorAction SilentlyContinue } else { $null }
    if ($startedProcess) {
        $closed = $startedProcess.CloseMainWindow()
        Start-Sleep -Seconds 2
        $startedProcess = Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue
        if ($startedProcess) {
            Stop-Process -Id $startedProcess.Id -Force
        }
        Write-Host "Closed WinUI smoke-test process $($process.Id). CloseMainWindow sent: $closed."
    }

    # The host's kill-on-close job ends the backend with the app; stop any payload backend that is still running.
    # The remote backend starts on first call, for example when the sidebar shows the Connections section.
    $backends = @(Get-Process -Name "sumafile-sevenzip-backend", "sumafile-remote-backend" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($payloadPluginsDir, [System.StringComparison]::OrdinalIgnoreCase) })
    $backends | Stop-Process -Force -ErrorAction SilentlyContinue
    # Wait, so the backends release their files in the disposable data root and leave nothing half-written behind
    # before the checks below and the clean-up run.
    foreach ($backend in $backends) {
        try { [void]$backend.WaitForExit(5000) } catch { }
    }

    # Checked once every process is gone. A failure here fails a smoke that otherwise passed; after an earlier
    # failure it is only reported, so the first error stays the one thrown.
    $afterRunFailures = @()
    $payloadAppOwned = @(Get-AppOwnedPluginItems -InstallDir $payloadDir)
    if ($payloadAppOwned.Count -gt 0) {
        $afterRunFailures += "The startup smoke left app-owned plugin files in the payload ${payloadDir}: $($payloadAppOwned -join ', ')"
    }
    try {
        Assert-LegacyPluginLocationsUnchanged -Before $legacyPluginLocations
    } catch {
        $afterRunFailures += $_.Exception.Message
    }

    Remove-Item -LiteralPath $approvalsDir -Recurse -Force -ErrorAction SilentlyContinue

    if ($afterRunFailures.Count -gt 0) {
        if ($smokeCompleted) { throw ($afterRunFailures -join [Environment]::NewLine) }
        foreach ($failure in $afterRunFailures) { Write-Warning $failure }
    }
}
