#requires -Version 5.1
# Tests scripts/lib/plugin-staging.ps1's bundled.json writer and checker with two fake plugins in a temp root:
# the list is valid JSON without a BOM for more than one plugin, and the checker reports a stale hash and a
# missing id. Prints "Plugin staging tests passed." or throws.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
. (Join-Path $PSScriptRoot 'lib\plugin-staging.ps1')

function Assert-Throws([scriptblock]$Action, [string]$Expected, [string]$Case) {
    $message = $null
    try { & $Action } catch { $message = $_.Exception.Message }
    if ($null -eq $message) { throw "${Case}: expected an error containing '$Expected', but nothing was thrown." }
    if (-not $message.Contains($Expected)) { throw "${Case}: expected an error containing '$Expected', got: $message" }
}

$ids = @('com.example.one', 'com.example.two')
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('sumafile-staging-test-' + [guid]::NewGuid().ToString('N'))
try {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    foreach ($id in $ids) {
        $dir = Join-Path $tmp $id
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $dir 'plugin.json'), "{`"id`":`"$id`",`"name`":`"$id`"}", $utf8)
    }
    $listPath = Join-Path $tmp 'bundled.json'

    # 1. Written without a BOM.
    Write-BundledPluginList -Root $tmp -PluginIds $ids
    $bytes = [System.IO.File]::ReadAllBytes($listPath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "Case 1: $listPath starts with a UTF-8 BOM."
    }

    # 2. Valid JSON with exactly two entries, in order, each with its manifest hash.
    $list = [System.Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    $entries = @($list.plugins)
    if ($entries.Count -ne 2) { throw "Case 2: expected 2 entries, got $($entries.Count)." }
    for ($i = 0; $i -lt $ids.Count; $i++) {
        $expectedHash = Get-PluginStagingSha256 (Join-Path (Join-Path $tmp $ids[$i]) 'plugin.json')
        if ($entries[$i].id -cne $ids[$i]) { throw "Case 2: entry $i has id '$($entries[$i].id)'; expected $($ids[$i])." }
        if ($entries[$i].manifestSha256 -cne $expectedHash) {
            throw "Case 2: entry $i has hash '$($entries[$i].manifestSha256)'; expected $expectedHash."
        }
    }

    # 3. The checker accepts it.
    Assert-BundledPluginList -Root $tmp -PluginIds $ids

    # 4. A changed manifest no longer matches its listed hash.
    [System.IO.File]::WriteAllText((Join-Path (Join-Path $tmp 'com.example.two') 'plugin.json'), '{"id":"com.example.two","changed":true}', $utf8)
    Assert-Throws { Assert-BundledPluginList -Root $tmp -PluginIds $ids } 'hashes to' 'Case 4'

    # 5. With the list rewritten, an id it does not list is reported before hashes and the count.
    Write-BundledPluginList -Root $tmp -PluginIds $ids
    Assert-Throws { Assert-BundledPluginList -Root $tmp -PluginIds @('com.example.one', 'com.example.two', 'com.example.three') } `
        'does not list com.example.three' 'Case 5'

    # 6. A one-plugin list (today's real staging) passes, and the count check still sees the extra entry.
    Assert-Throws { Assert-BundledPluginList -Root $tmp -PluginIds @('com.example.one') } 'lists 2 plugins; expected 1.' 'Case 6'
    Write-BundledPluginList -Root $tmp -PluginIds @('com.example.one')
    Assert-BundledPluginList -Root $tmp -PluginIds @('com.example.one')
} finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
}

# 7. The real staging table: both bundled plugins, in order, and the remote plugin's five files from their build
# outputs. Only paths are checked, so nothing needs to be built.
$bundledIds = @($script:BundledPluginIds)
if (($bundledIds -join ',') -cne 'com.sumafile.sevenzip,com.sumafile.remote') {
    throw "Case 7: bundled plugin ids are '$($bundledIds -join ',')'; expected com.sumafile.sevenzip,com.sumafile.remote."
}
$repo = Get-PluginStagingRepoRoot
$remoteUiOut = 'plugins\remote\ui\SumaFile.Remote\bin\Debug\net10.0-windows10.0.19041.0'
$expectedRemote = [ordered]@{
    'plugin.json'                 = 'plugins\remote\plugin.json'
    'sumafile-remote-backend.exe' = 'target\debug\sumafile-remote-backend.exe'
    'SumaFile.Remote.dll'         = "$remoteUiOut\SumaFile.Remote.dll"
    'SumaFile.Remote.Model.dll'   = "$remoteUiOut\SumaFile.Remote.Model.dll"
    'SumaFile.Remote.deps.json'   = "$remoteUiOut\SumaFile.Remote.deps.json"
}
$remoteSources = Get-BundledPluginSources -Id com.sumafile.remote -Configuration Debug
if ((@($remoteSources.Keys) -join '|') -cne (@($expectedRemote.Keys) -join '|')) {
    throw "Case 7: com.sumafile.remote stages [$(@($remoteSources.Keys) -join ', ')]; expected [$(@($expectedRemote.Keys) -join ', ')]."
}
foreach ($name in $expectedRemote.Keys) {
    $expectedPath = Join-Path $repo $expectedRemote[$name]
    if ($remoteSources[$name] -ne $expectedPath) { throw "Case 7: $name comes from $($remoteSources[$name]); expected $expectedPath." }
}
$releaseSources = Get-BundledPluginSources -Id com.sumafile.remote -Configuration Release
if ($releaseSources['sumafile-remote-backend.exe'] -ne (Join-Path $repo 'target\release\sumafile-remote-backend.exe') -or
    $releaseSources['SumaFile.Remote.dll'] -ne (Join-Path $repo ($remoteUiOut.Replace('\bin\Debug\', '\bin\Release\') + '\SumaFile.Remote.dll'))) {
    throw 'Case 7: the Release sources of com.sumafile.remote do not come from the Release outputs.'
}
$freshness = @(Get-BundledPluginFreshnessFiles -Id com.sumafile.remote)
$expectedFreshness = @('sumafile-remote-backend.exe', 'SumaFile.Remote.dll', 'SumaFile.Remote.Model.dll', 'plugin.json')
if (($freshness -join '|') -cne ($expectedFreshness -join '|')) {
    throw "Case 7: com.sumafile.remote freshness files are [$($freshness -join ', ')]; expected [$($expectedFreshness -join ', ')]."
}

Write-Host 'Plugin staging tests passed.'
