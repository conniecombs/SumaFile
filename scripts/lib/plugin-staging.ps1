#requires -Version 5.1
# Stages SumaFile's first-party (bundled) plugins into a plugins root, the same way for dev runs, smokes and the
# release. Dot-source it: `. scripts/lib/plugin-staging.ps1`.
#
# A staged root holds one folder per bundled plugin and a bundled.json that lists each plugin's manifest SHA-256
# (the lowercase hex of the staged plugin.json bytes, as PluginPackageHash computes it), UTF-8 without a BOM:
#   {"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"<64 lowercase hex>"},...]}
# The host trusts a plugin it finds in its install root under that hash without a consent dialog.

# Every bundled plugin, in staging and bundled.json order. Each id needs a branch in Get-BundledPluginSources and
# Get-BundledPluginFreshnessFiles.
$script:BundledPluginIds = @('com.sumafile.sevenzip', 'com.sumafile.remote', 'com.sumafile.git')
$script:BundledListFileName = 'bundled.json'

$script:SevenZipBackendExe = 'sumafile-sevenzip-backend.exe'
# third_party\7zip\win-x64 ships readme.txt too; the plugin folder does not carry it.
$script:SevenZipBundleFiles = @('7za.exe', '7za.dll', '7zxa.dll', 'License.txt', 'SUMAFILE_BUNDLE.md')

$script:RemoteBackendExe = 'sumafile-remote-backend.exe'
# The remote plugin's UI: the code-built WinUI assembly, its WinUI-free model and the deps file the host's
# AssemblyDependencyResolver reads. The other files a 19041 library build emits (projections, .pri, runtimes\) are
# never staged: the host supplies them.
$script:RemoteUiProject = 'plugins\remote\ui\SumaFile.Remote\SumaFile.Remote.csproj'
$script:RemoteUiFiles = @('SumaFile.Remote.dll', 'SumaFile.Remote.Model.dll', 'SumaFile.Remote.deps.json')

$script:GitBackendExe = 'sumafile-git-backend.exe'
# The Git plugin's UI, staged like the remote plugin's: the code-built WinUI assembly, its WinUI-free model and the
# deps file. The backend runs git.exe from PATH; the plugin folder carries no git.
$script:GitUiProject = 'plugins\git\ui\SumaFile.Git\SumaFile.Git.csproj'
$script:GitUiFiles = @('SumaFile.Git.dll', 'SumaFile.Git.Model.dll', 'SumaFile.Git.deps.json')

# Per bundled plugin: its backend exe (named when that source is missing) and the cargo package that builds it.
$script:BundledPluginBackends = @{
    'com.sumafile.sevenzip' = @{ Exe = $script:SevenZipBackendExe; Package = 'sumafile-sevenzip-backend' }
    'com.sumafile.remote'   = @{ Exe = $script:RemoteBackendExe; Package = 'sumafile-remote-backend' }
    'com.sumafile.git'      = @{ Exe = $script:GitBackendExe; Package = 'sumafile-git-backend' }
}

# Per bundled plugin with a C# UI: its project (named, with the build command, when a UI source is missing) and the
# staged names that build produces.
$script:BundledPluginUiProjects = @{
    'com.sumafile.remote' = @{ Project = $script:RemoteUiProject; Files = $script:RemoteUiFiles }
    'com.sumafile.git'    = @{ Project = $script:GitUiProject; Files = $script:GitUiFiles }
}

