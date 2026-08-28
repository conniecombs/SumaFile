$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$payloadDir = Join-Path $root "dist\winui\payload"
$exePath = Join-Path $payloadDir "SumaFile.exe"
$servicePath = Join-Path $payloadDir "simplefile-service.exe"
$expectedTitle = "SumaFile"
$timeoutSeconds = 25

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

    $service = Get-Process -Name "simplefile-service" -ErrorAction SilentlyContinue
    if (-not $service) {
        throw "WinUI host started but simplefile-service.exe was not running."
    }

    Write-Host "WinUI startup smoke passed: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)'."
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
