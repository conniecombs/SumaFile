#requires -Version 5.1
[CmdletBinding()]
param(
    [int[]]$Counts = @(10000, 100000),
    [switch]$SkipBuild,
    [switch]$Baseline,
    [ValidateRange(1000, 3840)]
    [int]$MaxWindowWidth = 3840,
    [ValidateRange(1, 7200)]
    [int]$TimeoutSeconds = 600,
    # A plugin folder (holding plugin.json) or a roots folder of plugin folders; each is pre-approved.
    [string]$PluginsDir,
    [switch]$NoPlugins
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'lib\plugin-staging.ps1')
if ($PluginsDir -and $NoPlugins) { throw 'Use -PluginsDir or -NoPlugins, not both.' }
$pluginRoots = $null
if ($PluginsDir) {
    $pluginsPath = if ([System.IO.Path]::IsPathRooted($PluginsDir)) { $PluginsDir } else { Join-Path $root $PluginsDir }
    if (-not (Test-Path -LiteralPath $pluginsPath -PathType Container)) { throw "Plugins folder not found: $pluginsPath. Run scripts/build-fixture-plugin.ps1." }
    $pluginsPath = (Resolve-Path -LiteralPath $pluginsPath).Path
    $pluginRoots = if (Test-Path -LiteralPath (Join-Path $pluginsPath 'plugin.json')) { Split-Path -Parent $pluginsPath } else { $pluginsPath }
    $pluginFolders = @(Get-ChildItem -LiteralPath $pluginRoots -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'plugin.json') })
    if ($pluginFolders.Count -eq 0) { throw "No plugin folders with plugin.json under $pluginRoots." }
}
if (-not $SkipBuild) {
    Push-Location -LiteralPath $root
    try {
        & cargo build --locked -p simplefile-service -p sumafile-sevenzip-backend -p sumafile-remote-backend -p sumafile-remote-test-server
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }
        # Restage target\plugins from the fresh target\debug output; the App's Debug build copies it into the exe folder.
        & (Join-Path $PSScriptRoot 'build-plugins.ps1') -SkipCargo | Out-Host
        & npm run build:winui
        if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    } finally { Pop-Location }
}
$exe = Join-Path $root 'src-winui\SimpleFile.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\SumaFile.exe'
$service = Join-Path $root 'target\debug\simplefile-service.exe'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $service)) { throw 'Debug app/service missing. Run without -SkipBuild.' }
# build:winui copies whatever target\plugins holds, so -SkipBuild could otherwise test an old bundled plugin.
$bundledPluginsRoot = Join-Path (Split-Path -Parent $exe) 'plugins'
if (Test-Path -LiteralPath (Join-Path $bundledPluginsRoot 'bundled.json')) {
    Assert-BundledPluginsFresh -Root $bundledPluginsRoot -Configuration Debug
} elseif ($PluginsDir) {
    # The plugin-mode smoke always checks the bundled plugins; they must never be skipped silently.
    throw "Bundled plugins missing from $bundledPluginsRoot. Run scripts/build-plugins.ps1 and rebuild."
}
$sevenZa = Join-Path $root 'third_party\7zip\win-x64\7za.exe'
if (($PluginsDir -or $NoPlugins) -and -not (Test-Path -LiteralPath $sevenZa)) { throw "7-Zip is missing: $sevenZa" }
$testRoot = Join-Path $root ('test-results\ui-smoke\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $testRoot | Out-Null
[System.IO.File]::WriteAllText((Join-Path $testRoot '.sumafile-ui-smoke'), 'disposable UI test data')
foreach ($name in @('empty', 'destination', 'state')) { New-Item -ItemType Directory -Path (Join-Path $testRoot $name) | Out-Null }
$png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/l9sAAAAASUVORK5CYII=')
foreach ($count in $Counts) {
    if ($count -lt 1000 -or $count -gt 100000) { throw 'Counts must be between 1,000 and 100,000.' }
    $folder = Join-Path $testRoot "fixtures\$count"
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    for ($index = 0; $index -lt $count; $index++) {
        $name = 'file-{0:D6}' -f $index
        if ($index % 100 -eq 0) { [System.IO.File]::WriteAllBytes((Join-Path $folder ($name + '.png')), $png) }
        else { [System.IO.File]::WriteAllText((Join-Path $folder ($name + '.txt')), 'SumaFile runtime fixture') }
    }
}
$archivesRoot = $null
if ($PluginsDir -or $NoPlugins) {
    # sample.7z for the 7-Zip checks, made with the bundled 7za from <archives>\fixture. The names are built from
    # code points so the script stays ASCII for Windows PowerShell 5.1.
    $archivesRoot = Join-Path $testRoot 'archives'
    $archiveFixture = Join-Path $archivesRoot 'fixture'
    $unicodeFolder = 'caf' + [char]0x00E9 + ' ' + [char]0x8CC7 + [char]0x6599
    $fixtureFiles = [ordered]@{
        'docs\a.txt' = 'SumaFile 7-Zip smoke: docs/a.txt'
        ($unicodeFolder + '\hello.txt') = 'SumaFile 7-Zip smoke: ' + $unicodeFolder + '/hello.txt'
        'literal [brackets] & spaces.txt' = 'SumaFile 7-Zip smoke: literal [brackets] & spaces'
    }
    foreach ($relative in $fixtureFiles.Keys) {
        $target = Join-Path $archiveFixture $relative
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [System.IO.File]::WriteAllText($target, $fixtureFiles[$relative])
    }
    Push-Location -LiteralPath $archiveFixture
    try {
        & $sevenZa a -t7z (Join-Path $archivesRoot 'sample.7z') '*' -bso0 -bsp0
        if ($LASTEXITCODE -ne 0) { throw "7za could not create sample.7z (exit $LASTEXITCODE)." }
    } finally { Pop-Location }
}
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $exe
$start.WorkingDirectory = Split-Path -Parent $exe
$start.UseShellExecute = $false
$start.EnvironmentVariables['SIMPLEFILE_UI_SMOKE_ROOT'] = $testRoot
$start.EnvironmentVariables['SIMPLEFILE_UI_SMOKE_BASELINE'] = $(if ($Baseline) { '1' } else { '0' })
$start.EnvironmentVariables['SIMPLEFILE_UI_SMOKE_MAX_WIDTH'] = [string]$MaxWindowWidth
$start.EnvironmentVariables['SIMPLEFILE_SERVICE_PATH'] = $service
$start.EnvironmentVariables['SIMPLEFILE_APP_DATA_DIR'] = Join-Path $testRoot 'state'
$start.EnvironmentVariables['SIMPLEFILE_METADATA_DB'] = Join-Path $testRoot 'state\metadata.db'
if ($archivesRoot) { $start.EnvironmentVariables['SUMAFILE_SMOKE_ARCHIVES'] = $archivesRoot }
# Every mode, plain included: the plain run stages the bundled plugins too, and a plugin backend writes its data
# under this root, so it must never be the real %LOCALAPPDATA%\SumaFile\plugins-data.
$start.EnvironmentVariables['SUMAFILE_PLUGIN_DATA_ROOT'] = Join-Path $testRoot 'plugins-data'
if ($PluginsDir) {
    # Same shape PluginApprovalStore writes (string enums, PascalCase, the plugin's full folder path), at a disposable path, so no consent dialog appears.
    $approvals = [ordered]@{}
    foreach ($folder in $pluginFolders) {
        # .NET directly: Get-FileHash is missing when Windows PowerShell inherits a pwsh PSModulePath.
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $digest = $sha.ComputeHash([System.IO.File]::ReadAllBytes((Join-Path $folder.FullName 'plugin.json'))) } finally { $sha.Dispose() }
        $hash = ([System.BitConverter]::ToString($digest) -replace '-', '').ToLowerInvariant()
        $approvals[$folder.Name] = [ordered]@{ ManifestHash = $hash; SignatureStatus = 'Unsigned'; State = 'Enabled'; UpdatedAt = [DateTimeOffset]::UtcNow.ToString('o'); Folder = $folder.FullName }
    }
    $approvalsPath = Join-Path $testRoot 'state\plugin-approvals.json'
    [System.IO.File]::WriteAllText($approvalsPath, ($approvals | ConvertTo-Json -Depth 4))
    $markerDir = Join-Path $testRoot 'hello-markers'
    New-Item -ItemType Directory -Path $markerDir | Out-Null
    # The install root first, as the app scans it. Only the -PluginsDir folders are pre-approved above, so the
    # bundled plugins are trusted through <exe dir>\plugins\bundled.json alone.
    $start.EnvironmentVariables['SUMAFILE_PLUGIN_ROOTS'] = "$bundledPluginsRoot;$pluginRoots"
    $start.EnvironmentVariables['SUMAFILE_PLUGIN_APPROVALS_PATH'] = $approvalsPath
    $start.EnvironmentVariables['SUMAFILE_HELLO_MARKER_DIR'] = $markerDir
    $start.EnvironmentVariables['SUMAFILE_SMOKE_PLUGINS'] = 'with'
} elseif ($NoPlugins) {
    $emptyRoots = Join-Path $testRoot 'no-plugins'
    New-Item -ItemType Directory -Path $emptyRoots | Out-Null
    $start.EnvironmentVariables['SUMAFILE_PLUGIN_ROOTS'] = $emptyRoots
    $start.EnvironmentVariables['SUMAFILE_PLUGIN_APPROVALS_PATH'] = Join-Path $testRoot 'state\plugin-approvals.json'
    $start.EnvironmentVariables['SUMAFILE_SMOKE_PLUGINS'] = 'none'
}
$process = [System.Diagnostics.Process]::Start($start)
try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $progressPath = Join-Path $testRoot 'progress.json'
        if (Test-Path -LiteralPath $progressPath) {
            Write-Host 'Last UI smoke checkpoint (completed scenarios and active stage):'
            Get-Content -LiteralPath $progressPath -Raw | Write-Host
        }
        throw "UI smoke timed out. Evidence: $testRoot"
    }
    $reportPath = Join-Path $testRoot 'report.json'
    if (-not (Test-Path -LiteralPath $reportPath)) { throw "App exited ($($process.ExitCode)) without a report. Evidence: $testRoot" }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw "UI smoke failed: $($report.failure)`nEvidence: $reportPath" }
    if ($process.ExitCode -ne 0) { throw "App exited with code $($process.ExitCode) after reporting results. Evidence: $reportPath" }
    Write-Host "UI runtime smoke passed. Report: $reportPath"
    $report.results | Where-Object { $_.scenario -eq 'large-folder' } | Format-Table -AutoSize | Out-Host
    Write-Host ("Completed scenarios: " + (($report.results | ForEach-Object { $_.scenario } | Group-Object | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', '))
    $report.uiHeartbeat | Format-List | Out-Host
} finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}