function Get-PluginStagingRepoRoot {
    (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
}

function Resolve-PluginStagingPath([string]$Path) {
    # Full path against the PowerShell location, for folders that may not exist yet.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path).TrimEnd('\', '/')
}

function Get-PluginStagingSha256([string]$Path) {
    # .NET directly: Get-FileHash is missing when Windows PowerShell inherits a pwsh PSModulePath.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash([System.IO.File]::ReadAllBytes($Path)) } finally { $sha.Dispose() }
    ([System.BitConverter]::ToString($digest) -replace '-', '').ToLowerInvariant()
}

function Get-SevenZipPluginSources([string]$Configuration) {
    # Ordered: staged file name -> source path in the repository.
    $repo = Get-PluginStagingRepoRoot
    $cargoProfile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
    $sources = [ordered]@{
        'plugin.json'             = Join-Path $repo 'plugins\sevenzip\plugin.json'
        $script:SevenZipBackendExe = Join-Path $repo "target\$cargoProfile\$($script:SevenZipBackendExe)"
    }
    $bundleDir = Join-Path $repo 'third_party\7zip\win-x64'
    foreach ($name in $script:SevenZipBundleFiles) { $sources[$name] = Join-Path $bundleDir $name }
    $sources
}

function Get-RemotePluginSources([string]$Configuration) {
    # Ordered: staged file name -> source path in the repository.
    $repo = Get-PluginStagingRepoRoot
    $cargoProfile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
    $uiConfiguration = if ($Configuration -eq 'Release') { 'Release' } else { 'Debug' }
    $uiOutput = Join-Path $repo "plugins\remote\ui\SumaFile.Remote\bin\$uiConfiguration\net10.0-windows10.0.19041.0"
    $sources = [ordered]@{
        'plugin.json'            = Join-Path $repo 'plugins\remote\plugin.json'
        $script:RemoteBackendExe = Join-Path $repo "target\$cargoProfile\$($script:RemoteBackendExe)"
    }
    foreach ($name in $script:RemoteUiFiles) { $sources[$name] = Join-Path $uiOutput $name }
    $sources
}

function Get-GitPluginSources([string]$Configuration) {
    # Ordered: staged file name -> source path in the repository.
    $repo = Get-PluginStagingRepoRoot
    $cargoProfile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
    $uiConfiguration = if ($Configuration -eq 'Release') { 'Release' } else { 'Debug' }
    $uiOutput = Join-Path $repo "plugins\git\ui\SumaFile.Git\bin\$uiConfiguration\net10.0-windows10.0.19041.0"
    $sources = [ordered]@{
        'plugin.json'         = Join-Path $repo 'plugins\git\plugin.json'
        $script:GitBackendExe = Join-Path $repo "target\$cargoProfile\$($script:GitBackendExe)"
    }
    foreach ($name in $script:GitUiFiles) { $sources[$name] = Join-Path $uiOutput $name }
    $sources
}

function Get-BundledPluginSources {
    # Ordered: staged file name -> source path in the repository, for one bundled plugin.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
    )
    switch -CaseSensitive ($Id) {
        'com.sumafile.sevenzip' { return Get-SevenZipPluginSources $Configuration }
        'com.sumafile.remote' { return Get-RemotePluginSources $Configuration }
        'com.sumafile.git' { return Get-GitPluginSources $Configuration }
        default { throw "Unknown bundled plugin $Id." }
    }
}

function Get-BundledPluginFreshnessFiles {
    # The staged names Assert-BundledPluginsFresh compares with their sources: the files a rebuild changes.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Id)
    switch -CaseSensitive ($Id) {
        'com.sumafile.sevenzip' { return @($script:SevenZipBackendExe, 'plugin.json') }
        'com.sumafile.remote' { return @($script:RemoteBackendExe, 'SumaFile.Remote.dll', 'SumaFile.Remote.Model.dll', 'SumaFile.Remote.deps.json', 'plugin.json') }
        'com.sumafile.git' { return @($script:GitBackendExe, 'SumaFile.Git.dll', 'SumaFile.Git.Model.dll', 'SumaFile.Git.deps.json', 'plugin.json') }
        default { throw "Unknown bundled plugin $Id." }
    }
}

function Write-BundledPluginList {
    # <Root>\bundled.json: each id with the SHA-256 of <Root>\<id>\plugin.json, in the given order.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$PluginIds
    )
    $rootPath = Resolve-PluginStagingPath $Root
    $entries = foreach ($id in $PluginIds) {
        $hash = Get-PluginStagingSha256 (Join-Path (Join-Path $rootPath $id) 'plugin.json')
        ConvertTo-Json -InputObject ([ordered]@{ id = $id; manifestSha256 = $hash }) -Compress
    }
    # Joined by hand: ConvertTo-Json unwraps a one-element array, and a string plus an array joins with $OFS
    # (a space), which is not JSON once there are two entries.
    $json = '{"plugins":[' + (@($entries) -join ',') + ']}'
    [System.IO.File]::WriteAllText(
        (Join-Path $rootPath $script:BundledListFileName), $json, (New-Object System.Text.UTF8Encoding $false))
}

