#requires -Version 5.1
# Tests scripts/lib/plugin-staging.ps1's bundled.json writer and checker with two fake plugins in a temp root:
# the list is valid JSON without a BOM for more than one plugin, and the checker reports a stale hash and a
# missing id. Then the app-owned plugin items of an install folder (Case 8, which stages the real Debug bundled
# plugins, so run scripts/build-plugins.ps1 first), the pre-release plugin locations (Case 9), the installer smokes'
# helpers (Case 10) and the Git plugin's staging table (Case 11).
# Prints "Plugin staging tests passed." or throws.
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

# 7. The real staging table: the three bundled plugins, in order, and the remote plugin's five files from their
# build outputs. Only paths are checked, so nothing needs to be built.
$bundledIds = @($script:BundledPluginIds)
if (($bundledIds -join ',') -cne 'com.sumafile.sevenzip,com.sumafile.remote,com.sumafile.git') {
    throw "Case 7: bundled plugin ids are '$($bundledIds -join ',')'; expected com.sumafile.sevenzip,com.sumafile.remote,com.sumafile.git."
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
$expectedFreshness = @('sumafile-remote-backend.exe', 'SumaFile.Remote.dll', 'SumaFile.Remote.Model.dll', 'SumaFile.Remote.deps.json', 'plugin.json')
if (($freshness -join '|') -cne ($expectedFreshness -join '|')) {
    throw "Case 7: com.sumafile.remote freshness files are [$($freshness -join ', ')]; expected [$($expectedFreshness -join ', ')]."
}

# 8. App-owned plugin items in an install folder: everything the app writes beside its bundled plugins. A clean
# staged install folder has none; the user's plugins, the installer's dot folders, plugin data, the approvals file
# and the plugin log are each listed once, sorted. The state check sees a changed and a deleted file.
$install = Join-Path ([System.IO.Path]::GetTempPath()) ('sumafile-app-owned-test-' + [guid]::NewGuid().ToString('N'))
try {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $installPlugins = Join-Path $install 'plugins'
    Install-BundledPlugins -Root $installPlugins -Configuration Debug
    $clean = @(Get-AppOwnedPluginItems -InstallDir $install)
    if ($clean.Count -ne 0) { throw "Case 8: a clean staged install folder lists app-owned items: [$($clean -join ', ')]." }

    $userPlugin = Join-Path $installPlugins 'com.example.user'
    New-Item -ItemType Directory -Force -Path $userPlugin | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $userPlugin 'plugin.json'), '{"id":"com.example.user"}', $utf8)
    New-Item -ItemType Directory -Force -Path (Join-Path $installPlugins '.com.example.user.installing-x') | Out-Null
    $dataFile = Join-Path (Join-Path (Join-Path $install 'plugins-data') 'x') 'a.txt'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dataFile) | Out-Null
    [System.IO.File]::WriteAllText($dataFile, 'data', $utf8)
    foreach ($name in @('plugin-approvals.json', 'plugins.log', 'plugins.log.1')) {
        [System.IO.File]::WriteAllText((Join-Path $install $name), $name, $utf8)
    }
    $expectedItems = @('plugin-approvals.json', 'plugins-data', 'plugins.log', 'plugins.log.1',
        'plugins\.com.example.user.installing-x', 'plugins\com.example.user')
    $items = @(Get-AppOwnedPluginItems -InstallDir $install)
    if (($items -join '|') -cne ($expectedItems -join '|')) {
        throw "Case 8: app-owned items are [$($items -join ', ')]; expected [$($expectedItems -join ', ')]."
    }
    # The approval store's interrupted writes are app-owned too.
    $approvalsTmp = Join-Path $install 'plugin-approvals.json.1234.0a1b.tmp'
    [System.IO.File]::WriteAllText($approvalsTmp, 'tmp', $utf8)
    $withTmp = @(Get-AppOwnedPluginItems -InstallDir $install)
    if ($withTmp -cnotcontains 'plugin-approvals.json.1234.0a1b.tmp' -or $withTmp.Count -ne 7) {
        throw "Case 8: with an approvals temp file the items are [$($withTmp -join ', ')]."
    }
    Remove-Item -LiteralPath $approvalsTmp -Force

    $before = Get-AppOwnedPluginState -InstallDir $install
    $again = Get-AppOwnedPluginState -InstallDir $install
    if (@($before.Keys).Count -eq 0 -or (@($before.Keys) -join '|') -cne (@($again.Keys) -join '|')) {
        throw "Case 8: the app-owned state is not stable: [$(@($before.Keys) -join ', ')] then [$(@($again.Keys) -join ', ')]."
    }
    Assert-AppOwnedPluginStateUnchanged -InstallDir $install -Before $before

    [System.IO.File]::WriteAllText($dataFile, 'changed', $utf8)
    Assert-Throws { Assert-AppOwnedPluginStateUnchanged -InstallDir $install -Before $before } 'plugins-data\x\a.txt' 'Case 8 (changed)'
    Assert-Throws { Assert-AppOwnedPluginStateUnchanged -InstallDir $install -Before $before } "App-owned plugin files changed under $install" 'Case 8 (message)'

    $before = Get-AppOwnedPluginState -InstallDir $install
    Remove-Item -LiteralPath (Join-Path $install 'plugins.log') -Force
    Assert-Throws { Assert-AppOwnedPluginStateUnchanged -InstallDir $install -Before $before } 'plugins.log' 'Case 8 (deleted)'
} finally {
    if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
}

