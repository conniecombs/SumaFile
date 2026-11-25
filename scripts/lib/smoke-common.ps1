# Helpers shared by the release build and the smoke scripts. Dot-source it after $root is set:
# Invoke-Native runs in $root unless told otherwise.

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ""
    Write-Host "==> $Message"
}

function Invoke-Native {
    # Runs a program, with its command line as a step heading, and throws when it exits with an error.
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

function ConvertTo-NativeArgument {
    # One argument of a Windows command line: quoted when it holds whitespace or a quote, with the backslashes before
    # a quote, and a trailing run of them, doubled (the CommandLineToArgvW rules the C runtime and git follow).
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', [System.Text.RegularExpressions.MatchEvaluator] { param($match) $match.Groups[1].Value + $match.Groups[1].Value + '\"' })
    $escaped = [regex]::Replace($escaped, '(\\+)$', [System.Text.RegularExpressions.MatchEvaluator] { param($match) $match.Groups[1].Value + $match.Groups[1].Value })
    return '"' + $escaped + '"'
}

function Invoke-GitProcess {
    # Runs git in -WorkingDirectory and returns its standard output. Throws with git's standard error when git fails,
    # and when it does not finish in time. -Environment sets variables for that process; a $null value removes one.
    param(
        [Parameter(Mandatory = $true)][string]$GitExe,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [hashtable]$Environment = @{},
        [int]$TimeoutSeconds = 30
    )

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $GitExe
    $start.WorkingDirectory = $WorkingDirectory
    $start.Arguments = ($Arguments | ForEach-Object { ConvertTo-NativeArgument -Value $_ }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $start.StandardErrorEncoding = New-Object System.Text.UTF8Encoding $false
    foreach ($name in $Environment.Keys) {
        if ($null -eq $Environment[$name]) {
            if ($start.EnvironmentVariables.ContainsKey($name)) { $start.EnvironmentVariables.Remove($name) }
        } else {
            $start.EnvironmentVariables[$name] = [string]$Environment[$name]
        }
    }

    $line = 'git ' + ($Arguments -join ' ')
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch { Write-Warning "Could not stop '$line': $($_.Exception.Message)" }
            throw "'$line' did not finish within $TimeoutSeconds s in $WorkingDirectory."
        }

        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "'$line' failed in $WorkingDirectory (exit $($process.ExitCode)): $($stderr.Result.Trim())"
        }

        return $stdout.Result
    }
    finally {
        $process.Dispose()
    }
}

function Start-BackgroundNative {
    # Starts a program and returns at once, so the caller can do other work while it runs. Its output is kept, not
    # printed: Wait-BackgroundNative prints it in one piece and throws when the program failed. A caller that fails
    # in between passes the job to Stop-BackgroundNative so the program is not left running.
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = $root
    )

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $FilePath
    $start.Arguments = ($ArgumentList | ForEach-Object { ConvertTo-NativeArgument -Value $_ }) -join ' '
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    Write-Step ("{0}, in the background: {1} {2}" -f $Title, $FilePath, ($ArgumentList -join " "))
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($start)
    [pscustomobject]@{
        Title        = $Title
        FilePath     = $FilePath
        ArgumentList = $ArgumentList
        Process      = $process
        Output       = $process.StandardOutput.ReadToEndAsync()
        Errors       = $process.StandardError.ReadToEndAsync()
        Watch        = $watch
    }
}

function Wait-BackgroundNative {
    # Waits for a program started by Start-BackgroundNative, prints what it wrote, and throws when it failed.
    param([Parameter(Mandatory = $true)]$Job)

    try {
        $Job.Process.WaitForExit()
        $Job.Watch.Stop()
        Write-Step ("{0}: finished after {1:N0} s, its output follows" -f $Job.Title, $Job.Watch.Elapsed.TotalSeconds)
        foreach ($text in @($Job.Output.Result, $Job.Errors.Result)) {
            if ($text -and $text.Trim()) { Write-Host $text.TrimEnd() }
        }
        if ($Job.Process.ExitCode -ne 0) {
            throw "Command failed with exit code $($Job.Process.ExitCode): $($Job.FilePath) $($Job.ArgumentList -join ' ')"
        }
    }
    finally {
        $Job.Process.Dispose()
    }
}

