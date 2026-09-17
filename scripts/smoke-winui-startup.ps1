$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$payloadDir = Join-Path $root "dist\winui\payload"
$exePath = Join-Path $payloadDir "SumaFile.exe"
$servicePath = Join-Path $payloadDir "simplefile-service.exe"
$expectedTitle = "SumaFile"
$timeoutSeconds = 25
$startupTimingLog = Join-Path $env:LOCALAPPDATA "SumaFile\startup-timing.log"

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

$process = Start-Process -FilePath $exePath -WorkingDirectory $payloadDir -PassThru
$windowProcess = $null

try {
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
}
finally {
    Get-Process -Name "simplefile-service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $startedProcess = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
    if ($startedProcess) {
        $closed = $startedProcess.CloseMainWindow()
        Start-Sleep -Seconds 2
        $startedProcess = Get-Process -Id $startedProcess.Id -ErrorAction SilentlyContinue
        if ($startedProcess) {
            Stop-Process -Id $startedProcess.Id -Force
        }
        Write-Host "Closed WinUI smoke-test process $($process.Id). CloseMainWindow sent: $closed."
    }
}