# 9. The pre-release plugin locations under %LOCALAPPDATA%\SumaFile, here a temp stand-in. The paths are built from
# segments, so no script holds a literal the packaging check forbids.
$localAppData = Join-Path ([System.IO.Path]::GetTempPath()) ('sumafile-legacy-test-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $localAppData | Out-Null
    $empty = Get-LegacyPluginLocationSnapshot -LocalAppData $localAppData
    if (@($empty.Keys).Count -ne 0) { throw "Case 9: a snapshot without a SumaFile folder is [$(@($empty.Keys) -join ', ')]." }
    Assert-LegacyPluginLocationsUnchanged -Before $empty -LocalAppData $localAppData

    $sumaFile = Join-Path $localAppData 'SumaFile'
    $legacyLog = Join-Path $sumaFile 'plugins.log'
    $legacyData = Join-Path $sumaFile 'plugins-data'
    New-Item -ItemType Directory -Force -Path (Join-Path $legacyData 'y') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path (Join-Path $sumaFile 'plugins') 'com.example.old') | Out-Null
    [System.IO.File]::WriteAllText($legacyLog, "old line`r`n", $utf8)
    [System.IO.File]::WriteAllText((Join-Path $sumaFile 'plugin-approvals.json'), '{}', $utf8)
    # The app's own files beside them are not plugin locations.
    [System.IO.File]::WriteAllText((Join-Path $sumaFile 'startup-timing.log'), 'timing', $utf8)

    $before = Get-LegacyPluginLocationSnapshot -LocalAppData $localAppData
    $expectedLegacy = @('plugin-approvals.json', 'plugins', 'plugins-data', 'plugins-data\y', 'plugins.log', 'plugins\com.example.old')
    $legacyKeys = @($before.Keys | Sort-Object)
    if (($legacyKeys -join '|') -cne (@($expectedLegacy | Sort-Object) -join '|')) {
        throw "Case 9: the legacy snapshot is [$($legacyKeys -join ', ')]; expected [$($expectedLegacy -join ', ')]."
    }
    Assert-LegacyPluginLocationsUnchanged -Before $before -LocalAppData $localAppData
    [System.IO.File]::WriteAllText((Join-Path $sumaFile 'startup-timing.log'), 'timing, longer', $utf8)
    Assert-LegacyPluginLocationsUnchanged -Before $before -LocalAppData $localAppData

    [System.IO.File]::AppendAllText($legacyLog, "new line`r`n", $utf8)
    Assert-Throws { Assert-LegacyPluginLocationsUnchanged -Before $before -LocalAppData $localAppData } 'plugins.log' 'Case 9 (log)'
    Assert-Throws { Assert-LegacyPluginLocationsUnchanged -Before $before -LocalAppData $localAppData } `
        'The app changed a pre-release plugin location under %LOCALAPPDATA%\SumaFile: ' 'Case 9 (message)'

    $before = Get-LegacyPluginLocationSnapshot -LocalAppData $localAppData
    New-Item -ItemType Directory -Force -Path (Join-Path $legacyData 'x') | Out-Null
    Assert-Throws { Assert-LegacyPluginLocationsUnchanged -Before $before -LocalAppData $localAppData } 'plugins-data\x' 'Case 9 (folder)'
} finally {
    if (Test-Path -LiteralPath $localAppData) { Remove-Item -LiteralPath $localAppData -Recurse -Force }
}

# 10. The installer smokes' helpers: the user plugin seed, the folder-gone wait, the quick folder snapshot and the
# launcher's refusal of an executable outside its smoke root. Nothing is installed or started.
$smoke = Join-Path ([System.IO.Path]::GetTempPath()) ('sumafile-installer-helpers-test-' + [guid]::NewGuid().ToString('N'))
try {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $seedInstall = Join-Path $smoke 'install'
    New-Item -ItemType Directory -Force -Path $seedInstall | Out-Null
    New-UserPluginSeed -InstallDir $seedInstall
    $seeded = Get-AppOwnedPluginState -InstallDir $seedInstall
    $seededFiles = @($seeded.Keys | Where-Object { [string]$seeded[$_] -cne '<dir>' } | Sort-Object)
    $expectedSeedFiles = @('plugin-approvals.json', 'plugins-data\com.example.smoke-user\data.txt',
        'plugins\com.example.smoke-user\payload.txt', 'plugins\com.example.smoke-user\plugin.json') | Sort-Object
    if (($seededFiles -join '|') -cne ($expectedSeedFiles -join '|')) {
        throw "Case 10: the seeded app-owned files are [$($seededFiles -join ', ')]; expected [$($expectedSeedFiles -join ', ')]."
    }
    $approvalsText = [System.IO.File]::ReadAllText((Join-Path $seedInstall 'plugin-approvals.json'))
    if ($approvalsText -cne '{}') { throw "Case 10: a seed without approvals writes '{}', got '$approvalsText'." }
    # An approvals file the app already wrote is kept.
    [System.IO.File]::WriteAllText((Join-Path $seedInstall 'plugin-approvals.json'), '{"kept":true}', $utf8)
    New-UserPluginSeed -InstallDir $seedInstall
    $approvalsText = [System.IO.File]::ReadAllText((Join-Path $seedInstall 'plugin-approvals.json'))
    if ($approvalsText -cne '{"kept":true}') { throw "Case 10: the seed replaced an existing approvals file: '$approvalsText'." }

    # Wait-FolderGone returns at once for a missing folder and throws within its timeout for one that stays.
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Wait-FolderGone -Path (Join-Path $smoke 'never-created') -TimeoutSeconds 30
    if ($watch.Elapsed.TotalSeconds -ge 2) { throw "Case 10: Wait-FolderGone took $($watch.Elapsed.TotalSeconds) s for a missing folder." }
    $watch.Restart()
    Assert-Throws { Wait-FolderGone -Path $seedInstall -TimeoutSeconds 1 } "$seedInstall still exists 1 s after uninstall: " 'Case 10 (stays)'
    Assert-Throws { Wait-FolderGone -Path $seedInstall -TimeoutSeconds 1 } 'plugins\com.example.smoke-user' 'Case 10 (entries)'
    if ($watch.Elapsed.TotalSeconds -ge 10) { throw "Case 10: Wait-FolderGone overran its 1 s timeout: $($watch.Elapsed.TotalSeconds) s." }

    # Get-FolderSnapshot: empty for a missing folder; a length change, a time change and an added file are named.
    $missing = Get-FolderSnapshot -Path (Join-Path $smoke 'never-created')
    if (@($missing.Keys).Count -ne 0) { throw "Case 10: the snapshot of a missing folder is [$(@($missing.Keys) -join ', ')]." }
    $snapDir = Join-Path $smoke 'snap'
    New-Item -ItemType Directory -Force -Path (Join-Path $snapDir 'sub') | Out-Null
    $snapFile = Join-Path (Join-Path $snapDir 'sub') 'a.txt'
    [System.IO.File]::WriteAllText($snapFile, 'one', $utf8)
    $snapBefore = Get-FolderSnapshot -Path $snapDir
    if ((@($snapBefore.Keys) -join '|') -cne 'sub|sub\a.txt' -or [string]$snapBefore['sub'] -cne '<dir>') {
        throw "Case 10: the folder snapshot is [$(@($snapBefore.Keys) -join ', ')]."
    }
    Assert-FolderSnapshotUnchanged -Path $snapDir -Before $snapBefore -Label 'Case 10 folder'
    [System.IO.File]::WriteAllText($snapFile, 'longer', $utf8)
    Assert-Throws { Assert-FolderSnapshotUnchanged -Path $snapDir -Before $snapBefore -Label 'Case 10 folder' } `
        "Case 10 folder changed under ${snapDir}: sub\a.txt (changed)" 'Case 10 (length)'
    $snapBefore = Get-FolderSnapshot -Path $snapDir
    [System.IO.File]::SetLastWriteTimeUtc($snapFile, [datetime]::UtcNow.AddMinutes(-5))
    Assert-Throws { Assert-FolderSnapshotUnchanged -Path $snapDir -Before $snapBefore -Label 'Case 10 folder' } 'sub\a.txt (changed)' 'Case 10 (time)'
    $snapBefore = Get-FolderSnapshot -Path $snapDir
    [System.IO.File]::WriteAllText((Join-Path $snapDir 'b.txt'), 'new', $utf8)
    Assert-Throws { Assert-FolderSnapshotUnchanged -Path $snapDir -Before $snapBefore -Label 'Case 10 folder' } 'b.txt (added)' 'Case 10 (added)'

    # Start-InstalledSumaFile launches only an app under its smoke root. The outside executable is a path that does
    # not exist, so a broken refusal could start nothing either.
    $outsideExe = Join-Path (Join-Path ([System.IO.Path]::GetTempPath()) ('sumafile-outside-' + [guid]::NewGuid().ToString('N'))) 'SumaFile.exe'
    Assert-Throws { Start-InstalledSumaFile -Exe $outsideExe -SmokeRoot $smoke } 'outside the smoke root' 'Case 10 (outside)'
    # A sibling folder whose name only starts with the smoke root's is outside it too.
    Assert-Throws { Start-InstalledSumaFile -Exe ($smoke + '-other\SumaFile.exe') -SmokeRoot $smoke } 'outside the smoke root' 'Case 10 (prefix)'
} finally {
    if (Test-Path -LiteralPath $smoke) { Remove-Item -LiteralPath $smoke -Recurse -Force }
}

