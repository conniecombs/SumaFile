#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PreviousRef = $env:SUMAFILE_PREVIOUS_REF,
    [string]$NewInstaller,
    [switch]$KeepPreviousWorktree
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$bundleDir = Join-Path $root "dist\winui"
$previousWorktree = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-previous-ref-" + [System.Guid]::NewGuid().ToString("N"))
$createdWorktree = $false

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = $root
    )

    Write-Host ""
    Write-Host "==> $FilePath $($ArgumentList -join ' ')"
    Push-Location -LiteralPath $WorkingDirectory
    try {
        & $FilePath @ArgumentList
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Resolve-NewInstaller {
    if ($NewInstaller) {
        return (Resolve-Path -LiteralPath $NewInstaller -ErrorAction Stop).Path
    }

    $installer = Get-ChildItem -Path $bundleDir -Filter "SumaFile_*_x64-winui-setup.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $installer) {
        throw "No current WinUI NSIS installer found in $bundleDir. Run 'npm run build:winui:release' first."
    }

    return $installer.FullName
}

function Resolve-PreviousInstaller {
    param([Parameter(Mandatory = $true)][string]$Worktree)

    $installer = Get-ChildItem -Path (Join-Path $Worktree "dist\winui") -Filter "SumaFile_*_x64-winui-setup.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $installer) {
        throw "Previous ref did not produce a WinUI NSIS installer under $Worktree\dist\winui."
    }

    return $installer.FullName
}

function Remove-PreviousWorktree {
    if (-not $createdWorktree -or $KeepPreviousWorktree) {
        return
    }

    $resolved = Resolve-Path -LiteralPath $previousWorktree -ErrorAction SilentlyContinue
    if (-not $resolved) {
        return
    }

    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $fullPath = [System.IO.Path]::GetFullPath($resolved.Path)
    if (-not $fullPath.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove previous-ref worktree outside ${tempRoot}: $fullPath"
    }

    Invoke-Native git @("worktree", "remove", "--force", $fullPath)
}

if (-not $PreviousRef) {
    throw "Pass -PreviousRef <git-ref> or set SUMAFILE_PREVIOUS_REF before running this smoke test."
}

$newInstallerPath = Resolve-NewInstaller

try {
    Invoke-Native git @("worktree", "add", "--detach", $previousWorktree, $PreviousRef)
    $createdWorktree = $true

    Invoke-Native powershell @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        "scripts\build-winui-release.ps1",
        "-SkipChecks",
        "-SkipSmoke",
        "-RequireInstaller"
    ) -WorkingDirectory $previousWorktree

    $previousInstallerPath = Resolve-PreviousInstaller -Worktree $previousWorktree
    Invoke-Native powershell @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        (Join-Path $root "scripts\smoke-winui-upgrade.ps1"),
        "-PreviousInstaller",
        $previousInstallerPath,
        "-NewInstaller",
        $newInstallerPath
    )
}
finally {
    Remove-PreviousWorktree
}
