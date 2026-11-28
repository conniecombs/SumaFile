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
    [switch]$NoPlugins,
    # Keep test-results\ui-smoke\<run> after a passing run too. A failed run always keeps it.
    [switch]$KeepResults,
    # Also make the gestures that need a real mouse (double-click, right-click, drag and drop). This moves the
    # pointer and presses its buttons for a few seconds, so leave the mouse alone while it runs.
    [switch]$PointerInput
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'lib\plugin-staging.ps1')
. (Join-Path $PSScriptRoot 'lib\credentials.ps1')
# ConvertTo-NativeArgument and Invoke-GitProcess.
. (Join-Path $PSScriptRoot 'lib\smoke-common.ps1')
if ($PluginsDir -and $NoPlugins) { throw 'Use -PluginsDir or -NoPlugins, not both.' }

# git for the Git plugin's fixtures: the smoke's own configuration, no repository found above the test root, and
# none taken from the environment this script was started in.
function Invoke-SmokeGit {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    Invoke-GitProcess -GitExe $smokeGitExe -WorkingDirectory $WorkingDirectory -Arguments (@('-c', 'core.fsmonitor=false') + $Arguments) -Environment @{
        GIT_DIR                 = $null
        GIT_WORK_TREE           = $null
        GIT_INDEX_FILE          = $null
        GIT_OBJECT_DIRECTORY    = $null
        GIT_CONFIG_GLOBAL       = $smokeGitConfig
        GIT_CONFIG_NOSYSTEM     = '1'
        GIT_CEILING_DIRECTORIES = $testRoot
        GIT_TERMINAL_PROMPT     = '0'
    }
}

# A run folder's name: test-results\ui-smoke\<UTC yyyyMMdd-HHmmss>-<8 hex>.
$UiSmokeRunNamePattern = '^\d{8}-\d{6}-[0-9a-f]{8}$'

