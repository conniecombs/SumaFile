$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$bundleDir = Join-Path $root "dist\winui"
$msi = Get-ChildItem -Path $bundleDir -Filter "SumaFile_*_x64-winui.msi" -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$expectedTitle = "SumaFile"
$timeoutSeconds = 25

if (-not $msi) {
    throw "No WinUI MSI found in $bundleDir. Run 'npm run build:winui:release' first."
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-winui-msi-smoke-" + [System.Guid]::NewGuid().ToString("N"))
$extractDir = Join-Path $smokeRoot "extract"
New-Item -ItemType Directory -Force -Path $extractDir | Out-Null

$process = $null

try {
    $msiArgs = @("/a", $msi.FullName, "/qn", "TARGETDIR=$extractDir")
    $msiexec = Start-Process -FilePath "msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru
    if ($msiexec.ExitCode -ne 0) {
        throw "WinUI MSI administrative extraction failed with exit code $($msiexec.ExitCode)."
    }

    $exe = Get-ChildItem -Path $extractDir -Filter "SumaFile.exe" -Recurse -File |
        Select-Object -First 1
    if (-not $exe) {
        throw "WinUI MSI extraction did not contain SumaFile.exe under $extractDir."
    }

    $service = Get-ChildItem -Path $extractDir -Filter "simplefile-service.exe" -Recurse -File |
        Select-Object -First 1
    if (-not $service) {
        throw "WinUI MSI extraction did not contain simplefile-service.exe."
    }

    Write-Host "Extracted $($msi.Name) to $extractDir."
    Write-Host "Extracted executable version: $($exe.VersionInfo.ProductVersion)."

    $process = Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
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
        throw "Extracted WinUI executable did not expose '$expectedTitle' within $timeoutSeconds seconds. Last title: '$lastTitle'."
    }

    Write-Host "WinUI MSI artifact smoke passed: PID $($windowProcess.Id), title '$($windowProcess.MainWindowTitle)'."
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
            Write-Host "Closed WinUI MSI smoke-test process $($process.Id). CloseMainWindow sent: $closed."
        }
    }

    Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
