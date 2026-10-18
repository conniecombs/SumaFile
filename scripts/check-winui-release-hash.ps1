#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

# Load the production function without executing the packaging script.
$scriptPath = Join-Path $PSScriptRoot 'build-winui-release.ps1'
$parseErrors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw "Cannot parse $scriptPath`: $parseErrors" }
$function = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-Sha256Hash' }, $true)
if ($null -eq $function) { throw 'Release script does not define Get-Sha256Hash.' }
. ([scriptblock]::Create($function.Extent.Text))

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('sumafile-hash-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $emptyPath = Join-Path $testRoot 'empty.bin'
    [System.IO.File]::WriteAllBytes($emptyPath, [byte[]]@())
    if ((Get-Sha256Hash $emptyPath -Attempts 1) -cne 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855') {
        throw 'Empty-file SHA-256 does not match the published test vector.'
    }
    $abcPath = Join-Path $testRoot 'file with spaces.bin'
    [System.IO.File]::WriteAllBytes($abcPath, [System.Text.Encoding]::ASCII.GetBytes('abc'))
    $reader = [System.IO.File]::Open($abcPath, 'Open', 'Read', 'ReadWrite')
    try {
        if ((Get-Sha256Hash $abcPath -Attempts 1) -cne 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') {
            throw 'SHA-256 with a concurrent reader does not match the abc test vector.'
        }
    } finally { $reader.Dispose() }
    $failed = $false
    try { Get-Sha256Hash (Join-Path $testRoot 'missing') -Attempts 2 -DelayMilliseconds 1 | Out-Null }
    catch { $failed = $true }
    if (-not $failed) { throw 'Hashing a missing artifact must fail after retries.' }
    Write-Host 'Release hashing passed: empty/abc vectors, concurrent reader, spaced path, and missing artifact.'
} finally {
    $resolved = [System.IO.Path]::GetFullPath($testRoot)
    $tempPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe test cleanup: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