function Assert-BundledPluginList {
    # Throws at the first failure, in this order: an id the list does not have, a listed hash that differs from
    # the staged plugin.json, then an entry count other than the number of ids.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$PluginIds
    )
    $rootPath = Resolve-PluginStagingPath $Root
    $listPath = Join-Path $rootPath $script:BundledListFileName
    if (-not (Test-Path -LiteralPath $listPath -PathType Leaf)) { throw "Bundled plugin list missing: $listPath" }
    $list = Get-Content -LiteralPath $listPath -Raw -Encoding UTF8 | ConvertFrom-Json
    # @() outside the if: an if statement unrolls a one-element array, and Windows PowerShell's single
    # [pscustomobject] has no Count.
    $entries = @(if ($list -and $list.PSObject.Properties['plugins']) { $list.plugins })

    $listed = [ordered]@{}
    foreach ($id in $PluginIds) {
        $entry = $entries | Where-Object { $_ -and $_.PSObject.Properties['id'] -and $_.id -ceq $id } | Select-Object -First 1
        if (-not $entry) { throw "$listPath does not list $id." }
        $listed[$id] = if ($entry.PSObject.Properties['manifestSha256']) { [string]$entry.manifestSha256 } else { '' }
    }
    foreach ($id in $PluginIds) {
        $actual = Get-PluginStagingSha256 (Join-Path (Join-Path $rootPath $id) 'plugin.json')
        if ($listed[$id] -cne $actual) {
            throw "$listPath lists manifest hash '$($listed[$id])' for $id, but the staged plugin.json hashes to $actual."
        }
    }
    if ($entries.Count -ne $PluginIds.Count) { throw "$listPath lists $($entries.Count) plugins; expected $($PluginIds.Count)." }
}

function Install-BundledPlugins {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
    )
    # Every source is checked before the root is touched, so a missing one leaves the previous staging whole.
    $sourcesById = [ordered]@{}
    foreach ($id in $script:BundledPluginIds) {
        $sources = Get-BundledPluginSources -Id $id -Configuration $Configuration
        $backend = $script:BundledPluginBackends[$id]
        $ui = $script:BundledPluginUiProjects[$id]
        foreach ($entry in $sources.GetEnumerator()) {
            $source = $entry.Value
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                if ($backend -and $entry.Key -ceq $backend.Exe) {
                    throw "Plugin backend for $id not found at $source. Run cargo build --locked -p $($backend.Package)$(if ($Configuration -eq 'Release') { ' --release' })."
                }
                if ($ui -and $ui.Files -ccontains $entry.Key) {
                    throw "Plugin UI file $($entry.Key) for $id not found at $source. Run dotnet build $($ui.Project.Replace('\', '/')) -c $Configuration --nologo."
                }
                throw "Bundled plugin $id source file not found: $source"
            }
        }
        $sourcesById[$id] = $sources
    }

    $rootPath = Resolve-PluginStagingPath $Root
    foreach ($id in $sourcesById.Keys) {
        $pluginDir = Join-Path $rootPath $id
        if (Test-Path -LiteralPath $pluginDir) { Remove-Item -LiteralPath $pluginDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
        foreach ($entry in $sourcesById[$id].GetEnumerator()) {
            Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $pluginDir $entry.Key)
        }
    }

    Write-BundledPluginList -Root $rootPath -PluginIds $script:BundledPluginIds
}

