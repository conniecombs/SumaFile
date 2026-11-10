#requires -Version 5.1
# Builds SumaFile's WinUI MSI from packaging/winui/Product.wxs and a heat-harvested payload fragment, the same way for
# the release (scripts/build-winui-release.ps1) and the MSI lifecycle smoke (scripts/smoke-winui-msi-lifecycle.ps1).
# Dot-source it: `. scripts/lib/wix-msi.ps1`. WiX v3 with WixUtilExtension: Product.wxs uses util:RemoveFolderEx.

# WiX 3.14.1 is the first v3 release whose util:RemoveFolderEx does not follow junctions and symlinks; the uninstall
# purge of plugins\ and plugins-data\ runs it on folders a user plugin can write, so an older candle.exe is refused.
$script:MinimumWixVersion = [version]'3.14.1'

function Get-WixToolVersion([string]$Path) {
    # The file version of a WiX tool (candle.exe), or $null when it cannot be read.
    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        return New-Object System.Version $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
    }
    catch {
        return $null
    }
}

function Test-WixBin([string]$Dir) {
    # True when -Dir holds candle.exe, light.exe, heat.exe and WixUtilExtension.dll, and candle.exe is 3.14.1 or later.
    if (-not $Dir) { return $false }
    foreach ($name in @('candle.exe', 'light.exe', 'heat.exe', 'WixUtilExtension.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Dir $name) -PathType Leaf)) { return $false }
    }
    $version = Get-WixToolVersion (Join-Path $Dir 'candle.exe')
    if (-not $version -or $version -lt $script:MinimumWixVersion) {
        Write-Warning "Skipping WiX in ${Dir}: candle.exe is $version; $($script:MinimumWixVersion) or later is required (RemoveFolderEx)."
        return $false
    }
    return $true
}

function Resolve-WixBin {
    # The WiX v3 bin folder holding candle.exe, light.exe, heat.exe and WixUtilExtension.dll, with candle.exe 3.14.1 or
    # later, or $null. PATH, the WIX variable and the fixed install folders come first; only when none of them fits are
    # both Program Files trees searched, which is slow.
    $candidates = New-Object System.Collections.Generic.List[string]
    $onPath = Get-Command 'candle.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $candidates.Add((Split-Path -Parent $onPath.Source)) }
    if ($env:WIX) { $candidates.Add((Join-Path $env:WIX 'bin')) }
    foreach ($rootDir in @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ }) {
        $candidates.Add((Join-Path $rootDir 'WiX Toolset v3.14\bin'))
    }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($dir in $candidates) {
        if ($seen.Add($dir) -and (Test-WixBin $dir)) { return $dir }
    }
    foreach ($rootDir in @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ }) {
        foreach ($candle in @(Get-ChildItem -Path $rootDir -Filter 'candle.exe' -Recurse -ErrorAction SilentlyContinue)) {
            if ($seen.Add($candle.DirectoryName) -and (Test-WixBin $candle.DirectoryName)) { return $candle.DirectoryName }
        }
    }
    return $null
}

function Invoke-WixTool {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )
    Write-Host ""
    Write-Host ("==> {0} {1}" -f $FilePath, ($ArgumentList -join ' '))
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($ArgumentList -join ' ')"
    }
}

function New-WinUIMsi {
    # Compiles Product.wxs and the harvested payload fragment into -MsiPath. The objects go to -WorkDir.
    # -LifecycleSmoke builds the lifecycle smoke's identity (Product.wxs's LifecycleSmoke block): its own UpgradeCode,
    # registry key, Start menu folder and component GUIDs, so the smoke never upgrades or touches a real install.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$WixBin,
        [Parameter(Mandatory = $true)][string]$PayloadDir,
        [Parameter(Mandatory = $true)][string]$HarvestedWxs,
        [Parameter(Mandatory = $true)][string]$IconPath,
        [Parameter(Mandatory = $true)][string]$ProductVersion,
        [Parameter(Mandatory = $true)][string]$WorkDir,
        [Parameter(Mandatory = $true)][string]$MsiPath,
        [switch]$LifecycleSmoke
    )
    $productWxs = Join-Path (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path 'packaging\winui\Product.wxs'
    $candle = Join-Path $WixBin 'candle.exe'
    $light = Join-Path $WixBin 'light.exe'
    $utilExtension = Join-Path $WixBin 'WixUtilExtension.dll'
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

    $candleArgs = @(
        '-nologo',
        '-ext', $utilExtension,
        "-dProductVersion=$ProductVersion",
        "-dPayloadDir=$PayloadDir",
        "-dIconFile=$IconPath"
    )
    if ($LifecycleSmoke) { $candleArgs += '-dLifecycleSmoke=1' }
    $candleArgs += @('-out', (Join-Path $WorkDir '\'), $productWxs, $HarvestedWxs)
    Invoke-WixTool $candle $candleArgs

    # The per-user LocalAppData payload and Windows App SDK localized MUI files trip WiX ICEs that do not apply to
    # this generated installer shape.
    $harvestedObj = Join-Path $WorkDir ([System.IO.Path]::GetFileNameWithoutExtension($HarvestedWxs) + '.wixobj')
    Invoke-WixTool $light @(
        '-nologo',
        '-spdb',
        '-ext', $utilExtension,
        '-sice:ICE03',
        '-sice:ICE38',
        '-sice:ICE64',
        '-sice:ICE91',
        '-out', $MsiPath,
        (Join-Path $WorkDir 'Product.wixobj'),
        $harvestedObj
    )
}

function Invoke-MsiComMember($Target, [string]$Name, [System.Reflection.BindingFlags]$Kind, [object[]]$Arguments) {
    # Late-bound calls on the WindowsInstaller.Installer automation objects, which work the same in Windows
    # PowerShell and pwsh.
    $Target.GetType().InvokeMember($Name, $Kind, $null, $Target, $Arguments)
}

function Get-MsiPropertyValue {
    # A value from an MSI file's Property table, or $null. The database is released before returning, so msiexec can
    # open the file right after.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$MsiPath,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = Invoke-MsiComMember $installer 'OpenDatabase' InvokeMethod @($MsiPath, 0)
        $view = Invoke-MsiComMember $database 'OpenView' InvokeMethod @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'")
        Invoke-MsiComMember $view 'Execute' InvokeMethod $null | Out-Null
        $record = Invoke-MsiComMember $view 'Fetch' InvokeMethod $null
        if ($null -eq $record) { return $null }
        return [string](Invoke-MsiComMember $record 'StringData' GetProperty @(1))
    }
    finally {
        if ($view) { Invoke-MsiComMember $view 'Close' InvokeMethod $null | Out-Null; [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($view) }
        if ($database) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
}

function Test-MsiProductRegistered {
    # True when Windows Installer knows the product for this user or the machine, in any state.
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProductCode)
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try { return ([int](Invoke-MsiComMember $installer 'ProductState' GetProperty @($ProductCode))) -ne -1 }
    finally { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) }
}

function Get-MsiRelatedProductCodes {
    # The ProductCodes Windows Installer has registered under an UpgradeCode, unrolled: wrap the call in @().
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$UpgradeCode)
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        # PowerShell enumerates the returned StringList as it leaves the call, so this is the codes themselves.
        $codes = @(Invoke-MsiComMember $installer 'RelatedProducts' GetProperty @($UpgradeCode) | ForEach-Object { [string]$_ })
        $codes
    }
    finally { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) }
}