# 11. The Git plugin's staging: its five files from their build outputs, the five files the freshness check
# compares, and its backend and UI entries. Only paths are checked, so nothing needs to be built.
$gitUiOut = 'plugins\git\ui\SumaFile.Git\bin\Debug\net10.0-windows10.0.19041.0'
$expectedGit = [ordered]@{
    'plugin.json'              = 'plugins\git\plugin.json'
    'sumafile-git-backend.exe' = 'target\debug\sumafile-git-backend.exe'
    'SumaFile.Git.dll'         = "$gitUiOut\SumaFile.Git.dll"
    'SumaFile.Git.Model.dll'   = "$gitUiOut\SumaFile.Git.Model.dll"
    'SumaFile.Git.deps.json'   = "$gitUiOut\SumaFile.Git.deps.json"
}
$gitSources = Get-BundledPluginSources -Id com.sumafile.git -Configuration Debug
if ((@($gitSources.Keys) -join '|') -cne (@($expectedGit.Keys) -join '|')) {
    throw "Case 11: com.sumafile.git stages [$(@($gitSources.Keys) -join ', ')]; expected [$(@($expectedGit.Keys) -join ', ')]."
}
foreach ($name in $expectedGit.Keys) {
    $expectedPath = Join-Path $repo $expectedGit[$name]
    if ($gitSources[$name] -ne $expectedPath) { throw "Case 11: $name comes from $($gitSources[$name]); expected $expectedPath." }
}
$gitReleaseSources = Get-BundledPluginSources -Id com.sumafile.git -Configuration Release
if ($gitReleaseSources['sumafile-git-backend.exe'] -ne (Join-Path $repo 'target\release\sumafile-git-backend.exe') -or
    $gitReleaseSources['SumaFile.Git.dll'] -ne (Join-Path $repo ($gitUiOut.Replace('\bin\Debug\', '\bin\Release\') + '\SumaFile.Git.dll'))) {
    throw 'Case 11: the Release sources of com.sumafile.git do not come from the Release outputs.'
}
$gitFreshness = @(Get-BundledPluginFreshnessFiles -Id com.sumafile.git)
$expectedGitFreshness = @('sumafile-git-backend.exe', 'SumaFile.Git.dll', 'SumaFile.Git.Model.dll', 'SumaFile.Git.deps.json', 'plugin.json')
if (($gitFreshness -join '|') -cne ($expectedGitFreshness -join '|')) {
    throw "Case 11: com.sumafile.git freshness files are [$($gitFreshness -join ', ')]; expected [$($expectedGitFreshness -join ', ')]."
}
$gitBackend = $script:BundledPluginBackends['com.sumafile.git']
if (-not $gitBackend -or $gitBackend.Exe -cne 'sumafile-git-backend.exe' -or $gitBackend.Package -cne 'sumafile-git-backend') {
    throw 'Case 11: the backend table must build sumafile-git-backend.exe with cargo package sumafile-git-backend.'
}
$gitUi = $script:BundledPluginUiProjects['com.sumafile.git']
if (-not $gitUi -or $gitUi.Project -cne 'plugins\git\ui\SumaFile.Git\SumaFile.Git.csproj' -or
    (@($gitUi.Files) -join '|') -cne 'SumaFile.Git.dll|SumaFile.Git.Model.dll|SumaFile.Git.deps.json') {
    throw 'Case 11: the UI table must build plugins\git\ui\SumaFile.Git\SumaFile.Git.csproj into its three staged files.'
}

Write-Host 'Plugin staging tests passed.'
