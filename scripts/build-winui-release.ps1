#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$SkipChecks,
    [switch]$SkipSmoke,
    [switch]$SkipInstaller,
    [switch]$RequireInstaller,
    [switch]$RequireUpdaterSignature,
    [switch]$Clean,
    [string]$Configuration = "Release"
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$distRoot = Join-Path $root "dist\winui"
$payloadDir = Join-Path $distRoot "payload"
$iconPath = Join-Path $root "packaging\winui\icon.ico"
$appProject = Join-Path $root "src-winui\SimpleFile.App\SimpleFile.App.csproj"

# Install-BundledPlugins and Assert-BundledPlugins stage and check payload\plugins and its bundled.json.
. (Join-Path $PSScriptRoot "lib\plugin-staging.ps1")
# Resolve-WixBin and New-WinUIMsi build the MSI (WiX v3 with WixUtilExtension).
. (Join-Path $PSScriptRoot "lib\wix-msi.ps1")

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ""
    Write-Host "==> $Message"
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = $root
    )

    Write-Step ("{0} {1}" -f $FilePath, ($ArgumentList -join " "))
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

function Get-ReleaseMetadata {
    $props = Get-Content -LiteralPath (Join-Path $root "src-winui\Directory.Build.props") -Raw
    if ($props -notmatch '<Version>([^<]+)</Version>') {
        throw "Could not read Version from src-winui\Directory.Build.props."
    }
    $numericVersion = $Matches[1]
    $displayVersion = $numericVersion
    if ($props -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
        $displayVersion = $Matches[1]
    }
    $cargoVersions = [ordered]@{}
    foreach ($crate in @("simplefile-core", "simplefile-ipc", "simplefile-service")) {
        $cargo = Get-Content -LiteralPath (Join-Path $root "crates\$crate\Cargo.toml") -Raw
        if ($cargo -notmatch '(?m)^version\s*=\s*"([^"]+)"') {
            throw "Could not read version from crates\$crate\Cargo.toml."
        }
        $cargoVersions[$crate] = $Matches[1]
    }
    $mismatches = $cargoVersions.GetEnumerator() | Where-Object { $_.Value -ne $numericVersion }
    if ($mismatches) {
        $details = ($cargoVersions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join " "
        throw "Version mismatch: $details Directory.Build.props=$numericVersion"
    }
    return [pscustomobject]@{
        Numeric = $numericVersion
        Display = $displayVersion
    }
}

function Find-ServiceExecutable {
    $candidates = @(
        (Join-Path $root "target\release\simplefile-service.exe"),
        (Join-Path $root "target\x86_64-pc-windows-msvc\release\simplefile-service.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Find-PublishDirectory {
    $targetFramework = "net10.0-windows10.0.19041.0"
    $candidates = @(
        (Join-Path $root "src-winui\SimpleFile.App\bin\Release\$targetFramework\win-x64\publish"),
        (Join-Path $root "src-winui\SimpleFile.App\bin\x64\Release\$targetFramework\win-x64\publish")
    )
    $binRoot = Join-Path $root "src-winui\SimpleFile.App\bin"
    if (Test-Path -LiteralPath $binRoot) {
        $candidates += Get-ChildItem -LiteralPath $binRoot -Directory -Recurse -Filter publish |
            Select-Object -ExpandProperty FullName
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate "SumaFile.exe")) {
            return $candidate
        }
        if (Test-Path -LiteralPath (Join-Path $candidate "SimpleFile.App.exe")) {
            return $candidate
        }
        if (Test-Path -LiteralPath (Join-Path $candidate "SimpleFile.exe")) {
            return $candidate
        }
    }
    return $null
}

function Resolve-Tool {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string[]]$CandidateDirs = @()
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    foreach ($dir in $CandidateDirs) {
        $path = Join-Path $dir $Name
        if (Test-Path -LiteralPath $path) {
            return $path
        }
    }

    $searchRoots = @(
        "${env:ProgramFiles(x86)}",
        $env:ProgramFiles
    ) | Where-Object { $_ }
    foreach ($rootDir in $searchRoots) {
        $found = Get-ChildItem -Path $rootDir -Filter $Name -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) {
            return $found.FullName
        }
    }

    return $null
}