function Stop-BackgroundNative {
    # Ends a program started by Start-BackgroundNative that was not waited for; nothing happens for $null or for a
    # job that Wait-BackgroundNative has already taken.
    param($Job)

    if (-not $Job) { return }
    try {
        if (-not $Job.Process.HasExited) { $Job.Process.Kill() }
    }
    catch {
        # Already gone, or already disposed by Wait-BackgroundNative.
    }
    try { $Job.Process.Dispose() } catch { }
}

function Get-SumaFileDisplayVersion {
    # The version users see, from src-winui\Directory.Build.props: InformationalVersion, or Version without one.
    param([Parameter(Mandatory = $true)][string]$Root)

    $props = Get-Content -LiteralPath (Join-Path $Root "src-winui\Directory.Build.props") -Raw
    if ($props -match '<InformationalVersion>([^<]+)</InformationalVersion>') {
        return $Matches[1]
    }
    if ($props -match '<Version>([^<]+)</Version>') {
        return $Matches[1]
    }
    throw "Could not read InformationalVersion or Version from src-winui\Directory.Build.props."
}

function Find-NewestWinUISetup {
    # The newest NSIS setup in the release folder, or nothing.
    param([Parameter(Mandatory = $true)][string]$BundleDir)

    Get-ChildItem -Path $BundleDir -Filter "SumaFile_*_x64-winui-setup.exe" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Resolve-NewInstaller {
    # The installer under test: -Explicit when given, else the newest setup in -BundleDir.
    param(
        [string]$Explicit,
        [Parameter(Mandatory = $true)][string]$BundleDir
    )

    if ($Explicit) {
        return (Resolve-Path -LiteralPath $Explicit -ErrorAction Stop).Path
    }

    $installer = Find-NewestWinUISetup -BundleDir $BundleDir
    if (-not $installer) {
        throw "No WinUI NSIS installer found in $BundleDir. Run 'npm run build:winui:release' first."
    }

    return $installer.FullName
}

# The installer smokes. Get-WinUIInstall needs lib\plugin-staging.ps1 (Get-SumaFileUninstallEntries).

function Get-WinUIInstall {
    @(Get-SumaFileUninstallEntries -Scope CurrentUser) | Select-Object -First 1
}

function Test-SameFolder([string]$Left, [string]$Right) {
    $a = [System.IO.Path]::GetFullPath($Left.Trim().Trim('"')).TrimEnd('\')
    $b = [System.IO.Path]::GetFullPath($Right.Trim().Trim('"')).TrimEnd('\')
    [string]::Equals($a, $b, [System.StringComparison]::OrdinalIgnoreCase)
}

function Find-WinUIExecutable {
    # SumaFile.exe of the install the smoke made. Without -Required a missing executable gives $null.
    param($Installed, [Parameter(Mandatory = $true)][string]$InstallDir, [switch]$Required)

    # Only the HKCU entry's InstallLocation, and only when it is this smoke's folder: the smoke never launches another
    # install's executable.
    if (-not $Installed.InstallLocation -or -not (Test-SameFolder $Installed.InstallLocation $InstallDir)) {
        throw "The SumaFile uninstall entry's InstallLocation is '$($Installed.InstallLocation)'; expected $InstallDir."
    }
    $exe = Join-Path $InstallDir "SumaFile.exe"
    if (Test-Path -LiteralPath $exe -PathType Leaf) { return $exe }
    if ($Required) { throw "Installed SumaFile.exe was not found at $exe." }
    return $null
}

function Invoke-Uninstall($installed) {
    $uninstallCommand = $installed.Values.QuietUninstallString
    if (-not $uninstallCommand) {
        $uninstallCommand = $installed.Values.UninstallString
    }

    $uninstallerPath = $null
    if ($uninstallCommand -match '^\s*"([^"]+)"') {
        $uninstallerPath = $Matches[1]
    } elseif ($uninstallCommand) {
        $uninstallerPath = ($uninstallCommand -split "\s+", 2)[0]
    }

    if (-not $uninstallerPath -or -not (Test-Path -LiteralPath $uninstallerPath)) {
        throw "Could not find SumaFile uninstaller. UninstallString: '$uninstallCommand'."
    }

    $uninstall = Start-Process -FilePath $uninstallerPath -ArgumentList "/S" -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "WinUI NSIS uninstall failed with exit code $($uninstall.ExitCode)."
    }
}

function Wait-PathsExist([string[]]$Paths, [int]$Seconds, [string]$Because) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $missing = @($Paths | Where-Object { -not (Test-Path -LiteralPath $_) })
        if ($missing.Count -eq 0) { return }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    throw "$Because Missing after $Seconds s: $($missing -join ', ')"
}