# Deletes a passed run's folder, and only one this script made: directly under the results root, named like a run,
# holding the .sumafile-ui-smoke marker written when it was created, with no link anywhere under it (a junction's
# target is never entered or deleted). Returns $true when the folder is gone; otherwise warns and keeps it.
function Remove-UiSmokeRunFolder {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string]$ResultsRoot
    )
    $separators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $results = [System.IO.Path]::GetFullPath($ResultsRoot).TrimEnd($separators)
    $run = [System.IO.Path]::GetFullPath($RunRoot).TrimEnd($separators)
    if (-not [string]::Equals([System.IO.Path]::GetDirectoryName($run), $results, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "Kept $run, which is not directly under $results."
        return $false
    }
    if ((Split-Path -Leaf $run) -notmatch $UiSmokeRunNamePattern) {
        Write-Warning "Kept $run, whose name is not a UI smoke run's."
        return $false
    }
    if (-not (Test-Path -LiteralPath (Join-Path $run '.sumafile-ui-smoke') -PathType Leaf)) {
        Write-Warning "Kept $run, which has no .sumafile-ui-smoke marker."
        return $false
    }
    # The link checks, the walk and the read-only reset can throw (a folder that cannot be listed, a file whose
    # attributes cannot be changed): that keeps the folder with a warning, and never fails a smoke that passed.
    try {
        foreach ($folder in @($results, $run)) {
            if (([System.IO.File]::GetAttributes($folder) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                Write-Warning "Kept $run, because $folder is a link."
                return $false
            }
        }
        # One walk that never follows a link: any link under the run folder keeps the whole folder. Git object files
        # are read-only, which Directory.Delete refuses, so they are made writable first.
        $readOnly = New-Object System.Collections.Generic.List[System.IO.FileInfo]
        $pending = New-Object System.Collections.Generic.Stack[System.IO.DirectoryInfo]
        $pending.Push((New-Object System.IO.DirectoryInfo $run))
        while ($pending.Count -gt 0) {
            foreach ($entry in $pending.Pop().EnumerateFileSystemInfos()) {
                if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                    Write-Warning "Kept $run, because $($entry.FullName) is a link."
                    return $false
                }
                if ($entry -is [System.IO.DirectoryInfo]) { $pending.Push($entry) }
                elseif ($entry.IsReadOnly) { $readOnly.Add($entry) }
            }
        }
        foreach ($file in $readOnly) { $file.IsReadOnly = $false }
    } catch {
        Write-Warning "Kept $run, which could not be prepared for deletion: $($_.Exception.Message)"
        return $false
    }
    # A handle closing late (an indexer, an antivirus scan) can fail the first attempt.
    for ($attempt = 1; ; $attempt++) {
        try {
            [System.IO.Directory]::Delete($run, $true)
            return $true
        } catch {
            if ($attempt -ge 5) {
                Write-Warning "Kept $run, which could not be deleted: $($_.Exception.Message)"
                return $false
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

# The passed report and screenshots live beside the run folders: test-results\ui-smoke\last-passed-report.json and
# test-results\ui-smoke\last-passed\*-polish.png. A run first deletes the previous run's, so a failed run never
# leaves an older pass's report or screenshots looking like its own. Only those files: the folder and anything else in
# it stay, and nothing is deleted through a link.
function Clear-UiSmokeLastPassed {
    param([Parameter(Mandatory = $true)][string]$ResultsRoot)
    if (-not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) { return }
    $report = Join-Path $ResultsRoot 'last-passed-report.json'
    if (Test-Path -LiteralPath $report -PathType Leaf) { [System.IO.File]::Delete($report) }
    $shots = Join-Path $ResultsRoot 'last-passed'
    if (-not (Test-Path -LiteralPath $shots -PathType Container)) { return }
    if (([System.IO.File]::GetAttributes($shots) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$shots is a link; remove it and rerun."
    }
    foreach ($shot in @(Get-ChildItem -LiteralPath $shots -Filter '*-polish.png' -File)) {
        if (($shot.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) { [System.IO.File]::Delete($shot.FullName) }
    }
}

# Copies the polish screenshots the run produced (workspace-, conflict- and rename-polish.png, and with plugins
# remote-manager-polish.png; none for -Baseline)
# from the top of the run folder into Destination, so they outlive the folder. $false, with a warning, when a copy
# fails: the caller then keeps the run folder, which still holds them.
function Copy-UiSmokePassedScreenshots {
    param(
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    try {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        foreach ($shot in @(Get-ChildItem -LiteralPath $RunRoot -Filter '*-polish.png' -File)) {
            Copy-Item -LiteralPath $shot.FullName -Destination (Join-Path $Destination $shot.Name) -Force
        }
        return $true
    } catch {
        Write-Warning "Could not copy the screenshots of $RunRoot to ${Destination}: $($_.Exception.Message)"
        return $false
    }
}

# Every process: id, parent id, name, executable, command line and creation time (local). System.Management through
# .NET directly: CimCmdlets, like Get-FileHash, can be missing when Windows PowerShell inherits a pwsh PSModulePath.
function Get-UiSmokeProcessTable {
    [void][System.Reflection.Assembly]::Load('System.Management, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a')
    $searcher = New-Object System.Management.ManagementObjectSearcher 'SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine, CreationDate FROM Win32_Process'
    try {
        foreach ($row in $searcher.Get()) {
            $created = $null
            if ($row['CreationDate']) { $created = [System.Management.ManagementDateTimeConverter]::ToDateTime([string]$row['CreationDate']) }
            [pscustomobject]@{
                ProcessId       = [UInt32]$row['ProcessId']
                ParentProcessId = [UInt32]$row['ParentProcessId']
                Name            = [string]$row['Name']
                ExecutablePath  = [string]$row['ExecutablePath']
                CommandLine     = [string]$row['CommandLine']
                CreationDate    = $created
            }
        }
    } finally { $searcher.Dispose() }
}

# The processes of this run still alive: the app's and the SFTP server's process trees (by parent id), anything
# started from the app, service, media and video process, plugin or 7-Zip executables, and anything whose command line names
# the run folder, among the processes created since the launch.
function Get-UiSmokeHelperProcesses {
    param(
        [int[]]$RootProcessIds = @(),
        [Parameter(Mandatory = $true)][DateTime]$Since,
        [string[]]$Executables = @(),
        [Parameter(Mandatory = $true)][string]$RunRoot
    )
    $recent = @(Get-UiSmokeProcessTable | Where-Object {
            $_.ProcessId -ne $PID -and $null -ne $_.CreationDate -and $_.CreationDate -ge $Since })
    $tree = New-Object 'System.Collections.Generic.HashSet[UInt32]'
    foreach ($id in $RootProcessIds) { [void]$tree.Add([UInt32]$id) }
    do {
        $grown = $false
        foreach ($candidate in $recent) {
            if ($tree.Contains([UInt32]$candidate.ParentProcessId) -and $tree.Add([UInt32]$candidate.ProcessId)) { $grown = $true }
        }
    } while ($grown)
    foreach ($candidate in $recent) {
        $match = $tree.Contains([UInt32]$candidate.ProcessId)
        if (-not $match -and $candidate.ExecutablePath) {
            foreach ($executable in $Executables) {
                $folderPrefix = $executable.TrimEnd('\') + '\'
                if ([string]::Equals($candidate.ExecutablePath, $executable, [StringComparison]::OrdinalIgnoreCase) -or
                    $candidate.ExecutablePath.StartsWith($folderPrefix, [StringComparison]::OrdinalIgnoreCase)) { $match = $true; break }
            }
        }
        if (-not $match -and $candidate.CommandLine -and $candidate.CommandLine.IndexOf($RunRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $match = $true
        }
        if ($match) { $candidate }
    }
}

# Waits up to $TimeoutSeconds for every helper process to exit, so none holds a file in the run folder. $false, with
# a warning naming them, when some are still running or the process list cannot be read.
function Wait-UiSmokeHelperProcessesGone {
    param(
        [int[]]$RootProcessIds = @(),
        [Parameter(Mandatory = $true)][DateTime]$Since,
        [string[]]$Executables = @(),
        [Parameter(Mandatory = $true)][string]$RunRoot,
        [int]$TimeoutSeconds = 30
    )
    $waited = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        try {
            $left = @(Get-UiSmokeHelperProcesses -RootProcessIds $RootProcessIds -Since $Since -Executables $Executables -RunRoot $RunRoot)
        } catch {
            Write-Warning "Could not list the run's helper processes: $($_.Exception.Message)"
            return $false
        }
        if ($left.Count -eq 0) { return $true }
        if ($waited.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            Write-Warning ("Helper processes still running $TimeoutSeconds s after the app exited: " + (($left | ForEach-Object { "$($_.Name) ($($_.ProcessId))" }) -join ', '))
            return $false
        }
        Start-Sleep -Milliseconds 500
    }
}

# Runs git for the Git plugin fixture, bounded at 30 s, isolated as the app is: the smoke's empty global config, no
# system config, no fsmonitor, no prompts, and no repository walk out of the test root. Returns git's stdout.
function Write-SmokeExifEntry {
    param($Writer, [int]$Tag, [int]$Type, [int]$Value)
    $Writer.Write([uint16]$Tag); $Writer.Write([uint16]$Type); $Writer.Write([uint32]1); $Writer.Write([uint32]$Value)
}

# An Exif APP1 segment as cameras write it (little-endian TIFF): IFD0 carries the orientation and, with a preview,
# IFD1 carries that JPEG as the embedded thumbnail (JPEGInterchangeFormat 0x0201 and its length 0x0202).
function New-SmokeExifSegment {
    param([int]$Orientation, [byte[]]$Preview)
    $tiff = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $tiff
    $writer.Write([byte[]](0x49, 0x49, 0x2A, 0x00)); $writer.Write([uint32]8)
    $ifd1Offset = 8 + 2 + 12 + 4
    $writer.Write([uint16]1)
    Write-SmokeExifEntry $writer 0x0112 3 $Orientation
    if ($Preview) { $writer.Write([uint32]$ifd1Offset) } else { $writer.Write([uint32]0) }
    if ($Preview) {
        $dataOffset = $ifd1Offset + 2 + 2 * 12 + 4
        $writer.Write([uint16]2)
        Write-SmokeExifEntry $writer 0x0201 4 $dataOffset
        Write-SmokeExifEntry $writer 0x0202 4 $Preview.Length
        $writer.Write([uint32]0)
        $writer.Write($Preview)
    }
    $writer.Flush()
    $body = $tiff.ToArray()
    $writer.Dispose()
    $length = 2 + 6 + $body.Length
    $segment = New-Object System.IO.MemoryStream
    $header = [byte[]](0xFF, 0xE1, [byte](($length -shr 8) -band 0xFF), [byte]($length -band 0xFF), 0x45, 0x78, 0x69, 0x66, 0x00, 0x00)
    $segment.Write($header, 0, $header.Length)
    $segment.Write($body, 0, $body.Length)
    return ,$segment.ToArray()
}

function Get-SmokeJpegBytes {
    param([System.Drawing.Image]$Image)
    $stream = New-Object System.IO.MemoryStream
    $Image.Save($stream, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    return ,$bytes
}

# A folder of camera-like photos for the thumbnail scenario: 1600x1200 JPEGs, every other one tagged Exif orientation
# 6 (a portrait photo stored as the sensor saw it, named -portrait), two of every three with the 160x120 preview
# cameras embed (the third, -plain, has none and is decoded at reduced size). The picture is off-centre so a turned
# thumbnail differs from an unturned one.
function New-SmokePhotoFixture {
    param([Parameter(Mandatory = $true)][string]$Folder, [int]$Count = 96)
    Add-Type -AssemblyName System.Drawing
    New-Item -ItemType Directory -Path $Folder -Force | Out-Null
    $picture = New-Object System.Drawing.Bitmap 1600, 1200
    $graphics = [System.Drawing.Graphics]::FromImage($picture)
    $graphics.Clear([System.Drawing.Color]::SteelBlue)
    $graphics.FillEllipse([System.Drawing.Brushes]::Goldenrod, 150, 120, 600, 600)
    $graphics.FillRectangle([System.Drawing.Brushes]::Firebrick, 1100, 800, 400, 300)
    $graphics.Dispose()
    $preview = New-Object System.Drawing.Bitmap $picture, 160, 120
    $pictureBytes = Get-SmokeJpegBytes -Image $picture
    $previewBytes = Get-SmokeJpegBytes -Image $preview
    $picture.Dispose()
    $preview.Dispose()
    if ($pictureBytes[0] -ne 0xFF -or $pictureBytes[1] -ne 0xD8) { throw 'System.Drawing did not produce a JPEG.' }
    for ($index = 0; $index -lt $Count; $index++) {
        $portrait = ($index % 2) -eq 1
        $withPreview = ($index % 3) -ne 2
        $kind = 'landscape'
        $orientation = 1
        if ($portrait) { $kind = 'portrait'; $orientation = 6 }
        if (-not $withPreview) { $kind += '-plain' }
        if ($withPreview) { $exif = New-SmokeExifSegment -Orientation $orientation -Preview $previewBytes }
        else { $exif = New-SmokeExifSegment -Orientation $orientation -Preview $null }
        $file = New-Object System.IO.MemoryStream
        $file.Write($pictureBytes, 0, 2)
        $file.Write($exif, 0, $exif.Length)
        $file.Write($pictureBytes, 2, $pictureBytes.Length - 2)
        [System.IO.File]::WriteAllBytes((Join-Path $Folder ('photo-{0:D3}-{1}.jpg' -f $index, $kind)), $file.ToArray())
        $file.Dispose()
    }
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
        # The service's media process, built on its own as the release build does. It lands beside the service,
        # which looks for it next to its own executable.
        & cargo build --locked -p sumafile-media --bin sumafile-media
        if ($LASTEXITCODE -ne 0) { throw 'Media process build failed.' }
        # The service's video process, built optimised, as it ships. The video thumbnail scenario times it against
        # the app's old path, which runs in Windows' own optimised code: an unoptimised build would lose that
        # comparison for a reason no user would ever meet. The service is pointed at it below.
        & cargo build --locked --release -p sumafile-video --bin sumafile-video
        if ($LASTEXITCODE -ne 0) { throw 'Video process build failed.' }
        # The tool that writes the test videos for that scenario.
        & cargo build --locked -p sumafile-video --example write-test-avi
        if ($LASTEXITCODE -ne 0) { throw 'Test video writer build failed.' }
        # Restage target\plugins from the fresh target\debug output; the App's Debug build copies it into the exe folder.
        & (Join-Path $PSScriptRoot 'build-plugins.ps1') -SkipCargo | Out-Host
        & npm run build:winui
        if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    } finally { Pop-Location }
}
$exe = Join-Path $root 'src-winui\SimpleFile.App\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\SumaFile.exe'
$service = Join-Path $root 'target\debug\simplefile-service.exe'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $service)) { throw 'Debug app/service missing. Run without -SkipBuild.' }
# Checked because nothing else would say so: without the media process the service runs, and the picture formats it
# decodes quietly fall back to Windows.
$media = Join-Path (Split-Path -Parent $service) 'sumafile-media.exe'
if (-not (Test-Path -LiteralPath $media)) { throw "sumafile-media.exe is missing beside $service. Run without -SkipBuild." }
# The same for the video process: without it, video thumbnails quietly fall back to Windows' own. It is the
# release build (see above), so it is not beside the debug service; SUMAFILE_VIDEO_HELPER tells the service where.
$video = Join-Path $root 'target\release\sumafile-video.exe'
if (-not (Test-Path -LiteralPath $video)) { throw "sumafile-video.exe is missing: $video. Run without -SkipBuild." }
$aviWriter = Join-Path $root 'target\debug\examples\write-test-avi.exe'
if (-not (Test-Path -LiteralPath $aviWriter)) { throw "write-test-avi.exe is missing: $aviWriter. Run without -SkipBuild." }
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
$uiSmokeRoot = Join-Path $root 'test-results\ui-smoke'
$passedShotsRoot = Join-Path $uiSmokeRoot 'last-passed'
Clear-UiSmokeLastPassed -ResultsRoot $uiSmokeRoot
$runName = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$testRoot = Join-Path $uiSmokeRoot $runName
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
New-SmokePhotoFixture -Folder (Join-Path $testRoot 'photos')
# Videos for the video thumbnail scenario: small and uncompressed, so they need no codec and the result does not
# depend on what this machine has installed (see crates\sumafile-video\tests\support\avi.rs).
& $aviWriter (Join-Path $testRoot 'videos') 24 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The test videos could not be written.' }
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
$start.EnvironmentVariables['SUMAFILE_SMOKE_POINTER'] = $(if ($PointerInput) { '1' } else { '0' })
$start.EnvironmentVariables['SIMPLEFILE_SERVICE_PATH'] = $service
$start.EnvironmentVariables['SUMAFILE_VIDEO_HELPER'] = $video
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
$appProcessId = $null
$serverProcessId = $null
$smokeCompleted = $false
# Every helper process of this run starts after this point (CIM creation times are local).
$launchedAt = [DateTime]::Now.AddSeconds(-1)
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
        $serverProcessId = $remoteServer.Id
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
    $appProcessId = $process.Id
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
    Write-Host 'UI runtime smoke scenarios passed.'
    $report.results | Where-Object { $_.scenario -eq 'large-folder' } | Format-Table -AutoSize | Out-Host
    $report.results | Where-Object { $_.scenario -eq 'thumbnails' } | Format-Table -AutoSize | Out-Host
    # Video thumbnails through the new path (the service's video process) and the old one (in the app).
    $report.results | Where-Object { $_.scenario -eq 'video-thumbnails' } | Format-List | Out-Host
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

# Only reached when the run and every after-run check passed: a throw above ends the script and keeps the run folder
# as evidence. The report and the polish screenshots are copied beside the folder first, so the printed paths, and
# CI's upload, outlive it.
if (-not $smokeCompleted) { throw "UI smoke did not complete. Evidence: $testRoot" }
$passedReportPath = Join-Path $uiSmokeRoot 'last-passed-report.json'
Copy-Item -LiteralPath $reportPath -Destination $passedReportPath -Force
$shotsCopied = Copy-UiSmokePassedScreenshots -RunRoot $testRoot -Destination $passedShotsRoot
Write-Host "UI runtime smoke passed. Report: $passedReportPath"
if ($shotsCopied) { Write-Host "Screenshots: $passedShotsRoot" }
if ($KeepResults) {
    Write-Host "Kept the run folder (-KeepResults): $testRoot"
} elseif (-not $shotsCopied) {
    Write-Warning "Kept the run folder, which holds the screenshots that could not be copied: $testRoot"
} else {
    $helperExecutables = @(@($installDir, $service, $media, $video, $sevenZa, $remoteServerExe, $pluginRoots) | Where-Object { $_ })
    $helperRoots = @(@($appProcessId, $serverProcessId) | Where-Object { $null -ne $_ })
    if (-not (Wait-UiSmokeHelperProcessesGone -RootProcessIds $helperRoots -Since $launchedAt -Executables $helperExecutables -RunRoot $testRoot)) {
        Write-Warning "Kept the run folder while helper processes may still use it: $testRoot"
    } elseif (Remove-UiSmokeRunFolder -RunRoot $testRoot -ResultsRoot $uiSmokeRoot) {
        Write-Host "Deleted the passed run's folder $testRoot (pass -KeepResults to keep it)."
    }
}
