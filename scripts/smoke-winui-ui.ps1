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
. (Join-Path $PSScriptRoot 'lib\credentials.ps1')
if ($PluginsDir -and $NoPlugins) { throw 'Use -PluginsDir or -NoPlugins, not both.' }

# One command-line argument for a Windows program: quoted when it holds whitespace or a quote, with the backslashes
# before a quote, and a trailing run of them, doubled (the CommandLineToArgvW rules git follows).
function ConvertTo-SmokeCommandLineArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', [System.Text.RegularExpressions.MatchEvaluator] { param($match) $match.Groups[1].Value + $match.Groups[1].Value + '\"' })
    $escaped = [regex]::Replace($escaped, '(\\+)$', [System.Text.RegularExpressions.MatchEvaluator] { param($match) $match.Groups[1].Value + $match.Groups[1].Value })
    return '"' + $escaped + '"'
}

# Runs git for the Git plugin fixture, bounded at 30 s, isolated as the app is: the smoke's empty global config, no
# system config, no fsmonitor, no prompts, and no repository walk out of the test root. Returns git's stdout.
function Invoke-SmokeGit {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $gitStart = New-Object System.Diagnostics.ProcessStartInfo
    $gitStart.FileName = $smokeGitExe
    $gitStart.WorkingDirectory = $WorkingDirectory
    $gitStart.Arguments = ((@('-c', 'core.fsmonitor=false') + $Arguments) | ForEach-Object { ConvertTo-SmokeCommandLineArgument -Value $_ }) -join ' '
    $gitStart.UseShellExecute = $false
    $gitStart.CreateNoWindow = $true
    $gitStart.RedirectStandardOutput = $true
    $gitStart.RedirectStandardError = $true
    foreach ($name in @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY')) {
        if ($gitStart.EnvironmentVariables.ContainsKey($name)) { $gitStart.EnvironmentVariables.Remove($name) }
    }
    $gitStart.EnvironmentVariables['GIT_CONFIG_GLOBAL'] = $smokeGitConfig
    $gitStart.EnvironmentVariables['GIT_CONFIG_NOSYSTEM'] = '1'
    $gitStart.EnvironmentVariables['GIT_CEILING_DIRECTORIES'] = $testRoot
    $gitStart.EnvironmentVariables['GIT_TERMINAL_PROMPT'] = '0'
    $line = 'git ' + ($Arguments -join ' ')
    $gitProcess = [System.Diagnostics.Process]::Start($gitStart)
    try {
        $stdout = $gitProcess.StandardOutput.ReadToEndAsync()
        $stderr = $gitProcess.StandardError.ReadToEndAsync()
        if (-not $gitProcess.WaitForExit(30000)) {
            try { $gitProcess.Kill() } catch { Write-Warning "Could not stop '$line': $($_.Exception.Message)" }
            throw "'$line' did not finish within 30 s in $WorkingDirectory."
        }
        $gitProcess.WaitForExit()
        if ($gitProcess.ExitCode -ne 0) { throw "'$line' failed in $WorkingDirectory (exit $($gitProcess.ExitCode)): $($stderr.Result.Trim())" }
        return $stdout.Result
    } finally { $gitProcess.Dispose() }
}
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
        & cargo build --locked -p simplefile-service -p sumafile-sevenzip-backend -p sumafile-remote-backend -p sumafile-git-backend -p sumafile-remote-test-server
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
# Every mode: git is isolated wherever the app's Git backend could run it, and the smoke's own git calls use the same
# empty config. It lives with the app's state.
$smokeGitConfig = Join-Path $testRoot 'state\gitconfig'
[System.IO.File]::WriteAllText($smokeGitConfig, '')
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
$gitRoot = $null
if ($PluginsDir -or $NoPlugins) {
    # The Git plugin's repositories under <testRoot>\git: a bare origin.git; repo, which pushed its first commit with
    # an upstream; a peer clone that pushed one more commit (so repo is behind by 1 and its pull is a fast-forward);
    # then repo's own changes, which it does not commit until the smoke has pulled; and not-a-repo. Every repository
    # is created with -b main and gets its identity, so no global init.defaultBranch, hook or signing applies.
    $gitCommands = @(Get-Command git -CommandType Application -ErrorAction SilentlyContinue)
    if ($gitCommands.Count -eq 0) { throw 'Git for Windows is required for the Git plugin smoke' }
    $smokeGitExe = $gitCommands[0].Path
    $gitRoot = Join-Path $testRoot 'git'
    $gitRepo = Join-Path $gitRoot 'repo'
    $gitOrigin = Join-Path $gitRoot 'origin.git'
    $gitPeer = Join-Path $gitRoot 'peer'
    New-Item -ItemType Directory -Path $gitRoot | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitRoot -Arguments @('init', '--bare', '-b', 'main', 'origin.git') | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitRoot -Arguments @('init', '-b', 'main', 'repo') | Out-Null
    $gitIdentity = @(
        @('config', 'user.name', 'SumaFile Smoke'),
        @('config', 'user.email', 'smoke@sumafile.invalid'),
        @('config', 'commit.gpgsign', 'false'),
        @('config', 'core.autocrlf', 'false'))
    foreach ($setting in $gitIdentity) { Invoke-SmokeGit -WorkingDirectory $gitRepo -Arguments $setting | Out-Null }
    $gitUnicodeFolder = 'caf' + [char]0x00E9 + ' ' + [char]0x8CC7 + [char]0x6599
    $gitFiles = [ordered]@{
        'tracked.txt' = "SumaFile Git smoke: tracked`n"
        ($gitUnicodeFolder + '\hello.txt') = "SumaFile Git smoke: hello`n"
        'literal [brackets] & spaces.txt' = "SumaFile Git smoke: literal`n"
        'discard-me.txt' = "SumaFile Git smoke: committed`n"
    }
    foreach ($relative in $gitFiles.Keys) {
        $target = Join-Path $gitRepo $relative
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [System.IO.File]::WriteAllText($target, $gitFiles[$relative])
    }
    Invoke-SmokeGit -WorkingDirectory $gitRepo -Arguments @('add', '-A') | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitRepo -Arguments @('commit', '-m', 'initial') | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitRepo -Arguments @('remote', 'add', 'origin', $gitOrigin) | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitRepo -Arguments @('push', '-u', 'origin', 'main') | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitRoot -Arguments @('clone', $gitOrigin, 'peer') | Out-Null
    foreach ($setting in $gitIdentity) { Invoke-SmokeGit -WorkingDirectory $gitPeer -Arguments $setting | Out-Null }
    [System.IO.File]::WriteAllText((Join-Path $gitPeer 'from-peer.txt'), "SumaFile Git smoke: from the peer`n")
    Invoke-SmokeGit -WorkingDirectory $gitPeer -Arguments @('add', '-A') | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitPeer -Arguments @('commit', '-m', 'from the peer') | Out-Null
    Invoke-SmokeGit -WorkingDirectory $gitPeer -Arguments @('push', 'origin', 'main') | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $gitRepo 'tracked.txt'), "SumaFile Git smoke: tracked, changed`n")
    [System.IO.File]::WriteAllText((Join-Path $gitRepo 'discard-me.txt'), "SumaFile Git smoke: changed, to be discarded`n")
    [System.IO.File]::WriteAllText((Join-Path $gitRepo 'new.txt'), "SumaFile Git smoke: untracked`n")
    [System.IO.File]::WriteAllText((Join-Path $gitRepo 'staged.txt'), "SumaFile Git smoke: staged`n")
    Invoke-SmokeGit -WorkingDirectory $gitRepo -Arguments @('add', '--', 'staged.txt') | Out-Null
    $gitNotARepo = Join-Path $gitRoot 'not-a-repo'
    New-Item -ItemType Directory -Path $gitNotARepo | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $gitNotARepo 'plain.txt'), "SumaFile Git smoke: not in a repository`n")
    $gitStatus = @((Invoke-SmokeGit -WorkingDirectory $gitRepo -Arguments @('status', '--porcelain=v1')) -split "`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($gitStatus.Count -ne 4) { throw "The Git fixture's repo has $($gitStatus.Count) changes, expected 4: $($gitStatus -join ' | ')" }
}
$remoteServerExe = $null
$remoteInfo = $null
$remoteLocal = $null
if ($PluginsDir) {
    # The FTP/SFTP plugin's files: <testRoot>\remote-server is served over SFTP, <testRoot>\remote-local is the
    # manager's local side. download\big.bin is the existing file the cancelled download must leave unchanged.
    $remoteServerExe = Join-Path $root 'target\debug\sumafile-remote-test-server.exe'
    if (-not (Test-Path -LiteralPath $remoteServerExe)) { throw "SFTP test server missing: $remoteServerExe. Run scripts/build-fixture-plugin.ps1 or run without -SkipBuild." }
    $remoteServerRoot = Join-Path $testRoot 'remote-server'
    $remoteUnicodeFolder = Join-Path $remoteServerRoot ('caf' + [char]0x00E9 + ' ' + [char]0x8CC7 + [char]0x6599)
    New-Item -ItemType Directory -Path $remoteUnicodeFolder -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $remoteUnicodeFolder 'hello.txt'), 'hello')
    [System.IO.File]::WriteAllText((Join-Path $remoteServerRoot 'literal [brackets] & spaces.txt'), 'literal')
    $remoteLocal = Join-Path $testRoot 'remote-local'
    New-Item -ItemType Directory -Path (Join-Path $remoteLocal 'download') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $remoteLocal 'upload.txt'), 'upload')
    $random = New-Object System.Random
    $buffer = New-Object byte[] (1MB)
    $stream = [System.IO.File]::Create((Join-Path $remoteServerRoot 'big.bin'))
    try {
        for ($index = 0; $index -lt 64; $index++) { $random.NextBytes($buffer); $stream.Write($buffer, 0, $buffer.Length) }
    } finally { $stream.Dispose() }
    $random.NextBytes($buffer)
    [System.IO.File]::WriteAllBytes((Join-Path $remoteLocal 'download\big.bin'), $buffer)
    $remoteInfo = Join-Path $testRoot 'remote-server.json'
    # Hello's row decorations: every row of hello-decorated is decorated at once, those of hello-slow after 600 ms.
    foreach ($relative in @('hello-decorated\a.txt', 'hello-decorated\b.txt', 'hello-slow\c.txt')) {
        $target = Join-Path $testRoot $relative
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        [System.IO.File]::WriteAllText($target, 'SumaFile decoration smoke')
    }
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
if ($gitRoot) { $start.EnvironmentVariables['SUMAFILE_SMOKE_GIT'] = $gitRoot }
# Every mode, plain included: the plain run stages the bundled plugins too. The Debug exe folder is this run's
# install folder, and plugin data, approvals and the plugin log default to it, so all three point into the test
# root and the smoke never writes into the Debug exe folder (checked after the run).
$installDir = Split-Path -Parent $exe
$start.EnvironmentVariables['SUMAFILE_PLUGIN_DATA_ROOT'] = Join-Path $testRoot 'plugins-data'
$start.EnvironmentVariables['SUMAFILE_PLUGIN_APPROVALS_PATH'] = Join-Path $testRoot 'state\plugin-approvals.json'
$start.EnvironmentVariables['SUMAFILE_PLUGIN_LOG_PATH'] = Join-Path $testRoot 'plugins.log'
# Every mode too: git is isolated wherever the app's Git backend could run it. test-results\ lies inside the SumaFile
# checkout, so git and the backend's repository walk stop at the test root and never find the checkout's own
# repository, and git reads none of the developer's global or system config (a core.hooksPath, commit.gpgsign or
# init.defaultBranch there could run, prompt or change a result). The empty config ($smokeGitConfig) lives with the
# app's state, and the fixture's own git calls above used it too.
$start.EnvironmentVariables['GIT_CEILING_DIRECTORIES'] = $testRoot
$start.EnvironmentVariables['GIT_CONFIG_GLOBAL'] = $smokeGitConfig
$start.EnvironmentVariables['GIT_CONFIG_NOSYSTEM'] = '1'
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
    $start.EnvironmentVariables['SUMAFILE_SMOKE_PLUGINS'] = 'none'
}
$remoteServer = $null
$process = $null
$smokeCompleted = $false
# Taken before the launch and checked after it: the app never touches the pre-release plugin locations under
# %LOCALAPPDATA%\SumaFile, and writes no app-owned plugin file into the Debug exe folder.
$legacyPluginLocations = Get-LegacyPluginLocationSnapshot
$installAppOwnedState = Get-AppOwnedPluginState -InstallDir $installDir
# The remote scenario saves a password in Credential Manager. The App deletes it itself, but not when it crashes or
# is killed, so the targets present before the launch are recorded and the smoke profile's own target is removed below.
$remoteCredentialPrefix = 'SumaFile.Remote.'
$credentialBaseline = Get-GenericCredentialTargets -Prefix $remoteCredentialPrefix
$remoteCredentialFile = Join-Path $testRoot 'remote-credential.txt'
try {
    if ($remoteServerExe) {
        # Before the App starts: the server writes a 1.0.4 metadata database holding its 'Legacy SFTP' profile where the
        # App's service opens its settings database (SIMPLEFILE_METADATA_DB), and the remote backend imports that profile.
        $serverStart = New-Object System.Diagnostics.ProcessStartInfo
        $serverStart.FileName = $remoteServerExe
        $serverStart.Arguments = '--root "{0}" --info "{1}" --legacy-db "{2}"' -f $remoteServerRoot, $remoteInfo, (Join-Path $testRoot 'state\metadata.db')
        $serverStart.UseShellExecute = $false
        $serverStart.CreateNoWindow = $true
        $remoteServer = [System.Diagnostics.Process]::Start($serverStart)
        $waited = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $remoteInfo)) {
            if ($remoteServer.HasExited) { throw "The SFTP test server exited ($($remoteServer.ExitCode)) before writing $remoteInfo." }
            if ($waited.Elapsed.TotalSeconds -gt 30) { throw "The SFTP test server did not write $remoteInfo within 30 s." }
            Start-Sleep -Milliseconds 100
        }
        $start.EnvironmentVariables['SUMAFILE_SMOKE_REMOTE'] = $remoteInfo
        $start.EnvironmentVariables['SUMAFILE_SMOKE_REMOTE_LOCAL'] = $remoteLocal
    }
    $process = [System.Diagnostics.Process]::Start($start)
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
    $smokeCompleted = $true
} finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
    if ($remoteServer) {
        if (-not $remoteServer.HasExited) { $remoteServer.Kill(); [void]$remoteServer.WaitForExit(5000) }
        $remoteServer.Dispose()
    }
    # Once the app is gone. A failure here fails a smoke that otherwise passed; after an earlier failure it is only
    # reported, so the first error stays the one thrown.
    $afterRunFailures = @()
    foreach ($check in @(
            { Assert-LegacyPluginLocationsUnchanged -Before $legacyPluginLocations },
            { Assert-AppOwnedPluginStateUnchanged -InstallDir $installDir -Before $installAppOwnedState },
            {
                # The target the App wrote right after saving its smoke profile is removed when it is still there and
                # new. Any other new SumaFile.Remote.* target fails the run and is left alone: it is not the smoke's.
                $smokeTarget = $null
                if (Test-Path -LiteralPath $remoteCredentialFile) {
                    $smokeTarget = ([System.IO.File]::ReadAllText($remoteCredentialFile)).Trim()
                    if (-not $smokeTarget.StartsWith($remoteCredentialPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "$remoteCredentialFile names '$smokeTarget', which is not a $remoteCredentialPrefix* target; nothing was deleted."
                    }
                }
                $current = Get-GenericCredentialTargets -Prefix $remoteCredentialPrefix
                $gained = @($current | Where-Object { $credentialBaseline -notcontains $_ })
                if ($smokeTarget -and ($gained -contains $smokeTarget)) {
                    Remove-GenericCredential -Target $smokeTarget
                    Write-Host "Removed the smoke profile's credential $smokeTarget, which the App left behind."
                }
                $stray = @($gained | Where-Object { $_ -ne $smokeTarget })
                if ($stray.Count -gt 0) {
                    throw "The UI smoke left Credential Manager targets it cannot attribute to its own profile (not deleted): $($stray -join ', ')"
                }
            })) {
        try { & $check } catch { $afterRunFailures += $_.Exception.Message }
    }
    if ($afterRunFailures.Count -gt 0) {
        if ($smokeCompleted) { throw (($afterRunFailures -join [Environment]::NewLine) + "`nEvidence: $testRoot") }
        foreach ($failure in $afterRunFailures) { Write-Warning "After the failed run: $failure" }
    }
}