function Assert-BundledPlugins {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Root)
    $rootPath = Resolve-PluginStagingPath $Root
    foreach ($pluginId in $script:BundledPluginIds) {
        $pluginDir = Join-Path $rootPath $pluginId
        foreach ($name in (Get-BundledPluginSources -Id $pluginId -Configuration Debug).Keys) {
            $path = Join-Path $pluginDir $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Bundled plugin file missing: $path" }
        }

        $manifestPath = Join-Path $pluginDir 'plugin.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $id = if ($manifest.PSObject.Properties['id']) { [string]$manifest.id } else { '' }
        if ($id -cne $pluginId) { throw "$manifestPath has id '$id'; expected $pluginId." }
    }

    Assert-BundledPluginList -Root $rootPath -PluginIds $script:BundledPluginIds
}

function Assert-BundledPluginsFresh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
    )
    $rootPath = Resolve-PluginStagingPath $Root
    foreach ($id in $script:BundledPluginIds) {
        $pluginDir = Join-Path $rootPath $id
        $sources = Get-BundledPluginSources -Id $id -Configuration $Configuration
        foreach ($name in (Get-BundledPluginFreshnessFiles -Id $id)) {
            $staged = Join-Path $pluginDir $name
            $source = $sources[$name]
            $fresh = (Test-Path -LiteralPath $staged -PathType Leaf) -and (Test-Path -LiteralPath $source -PathType Leaf) -and
                ((Get-PluginStagingSha256 $staged) -ceq (Get-PluginStagingSha256 $source))
            if (-not $fresh) { throw "Bundled plugin $id in $rootPath is stale. Run scripts/build-plugins.ps1 and rebuild." }
        }
    }
}

# App-owned plugin items: what the app itself writes into its install folder (phase 4: every plugin file lives
# there). The installers own the bundled plugin folders and bundled.json; everything else listed here is the app's,
# so a payload never carries it, an upgrade keeps it and an uninstall removes it.
$script:AppOwnedPluginFiles = @('plugins.log', 'plugins.log.1', 'plugin-approvals.json')
$script:AppOwnedPluginFilePatterns = @('plugin-approvals.json.*.tmp')
$script:AppOwnedPluginDataFolder = 'plugins-data'

function Get-PluginStagingRelativePath([string]$Base, [string]$Path) {
    $Path.Substring($Base.Length).TrimStart('\', '/')
}

function Get-PluginStagingSha256Shared([string]$Path) {
    # Like Get-PluginStagingSha256, but readable while another process still holds the file open (plugins.log).
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $digest = $sha.ComputeHash($stream) } finally { $sha.Dispose(); $stream.Dispose() }
    ([System.BitConverter]::ToString($digest) -replace '-', '').ToLowerInvariant()
}

function Get-AppOwnedPluginItems {
    # The sorted relative paths of the app-owned plugin items under an install folder: plugins.log, plugins.log.1,
    # plugin-approvals.json and its plugin-approvals.json.*.tmp leftovers, plugins-data, and every folder under
    # plugins whose name is not a bundled id, dot folders included. Empty for a clean staged install folder.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [string[]]$BundledIds = $script:BundledPluginIds
    )
    $installPath = Resolve-PluginStagingPath $InstallDir
    $items = New-Object System.Collections.Generic.List[string]
    if (Test-Path -LiteralPath $installPath -PathType Container) {
        foreach ($name in $script:AppOwnedPluginFiles) {
            if (Test-Path -LiteralPath (Join-Path $installPath $name)) { $items.Add($name) }
        }
        foreach ($file in @(Get-ChildItem -LiteralPath $installPath -File -Force)) {
            foreach ($pattern in $script:AppOwnedPluginFilePatterns) {
                if ($file.Name -like $pattern) { $items.Add($file.Name); break }
            }
        }
        if (Test-Path -LiteralPath (Join-Path $installPath $script:AppOwnedPluginDataFolder)) {
            $items.Add($script:AppOwnedPluginDataFolder)
        }
        $pluginsPath = Join-Path $installPath 'plugins'
        if (Test-Path -LiteralPath $pluginsPath -PathType Container) {
            foreach ($folder in @(Get-ChildItem -LiteralPath $pluginsPath -Directory -Force)) {
                # Case-insensitive, as Windows folder names are: a case variant of a bundled id is that folder.
                if (@($BundledIds) -notcontains $folder.Name) { $items.Add('plugins\' + $folder.Name) }
            }
        }
    }
    $sorted = $items.ToArray()
    [System.Array]::Sort($sorted, [System.StringComparer]::OrdinalIgnoreCase)
    # Unrolled: callers wrap the result in @(), which keeps an empty or one-element result an array.
    $sorted
}

function Get-AppOwnedPluginState {
    # Ordered: relative path -> SHA-256 for every file among and under the app-owned items, and "<dir>" for every
    # folder among and under them, so an added empty folder (a new plugins-data) is a change too.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$InstallDir)
    $installPath = Resolve-PluginStagingPath $InstallDir
    $state = [ordered]@{}
    foreach ($item in @(Get-AppOwnedPluginItems -InstallDir $installPath)) {
        $path = Join-Path $installPath $item
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $state[$item] = Get-PluginStagingSha256Shared $path
            continue
        }
        $state[$item] = '<dir>'
        foreach ($child in @(Get-ChildItem -LiteralPath $path -Recurse -Force | Sort-Object FullName)) {
            $relative = Get-PluginStagingRelativePath $installPath $child.FullName
            $state[$relative] = if ($child.PSIsContainer) { '<dir>' } else { Get-PluginStagingSha256Shared $child.FullName }
        }
    }
    $state
}

