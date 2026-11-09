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
$script:BundledPluginIds = @('com.sumafile.sevenzip', 'com.sumafile.remote')
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

# Per bundled plugin: its backend exe (named when that source is missing) and the cargo package that builds it.
$script:BundledPluginBackends = @{
    'com.sumafile.sevenzip' = @{ Exe = $script:SevenZipBackendExe; Package = 'sumafile-sevenzip-backend' }
    'com.sumafile.remote'   = @{ Exe = $script:RemoteBackendExe; Package = 'sumafile-remote-backend' }
}

# Per bundled plugin with a C# UI: its project (named, with the build command, when a UI source is missing) and the
# staged names that build produces.
$script:BundledPluginUiProjects = @{
    'com.sumafile.remote' = @{ Project = $script:RemoteUiProject; Files = $script:RemoteUiFiles }
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
        default { throw "Unknown bundled plugin $Id." }
    }
}

function Get-BundledPluginFreshnessFiles {
    # The staged names Assert-BundledPluginsFresh compares with their sources: the files a rebuild changes.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Id)
    switch -CaseSensitive ($Id) {
        'com.sumafile.sevenzip' { return @($script:SevenZipBackendExe, 'plugin.json') }
        'com.sumafile.remote' { return @($script:RemoteBackendExe, 'SumaFile.Remote.dll', 'SumaFile.Remote.Model.dll', 'plugin.json') }
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