function Assert-Payload {
    param([Parameter(Mandatory = $true)][string]$Directory)

    foreach ($required in @("SumaFile.exe", "simplefile-service.exe", "resources.pri", "MainWindow.xbf")) {
        $path = Join-Path $Directory $required
        if (-not (Test-Path -LiteralPath $path)) {
            throw "WinUI payload is missing $required under $Directory."
        }
    }

    # 7-Zip ships as the bundled plugin; the payload must not bring back 1.0.4's tools\7zip.
    $legacySevenZip = Join-Path $Directory "tools\7zip"
    if (Test-Path -LiteralPath $legacySevenZip) {
        throw "WinUI payload must not contain $legacySevenZip; 7-Zip ships as the bundled plugin in plugins\com.sumafile.sevenzip."
    }

    Assert-BundledPlugins -Root (Join-Path $Directory "plugins")

    # Plugin data, approvals, the plugin log and user-installed plugins are the app's own files in its install folder;
    # a payload that carried them would ship one machine's state to every user.
    $appOwned = @(Get-AppOwnedPluginItems -InstallDir $Directory)
    if ($appOwned.Count -gt 0) {
        throw "WinUI payload must not contain app-owned plugin files: $($appOwned -join ', ')"
    }
}

function Get-Sha256Hash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Attempts = 10,
        [int]$DelayMilliseconds = 500
    )

    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, $share)
            try {
                $sha256 = [System.Security.Cryptography.SHA256]::Create()
                try {
                    $hashBytes = $sha256.ComputeHash($stream)
                    return ([System.BitConverter]::ToString($hashBytes) -replace "-", "").ToLowerInvariant()
                }
                finally {
                    $sha256.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }
        }
        catch {
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds $DelayMilliseconds
                continue
            }
            throw
        }
    }
}