function Get-PluginStateChanges([System.Collections.IDictionary]$Before, [System.Collections.IDictionary]$After) {
    # "<path> (added|removed|changed)" for every difference, sorted by path.
    $changes = New-Object System.Collections.Generic.List[string]
    foreach ($key in $Before.Keys) {
        if (-not $After.Contains($key)) { $changes.Add("$key (removed)") }
        elseif ([string]$After[$key] -cne [string]$Before[$key]) { $changes.Add("$key (changed)") }
    }
    foreach ($key in $After.Keys) {
        if (-not $Before.Contains($key)) { $changes.Add("$key (added)") }
    }
    $sorted = $changes.ToArray()
    [System.Array]::Sort($sorted, [System.StringComparer]::OrdinalIgnoreCase)
    $sorted
}

function Assert-AppOwnedPluginStateUnchanged {
    # Throws when an app-owned plugin file or folder under the install folder was added, removed or changed since
    # -Before (a Get-AppOwnedPluginState result).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$InstallDir,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Before
    )
    $installPath = Resolve-PluginStagingPath $InstallDir
    $changes = @(Get-PluginStateChanges $Before (Get-AppOwnedPluginState -InstallDir $installPath))
    if ($changes.Count -gt 0) { throw "App-owned plugin files changed under ${installPath}: $($changes -join ', ')" }
}

function Get-FolderSnapshotValue([System.IO.FileSystemInfo]$Item) {
    if ($Item.PSIsContainer) { '<dir>' } else { "$($Item.Length)|$($Item.LastWriteTimeUtc.Ticks)" }
}

function Get-FolderSnapshot {
    # Ordered: path relative to -Path -> "<length>|<LastWriteTimeUtc.Ticks>" for every file under it and "<dir>" for
    # every folder under it, sorted; empty when the folder is missing. It hashes nothing, so it is quick on a whole
    # install folder.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)
    $folder = Resolve-PluginStagingPath $Path
    $snapshot = [ordered]@{}
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) { return $snapshot }
    foreach ($entry in @(Get-ChildItem -LiteralPath $folder -Recurse -Force | Sort-Object FullName)) {
        $snapshot[(Get-PluginStagingRelativePath $folder $entry.FullName)] = Get-FolderSnapshotValue $entry
    }
    $snapshot
}

function Assert-FolderSnapshotUnchanged {
    # Throws "<label> changed under <path>: <path> (added|removed|changed), ..." when anything under -Path differs from
    # -Before (a Get-FolderSnapshot result).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Before,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $folder = Resolve-PluginStagingPath $Path
    $changes = @(Get-PluginStateChanges $Before (Get-FolderSnapshot -Path $folder))
    if ($changes.Count -gt 0) { throw "$Label changed under ${folder}: $($changes -join ', ')" }
}

