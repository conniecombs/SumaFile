$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$bundleDir = Join-Path $root "dist\winui"

# Assert-BundledPlugins: the bundled plugin folders and bundled.json in the extracted image.
. (Join-Path $PSScriptRoot "lib\plugin-staging.ps1")
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
$extractedExeDir = $null

# The extracted app must never touch the user's data: it scans only the extracted plugins folder, keeps approvals,
# plugin data, the plugin log, metadata.db and app data under $smokeRoot, so the bundled remote plugin imports no
# real profiles and seeds nothing into the real settings.
$smokeAppDataDir = Join-Path $smokeRoot "app-data"
$smokeEnvironment = [ordered]@{
    SUMAFILE_PLUGIN_ROOTS = $null
    SUMAFILE_PLUGIN_APPROVALS_PATH = Join-Path $smokeRoot "plugin-approvals.json"
    SUMAFILE_PLUGIN_DATA_ROOT = Join-Path $smokeRoot "plugins-data"
    SUMAFILE_PLUGIN_LOG_PATH = Join-Path $smokeRoot "plugins.log"
    SIMPLEFILE_METADATA_DB = Join-Path $smokeAppDataDir "metadata.db"
    SIMPLEFILE_APP_DATA_DIR = $smokeAppDataDir
}
$previousEnvironment = @{}
foreach ($name in $smokeEnvironment.Keys) {
    $previousEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name, "Process")
}

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

    Assert-BundledPlugins -Root (Join-Path $exe.DirectoryName "plugins")
    if (Test-Path -LiteralPath (Join-Path $exe.DirectoryName "tools\7zip")) {
        throw "WinUI MSI extraction must not contain tools\7zip; 7-Zip ships as the bundled plugin."
    }
    Write-Host "Extracted MSI carries the bundled plugins."

    $extractedExeDir = $exe.DirectoryName
    New-Item -ItemType Directory -Force -Path $smokeAppDataDir | Out-Null
    $smokeEnvironment["SUMAFILE_PLUGIN_ROOTS"] = Join-Path $extractedExeDir "plugins"
    foreach ($name in $smokeEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $smokeEnvironment[$name], "Process")
    }
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

    foreach ($name in $smokeEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], "Process")
    }

    # The host's kill-on-close job ends plugin backends with the app; stop any that still run from the extracted
    # folder before removing it.
    if ($extractedExeDir) {
        $extractedPlugins = Join-Path $extractedExeDir "plugins"
        Get-Process -Name "sumafile-*-backend" -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($extractedPlugins, [System.StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                $_.WaitForExit(5000) | Out-Null
            }
    }

    Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
}
