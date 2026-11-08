param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$fixtureProject = Join-Path $root "src-winui\SimpleFile.Tests\Fixtures\HelloPlugin\SimpleFile.Tests.Fixtures.HelloPlugin.csproj"
$fixtureSourceDir = Split-Path -Parent $fixtureProject
$pluginDir = Join-Path $root "target\fixture-plugins\com.sumafile.hello"
$cargoProfileDir = if ($Configuration -eq "Release") { "release" } else { "debug" }

Push-Location $root
try {
    $cargoArgs = @("build", "-p", "hello-plugin-backend")
    if ($Configuration -eq "Release") { $cargoArgs += "--release" }
    & cargo @cargoArgs
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed with exit code $LASTEXITCODE." }

    & dotnet build $fixtureProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$dll = Get-ChildItem -LiteralPath (Join-Path $fixtureSourceDir "bin\$Configuration") -Recurse -Filter "SimpleFile.Tests.Fixtures.HelloPlugin.dll" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $dll) { throw "Fixture plugin DLL not found under $fixtureSourceDir\bin\$Configuration." }

$exe = Join-Path $root "target\$cargoProfileDir\hello-plugin-backend.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "Backend executable not found at $exe." }

if (Test-Path -LiteralPath $pluginDir) {
    Remove-Item -LiteralPath $pluginDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

Copy-Item -LiteralPath (Join-Path $fixtureSourceDir "plugin.json") -Destination $pluginDir
Copy-Item -LiteralPath $dll.FullName -Destination $pluginDir
$depsJson = [System.IO.Path]::ChangeExtension($dll.FullName, ".deps.json")
if (Test-Path -LiteralPath $depsJson) {
    Copy-Item -LiteralPath $depsJson -Destination $pluginDir
}
Copy-Item -LiteralPath $exe -Destination $pluginDir

Write-Host "Fixture plugin staged at:"
Write-Output $pluginDir