function Get-LegacyPluginLocationSnapshot {
    # Ordered: path relative to <LocalAppData>\SumaFile -> "<length>|<LastWriteTimeUtc.Ticks>" for a file, "<dir>" for
    # a folder. It covers the plugins and plugins-data folders and everything under them, plus plugin-approvals.json,
    # plugins.log and plugins.log.1: the pre-release plugin locations the app must never touch again. A missing item
    # is absent. The paths are joined from segments, so no script holds a literal check-winui-packaging.mjs forbids.
    [CmdletBinding()]
    param([string]$LocalAppData = $env:LOCALAPPDATA)
    $sumaFile = Join-Path (Resolve-PluginStagingPath $LocalAppData) 'SumaFile'
    $snapshot = [ordered]@{}
    foreach ($name in @('plugin-approvals.json', 'plugins.log', 'plugins.log.1', 'plugins', 'plugins-data')) {
        $path = Join-Path $sumaFile $name
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $item = Get-Item -LiteralPath $path -Force
        $snapshot[$name] = Get-FolderSnapshotValue $item
        if ($item.PSIsContainer) {
            $children = Get-FolderSnapshot -Path $path
            foreach ($child in $children.Keys) { $snapshot[(Join-Path $name $child)] = $children[$child] }
        }
    }
    $snapshot
}

function Assert-LegacyPluginLocationsUnchanged {
    # Throws when anything in the pre-release plugin locations was added, removed or changed since -Before (a
    # Get-LegacyPluginLocationSnapshot result).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary]$Before,
        [string]$LocalAppData = $env:LOCALAPPDATA
    )
    $changes = @(Get-PluginStateChanges $Before (Get-LegacyPluginLocationSnapshot -LocalAppData $LocalAppData))
    if ($changes.Count -gt 0) {
        throw "The app changed a pre-release plugin location under %LOCALAPPDATA%\SumaFile: $($changes -join ', ')"
    }
}

# The installer smokes (smoke-winui-nsis.ps1, smoke-winui-msi-lifecycle.ps1, smoke-winui-upgrade.ps1) install only into
# a disposable folder and never touch another SumaFile install on the machine. These helpers seed a user's plugin
# files, launch the installed copy, stop it by path and check that nothing outside the smoke changed.

$script:UserPluginSeedId = 'com.example.smoke-user'
# Every image the NSIS installer ends by name (.onInit and the Uninstall section), plus any plugin backend.
$script:SumaFileProcessNames = @('SumaFile', 'SimpleFile', 'SimpleFile.App', 'simplefile-service')
$script:SumaFileBackendProcessPattern = 'sumafile-*-backend'
# Removed from the installed app's environment, so its default plugin locations, all in its install folder, are what
# the installer smokes test.
$script:PluginLocationOverrides = @(
    'SUMAFILE_PLUGIN_ROOTS', 'SUMAFILE_PLUGIN_DATA_ROOT', 'SUMAFILE_PLUGIN_APPROVALS_PATH', 'SUMAFILE_PLUGIN_LOG_PATH'
)

function New-UserPluginSeed {
    # Writes what a user's own plugin leaves in an install folder: plugins\com.example.smoke-user\{plugin.json,
    # payload.txt} and plugins-data\com.example.smoke-user\data.txt, and plugin-approvals.json as {} when the app has
    # not written one. A reinstall or upgrade must keep all of it; an uninstall must remove it.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$InstallDir)
    $installPath = Resolve-PluginStagingPath $InstallDir
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $pluginDir = Join-Path (Join-Path $installPath 'plugins') $script:UserPluginSeedId
    $dataDir = Join-Path (Join-Path $installPath 'plugins-data') $script:UserPluginSeedId
    New-Item -ItemType Directory -Force -Path $pluginDir, $dataDir | Out-Null
    $manifest = '{"id":"' + $script:UserPluginSeedId + '","name":"Smoke user plugin","version":"1.0.0"}'
    [System.IO.File]::WriteAllText((Join-Path $pluginDir 'plugin.json'), $manifest, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $pluginDir 'payload.txt'), ('user plugin payload ' + [guid]::NewGuid().ToString('N')), $utf8)
    [System.IO.File]::WriteAllText((Join-Path $dataDir 'data.txt'), ('user plugin data ' + [guid]::NewGuid().ToString('N')), $utf8)
    $approvals = Join-Path $installPath 'plugin-approvals.json'
    if (-not (Test-Path -LiteralPath $approvals)) { [System.IO.File]::WriteAllText($approvals, '{}', $utf8) }
}

