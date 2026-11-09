#requires -Version 5.1
# Runs the App's real CopyBundledPlugins MSBuild target against temp folders. It must replace each staged plugin
# folder whole (a file a rebuild dropped does not linger) and leave everything else in the output alone: a
# user-installed plugin beside the bundled ones and the app-owned plugins-data folder.
# Needs the App's restore output, so run `npm run build:winui` first.
# Prints "Plugin copy targets keep user plugins and app-owned files." or throws.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src-winui\SimpleFile.App\SimpleFile.App.csproj'
if (-not (Test-Path -LiteralPath (Join-Path $repo 'src-winui\SimpleFile.App\obj\project.assets.json'))) {
    throw 'Run npm run build:winui first.'
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('sumafile-copy-targets-' + [guid]::NewGuid().ToString('N'))
try {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    function Write-TestFile([string]$Path, [string]$Text) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
        [System.IO.File]::WriteAllText($Path, $Text, $utf8)
    }

    # The staging root, as scripts/build-plugins.ps1 leaves target\plugins.
    $staging = Join-Path $root 'staging'
    Write-TestFile (Join-Path $staging 'bundled.json') '{"plugins":[]}'
    Write-TestFile (Join-Path $staging 'com.sumafile.remote\plugin.json') '{"id":"com.sumafile.remote"}'
    Write-TestFile (Join-Path $staging 'com.sumafile.remote\a.txt') 'a'

    # The Debug output: an older copy of the bundled plugin with a file the new staging dropped, a user-installed
    # plugin and the app-owned data folder.
    $out = Join-Path $root 'out'
    $stale = Join-Path $out 'plugins\com.sumafile.remote\stale.txt'
    $userManifest = Join-Path $out 'plugins\com.example.user\plugin.json'
    $keep = Join-Path $out 'plugins-data\x\keep.txt'
    Write-TestFile $stale 'stale'
    Write-TestFile $userManifest '{"id":"com.example.user"}'
    Write-TestFile $keep 'keep'

    # The same platform properties `npm run build:winui` gives the App through the solution (Debug|x64). Windows
    # PowerShell quotes an argument with a space, and a quoted value must not end in one backslash, which would
    # escape the closing quote.
    function Get-DirectoryArgument([string]$Path) { if ($Path.Contains(' ')) { return "$Path\\" } return "$Path\" }
    $outArg = Get-DirectoryArgument $out
    $stagingArg = Get-DirectoryArgument $staging
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & dotnet msbuild $project -t:CopyBundledPlugins -p:Configuration=Debug -p:Platform=x64 `
            "-p:OutDir=$outArg" "-p:BundledPluginsStagingDir=$stagingArg" -nologo 2>&1
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($LASTEXITCODE -ne 0) {
        throw "CopyBundledPlugins failed (exit $LASTEXITCODE):`n$($output -join "`n")"
    }

    if (Test-Path -LiteralPath $stale) { throw 'CopyBundledPlugins left stale.txt in the bundled plugin folder.' }
    $copied = Join-Path $out 'plugins\com.sumafile.remote\a.txt'
    if (-not (Test-Path -LiteralPath $copied)) { throw 'CopyBundledPlugins did not copy a.txt.' }
    if (-not (Test-Path -LiteralPath (Join-Path $out 'plugins\bundled.json'))) { throw 'CopyBundledPlugins did not copy bundled.json.' }
    if (-not (Test-Path -LiteralPath $userManifest) -or
        [System.IO.File]::ReadAllText($userManifest) -ne '{"id":"com.example.user"}') {
        throw 'CopyBundledPlugins changed the user-installed plugin com.example.user.'
    }
    if (-not (Test-Path -LiteralPath $keep) -or [System.IO.File]::ReadAllText($keep) -ne 'keep') {
        throw 'CopyBundledPlugins changed plugins-data\x\keep.txt.'
    }
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Write-Host 'Plugin copy targets keep user plugins and app-owned files.'
