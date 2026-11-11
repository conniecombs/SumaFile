<#
.SYNOPSIS
Builds the plugin template's backend and UI half and stages them as a plugin folder.

.DESCRIPTION
Builds backend\ (cargo) and ui\Example.Plugin (dotnet), then writes a fresh
<cargo target folder>\template-plugins\com.example.template holding plugin.json,
Example.Plugin.dll, Example.Plugin.deps.json and example-plugin-backend.exe. In the
SumaFile repository that is target\template-plugins\com.example.template. Point
SUMAFILE_PLUGIN_ROOTS at the template-plugins folder to load it (see README.md).
Runs on Windows PowerShell 5.1 and PowerShell 7.

.PARAMETER Configuration
Debug (the default) or Release, for both halves.
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$pluginId = "com.example.template"
$templateRoot = $PSScriptRoot
$backendManifest = Join-Path $templateRoot "backend\Cargo.toml"
$uiProject = Join-Path $templateRoot "ui\Example.Plugin\Example.Plugin.csproj"
$uiProjectDir = Split-Path -Parent $uiProject
$cargoProfileDir = if ($Configuration -eq "Release") { "release" } else { "debug" }

$cargoArgs = @("build", "--manifest-path", $backendManifest)
if ($Configuration -eq "Release") { $cargoArgs += "--release" }
& cargo @cargoArgs
if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE." }

# Cargo's target folder: the workspace's (the repository's target\) inside SumaFile, backend\target in a standalone copy.
$metadata = & cargo metadata --format-version 1 --no-deps --manifest-path $backendManifest
if ($LASTEXITCODE -ne 0) { throw "cargo metadata failed with exit code $LASTEXITCODE." }
$targetDir = ($metadata | Out-String | ConvertFrom-Json).target_directory
if (-not $targetDir) { throw "cargo metadata did not report a target directory." }

& dotnet build $uiProject -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

$dll = Get-ChildItem -LiteralPath (Join-Path $uiProjectDir "bin\$Configuration") -Recurse -Filter "Example.Plugin.dll" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $dll) { throw "Example.Plugin.dll not found under $uiProjectDir\bin\$Configuration." }
$depsJson = [System.IO.Path]::ChangeExtension($dll.FullName, ".deps.json")
if (-not (Test-Path -LiteralPath $depsJson)) { throw "Example.Plugin.deps.json not found next to $($dll.FullName)." }

$exe = Join-Path $targetDir "$cargoProfileDir\example-plugin-backend.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Backend executable not found at $exe." }

# A fresh folder each time, so a file a rebuild dropped does not linger (and an old signature.json never survives).
$pluginDir = Join-Path (Join-Path $targetDir "template-plugins") $pluginId
if (Test-Path -LiteralPath $pluginDir) {
    Remove-Item -LiteralPath $pluginDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

Copy-Item -LiteralPath (Join-Path $templateRoot "plugin.json") -Destination $pluginDir
Copy-Item -LiteralPath $dll.FullName -Destination $pluginDir
Copy-Item -LiteralPath $depsJson -Destination $pluginDir
Copy-Item -LiteralPath $exe -Destination $pluginDir

Write-Host "Template plugin staged at:"
Write-Output $pluginDir