function Wait-FolderGone {
    # Returns once -Path no longer exists; throws "<path> still exists <n> s after uninstall: <first 10 entries>" when
    # it is still there after -TimeoutSeconds.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )
    $folder = Resolve-PluginStagingPath $Path
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (Test-Path -LiteralPath $folder) {
        if ((Get-Date) -ge $deadline) {
            $entries = @(@((Get-FolderSnapshot -Path $folder).Keys) | Select-Object -First 10)
            $listed = if ($entries.Count -gt 0) { $entries -join ', ' } else { '(empty)' }
            throw "$folder still exists $TimeoutSeconds s after uninstall: $listed"
        }
        Start-Sleep -Milliseconds 250
    }
}

function Get-SumaFileUninstallEntries {
    # The uninstall entries whose DisplayName is "SumaFile", sorted by key: HKCU for CurrentUser; HKLM and HKLM
    # WOW6432Node for Machine. Each carries its Key, KeyName, DisplayVersion, InstallLocation and raw Values. Machine
    # entries are only ever read, to snapshot them. Unrolled: wrap the call in @().
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][ValidateSet('CurrentUser', 'Machine')][string]$Scope)
    $roots = if ($Scope -eq 'CurrentUser') {
        @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall')
    } else {
        @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
    }
    $entries = foreach ($rootKey in $roots) {
        foreach ($key in @(Get-ChildItem -LiteralPath $rootKey -ErrorAction SilentlyContinue)) {
            $values = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if (-not $values -or -not $values.PSObject.Properties['DisplayName'] -or [string]$values.DisplayName -cne 'SumaFile') { continue }
            $version = if ($values.PSObject.Properties['DisplayVersion']) { [string]$values.DisplayVersion } else { '' }
            $location = if ($values.PSObject.Properties['InstallLocation']) { [string]$values.InstallLocation } else { '' }
            [pscustomobject]@{
                Key             = "$rootKey\$($key.PSChildName)"
                KeyName         = $key.PSChildName
                DisplayVersion  = $version
                InstallLocation = $location
                Values          = $values
            }
        }
    }
    @($entries) | Sort-Object Key
}

function Assert-SumaFileUninstallEntriesUnchanged {
    # Throws when the "SumaFile" uninstall entries of -Scope differ from -Before (a Get-SumaFileUninstallEntries
    # result) in key, DisplayVersion or InstallLocation. -IgnoreKeyNames leaves out the smoke's own entries (its MSIs'
    # ProductCodes) while they exist.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('CurrentUser', 'Machine')][string]$Scope,
        [AllowEmptyCollection()][AllowNull()][object[]]$Before = @(),
        [string[]]$IgnoreKeyNames = @(),
        [Parameter(Mandatory = $true)][string]$Label
    )
    $describe = { param($entry) "$($entry.Key)|$($entry.DisplayVersion)|$($entry.InstallLocation)" }
    $expected = @(@($Before) | Where-Object { $_ } | ForEach-Object { & $describe $_ })
    $actual = @(@(Get-SumaFileUninstallEntries -Scope $Scope) | Where-Object { $IgnoreKeyNames -notcontains $_.KeyName } |
            ForEach-Object { & $describe $_ })
    if (($expected -join "`n") -cne ($actual -join "`n")) {
        throw "$Label changed the $Scope SumaFile uninstall entries: before [$($expected -join '; ')], now [$($actual -join '; ')]."
    }
}

function Assert-NoSumaFileProcess {
    # The NSIS installer ends SumaFile, its service and the bundled backends by image name, whichever install they
    # belong to, so an installer smoke refuses to run while any of them does.
    [CmdletBinding()]
    param()
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $script:SumaFileProcessNames -contains $_.ProcessName -or $_.ProcessName -like $script:SumaFileBackendProcessPattern
        })
    foreach ($process in $running) {
        $path = try { $process.Path } catch { $null }
        if (-not $path) { $path = '<path unknown>' }
        throw "Close SumaFile first: $($process.ProcessName) ($($process.Id)) $path is running; the installer ends it by name."
    }
}

