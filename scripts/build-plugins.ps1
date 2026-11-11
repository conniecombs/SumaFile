#requires -Version 5.1
# Builds and stages SumaFile's bundled plugins ($script:BundledPluginIds in lib\plugin-staging.ps1:
# com.sumafile.sevenzip, com.sumafile.remote and com.sumafile.git) into a plugins root with its bundled.json.
# Debug builds of SimpleFile.App copy target\plugins into the app's plugins folder (CopyBundledPlugins).
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    # Relative paths resolve against the repository root.
    [string]$Destination = 'target\plugins',
    # Stage what target\<debug|release> already holds instead of running cargo first.
    [switch]$SkipCargo,
    # Stage what the plugin UI projects' bin\<Configuration> already hold instead of running dotnet build first.
    [switch]$SkipDotnet
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'lib\plugin-staging.ps1')

Push-Location -LiteralPath $root
try {
    if (-not $SkipCargo) {
        $cargoArgs = @('build', '--locked', '-p', 'sumafile-sevenzip-backend', '-p', 'sumafile-remote-backend', '-p', 'sumafile-git-backend')
        if ($Configuration -eq 'Release') { $cargoArgs += '--release' }
        & cargo @cargoArgs
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE." }
    }
    if (-not $SkipDotnet) {
        # Every bundled plugin with a C# UI, in staging order (the table itself is unordered). To the host, so the
        # script's output stays the staged root alone.
        foreach ($id in $script:BundledPluginIds) {
            $ui = $script:BundledPluginUiProjects[$id]
            if (-not $ui) { continue }
            $project = $ui.Project.Replace('\', '/')
            & dotnet build $project -c $Configuration --nologo | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "dotnet build of $(Split-Path -Leaf $project) failed with exit code $LASTEXITCODE." }
        }
    }
} finally { Pop-Location }

$destinationPath = if ([System.IO.Path]::IsPathRooted($Destination)) { $Destination } else { Join-Path $root $Destination }
$destinationPath = [System.IO.Path]::GetFullPath($destinationPath).TrimEnd('\', '/')
Install-BundledPlugins -Root $destinationPath -Configuration $Configuration
Assert-BundledPlugins -Root $destinationPath
Write-Host "Bundled plugins verified: $($script:BundledPluginIds -join ', ')"
Write-Output $destinationPath