function Test-TextContains {
    param(
        [string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    return $Text.IndexOf($Needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-LikelyWinUILockHolder {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolved = $Path
    if (Test-Path -LiteralPath $Path) {
        try {
            $resolved = (Resolve-Path -LiteralPath $Path).Path
        }
        catch {
        }
    }

    $forward = $resolved.Replace('\', '/')
    $processNames = @("SumaFile.exe", "SimpleFile.App.exe", "SimpleFile.exe", "simplefile-service.exe", "sumafile-sevenzip-backend.exe", "sumafile-remote-backend.exe", "sumafile-git-backend.exe")
    $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    if (-not $processes) {
        return @()
    }

    return @(
        $processes | Where-Object {
            $isKnownWinUIProcess = $processNames -contains $_.Name
            $matchesExecutable = (Test-TextContains -Text $_.ExecutablePath -Needle $resolved) -or
                (Test-TextContains -Text $_.ExecutablePath -Needle $forward)
            $matchesCommandLine = (Test-TextContains -Text $_.CommandLine -Needle $resolved) -or
                (Test-TextContains -Text $_.CommandLine -Needle $forward)
            $isKnownWinUIProcess -or $matchesExecutable -or $matchesCommandLine
        } | Select-Object ProcessId, Name, ExecutablePath, CommandLine
    )
}

function Remove-PathWithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Recurse,
        [string]$Context = "path",
        [int]$Attempts = 6,
        [int]$DelayMilliseconds = 750
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $removeArgs = @{
        LiteralPath = $Path
        Force = $true
        ErrorAction = "Stop"
    }
    if ($Recurse) {
        $removeArgs.Recurse = $true
    }

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Remove-Item @removeArgs
            return
        }
        catch {
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds $DelayMilliseconds
                continue
            }

            $lockHolders = @(Get-LikelyWinUILockHolder -Path $Path)
            if ($lockHolders.Count -gt 0) {
                Write-Warning "Possible WinUI payload lock holders:"
                $lockHolders | ForEach-Object {
                    $detail = if ($_.ExecutablePath) { $_.ExecutablePath } else { $_.CommandLine }
                    Write-Warning ("  PID {0} {1} {2}" -f $_.ProcessId, $_.Name, $detail)
                }
            }

            throw "Could not remove $Context at ${Path} after $Attempts attempts. Close any SumaFile window, installer, smoke-test app, Explorer window, or terminal opened inside that path, then rerun. Original error: $($_.Exception.Message)"
        }
    }
}

function New-WinUIPayload {
    param(
        [Parameter(Mandatory = $true)][string]$PublishDir,
        [Parameter(Mandatory = $true)][string]$ServiceExe,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (Test-Path -LiteralPath $Destination) {
        Remove-PathWithRetry -Path $Destination -Recurse -Context "WinUI payload directory"
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $PublishDir "*") -Destination $Destination -Recurse -Force

    $uiExe = Join-Path $Destination "SumaFile.exe"
    foreach ($publishedName in @("SimpleFile.App.exe", "SimpleFile.exe")) {
        $publishedExe = Join-Path $Destination $publishedName
        if ((Test-Path -LiteralPath $publishedExe) -and -not (Test-Path -LiteralPath $uiExe)) {
            Move-Item -LiteralPath $publishedExe -Destination $uiExe -Force
        }
    }

    Copy-Item -LiteralPath $ServiceExe -Destination (Join-Path $Destination "simplefile-service.exe") -Force
    Install-BundledPlugins -Root (Join-Path $Destination "plugins") -Configuration Release
    Assert-Payload $Destination
}

$release = Get-ReleaseMetadata
$version = $release.Display
$numericVersion = $release.Numeric
Write-Host "WinUI release version: $version (numeric $numericVersion)"

if ($RequireUpdaterSignature) {
    if (-not $env:SIMPLEFILE_SIGNING_PRIVATE_KEY) {
        throw "SIMPLEFILE_SIGNING_PRIVATE_KEY is required when -RequireUpdaterSignature is supplied."
    }
    if (-not $env:SIMPLEFILE_UPDATER_PUBLIC_KEY) {
        throw "SIMPLEFILE_UPDATER_PUBLIC_KEY is required when -RequireUpdaterSignature is supplied."
    }
}

if ($Clean -and (Test-Path -LiteralPath $distRoot)) {
    Write-Step "Cleaning $distRoot"
    Remove-PathWithRetry -Path $distRoot -Recurse -Context "WinUI dist directory"
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

if (-not $SkipChecks) {
    Invoke-Native npm @("run", "check:winui-packaging")
    # check:winui runs the C# tests, whose fixtures the test project copies after its build: the Hello plugin and
    # the SFTP test server (build-fixture-plugin.ps1), and the staged remote and Git plugins with their bundled.json
    # (build-plugins.ps1, Debug). The CI winui job runs the same two scripts before check:winui.
    Invoke-Native powershell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts\build-fixture-plugin.ps1")
    Invoke-Native powershell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts\build-plugins.ps1", "-Configuration", "Debug")
    Invoke-Native npm @("run", "check:winui")
}

Write-Step "Build simplefile-service"
Invoke-Native cargo @("build", "-p", "simplefile-service", "--locked", "--release")
Invoke-Native cargo @("build", "-p", "sumafile-sevenzip-backend", "--locked", "--release")
Invoke-Native cargo @("build", "-p", "sumafile-remote-backend", "--locked", "--release")
Invoke-Native cargo @("build", "-p", "sumafile-git-backend", "--locked", "--release")

$serviceExe = Find-ServiceExecutable
if (-not $serviceExe) {
    throw "simplefile-service.exe was not produced. Expected target\release\simplefile-service.exe."
}

Write-Step "Publish WinUI unpackaged host"
Invoke-Native dotnet @(
    "publish", $appProject,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "--nologo"
)

$publishDir = Find-PublishDirectory
if (-not $publishDir) {
    throw "dotnet publish did not produce SumaFile.exe under src-winui\\SimpleFile.App\\bin\\**\\publish."
}

# The App publish does not build plugin projects; Install-BundledPlugins stages each plugin UI from bin\Release.
# The list must name exactly the UI projects of the staging table, so no bundled UI is staged from an old build.
$pluginUiProjects = @(
    "plugins\remote\ui\SumaFile.Remote\SumaFile.Remote.csproj",
    "plugins\git\ui\SumaFile.Git\SumaFile.Git.csproj"
)
$stagedUiProjects = @($script:BundledPluginIds | Where-Object { $script:BundledPluginUiProjects[$_] } |
    ForEach-Object { $script:BundledPluginUiProjects[$_].Project })
if (($pluginUiProjects -join "|") -cne ($stagedUiProjects -join "|")) {
    throw "The release build's plugin UI projects [$($pluginUiProjects -join ', ')] differ from the staging table's [$($stagedUiProjects -join ', ')]."
}
foreach ($pluginUiProject in $pluginUiProjects) {
    Write-Step "Build the plugin UI $(Split-Path -Leaf $pluginUiProject)"
    Invoke-Native dotnet @(
        "build", (Join-Path $root $pluginUiProject),
        "-c", "Release",
        "--nologo"
    )
}

New-WinUIPayload -PublishDir $publishDir -ServiceExe $serviceExe -Destination $payloadDir

$portableZip = Join-Path $distRoot "SumaFile_${version}_x64-winui-portable.zip"
if (Test-Path -LiteralPath $portableZip) {
    Remove-PathWithRetry -Path $portableZip -Context "existing portable ZIP"
}
Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $portableZip -Force
Write-Host "Wrote $portableZip"

$setupName = "SumaFile_${version}_x64-winui-setup.exe"
$setupPath = Join-Path $distRoot $setupName
$msiPath = Join-Path $distRoot "SumaFile_${version}_x64-winui.msi"
$builtSetup = $false
$builtMsi = $false

if (-not $SkipInstaller) {
    $makensis = Resolve-Tool "makensis.exe" @(
        "C:\Program Files (x86)\NSIS",
        "C:\Program Files\NSIS"
    )
    if ($makensis) {
        Write-Step "Build NSIS WinUI setup"
        $nsi = Join-Path $root "packaging\winui\sumafile-winui.nsi"
        $payloadNsis = $payloadDir.Replace('\', '/')
        $setupNsis = $setupPath.Replace('\', '/')
        $iconNsis = $iconPath.Replace('\', '/')
        Invoke-Native $makensis @(
            "/DVERSION=$version",
            "/DNUMERIC_VERSION=$numericVersion",
            "/DPAYLOAD=$payloadNsis",
            "/DOUTFILE=$setupNsis",
            "/DICON=$iconNsis",
            $nsi
        )
        $builtSetup = Test-Path -LiteralPath $setupPath
    }
    else {
        $message = "makensis.exe was not found; skipped NSIS WinUI setup."
        if ($RequireInstaller) { throw $message }
        Write-Warning $message
    }

    # WiX v3 with WixUtilExtension (Product.wxs purges the app-owned plugin items on uninstall); New-WinUIMsi in
    # scripts\lib\wix-msi.ps1 is shared with the MSI lifecycle smoke.
    $wixBin = Resolve-WixBin
    if ($wixBin) {
        Write-Step "Build WiX WinUI MSI"
        $wixOut = Join-Path $distRoot "wix"
        New-Item -ItemType Directory -Force -Path $wixOut | Out-Null
        $harvested = Join-Path $wixOut "harvested.wxs"
        Invoke-Native (Join-Path $wixBin "heat.exe") @(
            "dir", $payloadDir,
            "-nologo",
            "-cg", "SimpleFileWinUIFiles",
            "-gg",
            "-sfrag",
            "-srd",
            "-sreg",
            "-dr", "INSTALLDIR",
            "-var", "var.PayloadDir",
            "-out", $harvested
        )
        $msiBuildPath = Join-Path $distRoot (
            "{0}.tmp-{1}.msi" -f
                [System.IO.Path]::GetFileNameWithoutExtension($msiPath),
                [System.Guid]::NewGuid().ToString("N")
        )
        try {
            New-WinUIMsi -WixBin $wixBin -PayloadDir $payloadDir -HarvestedWxs $harvested -IconPath $iconPath `
                -ProductVersion $numericVersion -WorkDir $wixOut -MsiPath $msiBuildPath
            if (Test-Path -LiteralPath $msiPath) {
                try {
                    Remove-PathWithRetry -Path $msiPath -Context "existing WinUI MSI"
                }
                catch {
                    throw "Could not replace existing WinUI MSI at ${msiPath}. Close any installer or smoke-test process using it, then rerun. Original error: $($_.Exception.Message)"
                }
            }
            Move-Item -LiteralPath $msiBuildPath -Destination $msiPath -Force
        }
        finally {
            if (Test-Path -LiteralPath $msiBuildPath) {
                Remove-Item -LiteralPath $msiBuildPath -Force -ErrorAction SilentlyContinue
            }
        }
        $builtMsi = Test-Path -LiteralPath $msiPath
    }
    else {
        $message = "WiX v3.14.1 or later (candle/heat/light with WixUtilExtension.dll) was not found; skipped WinUI MSI."
        if ($RequireInstaller) { throw $message }
        Write-Warning $message
    }
}

$signature = ""
$setupSha256 = ""
$setupSize = 0
if ($builtSetup) {
    $setupItem = Get-Item -LiteralPath $setupPath
    $setupSize = $setupItem.Length
    $setupSha256 = Get-Sha256Hash -Path $setupPath
}
$signingKey = $env:SIMPLEFILE_SIGNING_PRIVATE_KEY
$requireUpdaterSignatureValue = if ($RequireUpdaterSignature) { "true" } else { "false" }
if ($builtSetup -and $signingKey) {
    Write-Step "Sign WinUI setup for latest-winui.json"
    try {
        $sigPath = "$setupPath.sig"
        $signArgs = @(
            "scripts/sign-update-payload.mjs",
            "--file=$setupPath",
            "--out=$sigPath",
            "--require-public-key=$requireUpdaterSignatureValue"
        )
        Invoke-Native node $signArgs
        $signature = (Get-Content -LiteralPath $sigPath -Raw).Trim()
    }
    catch {
        Write-Warning "WinUI updater signing failed: $($_.Exception.Message)"
        if ($RequireInstaller -or $RequireUpdaterSignature) { throw }
    }
}
elseif ($RequireUpdaterSignature) {
    throw "NSIS setup was built but could not be signed because SIMPLEFILE_SIGNING_PRIVATE_KEY is not set."
}

$latestArgs = @(
    "scripts/write-latest-winui.mjs",
    "--version=$version",
    "--setup=$setupName",
    "--out=$distRoot",
    "--signature=$signature",
    "--sha256=$setupSha256",
    "--size=$setupSize",
    "--channel=stable",
    "--require-installable=$requireUpdaterSignatureValue"
)
Invoke-Native node $latestArgs

if (-not $SkipSmoke) {
    Invoke-Native powershell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts\smoke-winui-startup.ps1")
    Invoke-Native powershell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts\smoke-winui-file-ops.ps1")
    if ($builtMsi) {
        Invoke-Native powershell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "scripts\smoke-winui-msi.ps1")
    }
}

Write-Step "WinUI artifacts"
Get-ChildItem -LiteralPath $distRoot -File | Sort-Object Name | ForEach-Object {
    "{0}`t{1:N1} MB" -f $_.Name, ($_.Length / 1MB)
}

if ($RequireInstaller -and -not $builtSetup) {
    throw "NSIS WinUI setup was required but not produced."
}
if ($RequireInstaller -and -not $builtMsi) {
    throw "WinUI MSI was required but not produced."
}