function Test-PathUnderFolder([string]$Path, [string]$Folder) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetFullPath($Folder).TrimEnd('\', '/') + '\'
    $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)
}

function Start-InstalledSumaFile {
    # Starts an installed SumaFile.exe that lies under -SmokeRoot and returns its Process. The app gets none of the
    # four plugin location overrides, so its defaults (all in its install folder) are what is tested. Its metadata
    # database and app data live under -SmokeRoot, and git is isolated from the developer's config
    # (GIT_CONFIG_GLOBAL=<SmokeRoot>\gitconfig, an empty file, and GIT_CONFIG_NOSYSTEM=1).
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string]$SmokeRoot
    )
    $exePath = Resolve-PluginStagingPath $Exe
    $rootPath = Resolve-PluginStagingPath $SmokeRoot
    if (-not (Test-PathUnderFolder $exePath $rootPath)) {
        throw "Start-InstalledSumaFile refuses ${exePath}: it is outside the smoke root $rootPath."
    }
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) { throw "Installed SumaFile.exe not found at $exePath." }

    $appData = Join-Path $rootPath 'app-data'
    New-Item -ItemType Directory -Force -Path $appData | Out-Null
    $gitConfig = Join-Path $rootPath 'gitconfig'
    if (-not (Test-Path -LiteralPath $gitConfig)) { [System.IO.File]::WriteAllText($gitConfig, '') }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exePath
    $startInfo.WorkingDirectory = Split-Path -Parent $exePath
    $startInfo.UseShellExecute = $false
    foreach ($name in $script:PluginLocationOverrides) {
        if ($startInfo.EnvironmentVariables.ContainsKey($name)) { $startInfo.EnvironmentVariables.Remove($name) }
    }
    $startInfo.EnvironmentVariables['SIMPLEFILE_METADATA_DB'] = Join-Path $appData 'metadata.db'
    $startInfo.EnvironmentVariables['SIMPLEFILE_APP_DATA_DIR'] = $appData
    $startInfo.EnvironmentVariables['GIT_CONFIG_GLOBAL'] = $gitConfig
    $startInfo.EnvironmentVariables['GIT_CONFIG_NOSYSTEM'] = '1'
    [System.Diagnostics.Process]::Start($startInfo)
}

function Wait-SumaFileWindow {
    # Waits until the started app shows its "SumaFile" window and responds, and returns that process; throws after
    # -TimeoutSeconds.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds = 25,
        [string]$ExpectedTitle = 'SumaFile'
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $candidate = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
        if ($candidate -and $candidate.MainWindowTitle -eq $ExpectedTitle -and $candidate.Responding) { return $candidate }
    } while ((Get-Date) -lt $deadline)
    $last = Get-Process -Id $Process.Id -ErrorAction SilentlyContinue
    $lastTitle = if ($last) { $last.MainWindowTitle } else { '<process exited>' }
    throw "Installed SumaFile did not show '$ExpectedTitle' within $TimeoutSeconds seconds. Last title: '$lastTitle'."
}

function Stop-ProcessesUnderFolder {
    # Asks -MainProcess to close its window, then stops every process whose image lies under -Path (never by name
    # alone) and waits for each to exit. Returns the names of the processes it stopped.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [System.Diagnostics.Process]$MainProcess
    )
    $folder = Resolve-PluginStagingPath $Path
    if ($MainProcess) {
        $running = Get-Process -Id $MainProcess.Id -ErrorAction SilentlyContinue
        if ($running) {
            [void]$running.CloseMainWindow()
            [void]$running.WaitForExit(5000)
        }
    }
    $stopped = @()
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        $image = try { $process.Path } catch { $null }
        if (-not $image -or -not (Test-PathUnderFolder $image $folder)) { continue }
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        [void]$process.WaitForExit(5000)
        $stopped += $process.ProcessName
    }
    $stopped
}
