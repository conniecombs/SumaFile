#requires -Version 5.1
[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 30
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$payloadDir = Join-Path $root "dist\winui\payload"
$servicePath = Join-Path $payloadDir "simplefile-service.exe"

if (-not (Test-Path -LiteralPath $servicePath)) {
    throw "WinUI payload is missing simplefile-service.exe at $servicePath. Run 'npm run build:winui:release' first."
}

$smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("sumafile-winui-file-ops-smoke-" + [System.Guid]::NewGuid().ToString("N"))
$srcDir = Join-Path $smokeRoot "source"
$dstDir = Join-Path $smokeRoot "destination"
$pipeName = "sumafile-file-ops-smoke-" + [System.Guid]::NewGuid().ToString("N")
$authToken = [System.Guid]::NewGuid().ToString("N")
$nextId = 0
$progressEvents = New-Object System.Collections.Generic.List[object]
$tokenFile = Join-Path $smokeRoot "service-token.txt"
$serviceErrorFile = Join-Path $smokeRoot "service-stderr.txt"
$pipe = $null
$service = $null

# The payload's bundled 7-Zip plugin, driven through the payload service.
$sevenZipPluginId = "com.sumafile.sevenzip"
$sevenZipPluginDir = Join-Path $payloadDir "plugins\$sevenZipPluginId"
$sevenZipBackendPath = Join-Path $sevenZipPluginDir "sumafile-sevenzip-backend.exe"
$sevenZipNote = "7-Zip support requires the SumaFile 7-Zip plugin."
$sevenZipPipeName = "sumafile-7z-smoke-" + [System.Guid]::NewGuid().ToString("N")
$sevenZipDataDir = Join-Path $smokeRoot "plugin-data"
$sevenZipBackend = $null
$sevenZipKeepAlive = $null

# The payload's bundled remote plugin, driven directly over its pipe with the service's handshake.
$remotePluginId = "com.sumafile.remote"
$remotePluginDir = Join-Path $payloadDir "plugins\$remotePluginId"
$remoteBackendPath = Join-Path $remotePluginDir "sumafile-remote-backend.exe"
$remotePipeName = "sumafile-remote-smoke-" + [System.Guid]::NewGuid().ToString("N")
$remoteDataDir = Join-Path $smokeRoot "remote-data"
$remoteBackend = $null
$remotePipe = $null

# The payload's bundled Git plugin, driven directly over its pipe like the remote one, against a disposable repository.
# Every git process (the smoke's own and the backend's) gets an empty global config and no system config, so a
# developer's core.hooksPath, commit.gpgsign or init.defaultBranch can never run, prompt or change the result, and
# stops its repository search at the smoke root.
$gitPluginId = "com.sumafile.git"
$gitPluginDir = Join-Path $payloadDir "plugins\$gitPluginId"
$gitBackendPath = Join-Path $gitPluginDir "sumafile-git-backend.exe"
$gitPipeName = "sumafile-git-smoke-" + [System.Guid]::NewGuid().ToString("N")
$gitDataDir = Join-Path $smokeRoot "git-data"
$gitSmokeConfig = Join-Path $smokeRoot "gitconfig"
$gitRepoDir = Join-Path $smokeRoot "git-repo"
$gitPlainDir = Join-Path $smokeRoot "git-plain"
$gitExe = $null
$gitBackend = $null
$gitPipe = $null

function Test-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    return $Value.PSObject.Properties.Name -contains $Name
}

function Assert-Smoke {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function New-SmokeFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.Encoding]::UTF8)
}

function Read-Exact {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][int]$Length
    )

    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -le 0) {
            throw "IPC pipe closed while reading a frame."
        }

        $offset += $read
    }

    return $buffer
}

function Write-Frame {
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)]$Payload
    )

    $json = $Payload | ConvertTo-Json -Depth 40 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $header = [System.BitConverter]::GetBytes([uint32]$bytes.Length)
    $Stream.Write($header, 0, $header.Length)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
}

function Read-Frame {
    param([Parameter(Mandatory = $true)][System.IO.Stream]$Stream)

    $header = Read-Exact -Stream $Stream -Length 4
    $length = [System.BitConverter]::ToUInt32($header, 0)
    if ($length -gt (80 * 1024 * 1024)) {
        throw "IPC frame length $length exceeds the supported maximum."
    }

    $payload = Read-Exact -Stream $Stream -Length ([int]$length)
    $json = [System.Text.Encoding]::UTF8.GetString($payload)
    return $json | ConvertFrom-Json
}

function Read-RpcResponse {
    param([Parameter(Mandatory = $true)][int]$Id)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $message = Read-Frame -Stream $pipe
        if ((Test-JsonProperty -Value $message -Name "method") -and $message.method -eq "operation-progress") {
            $progressEvents.Add($message.params)
            continue
        }

        if ((Test-JsonProperty -Value $message -Name "id") -and [int]$message.id -eq $Id) {
            if ((Test-JsonProperty -Value $message -Name "error") -and $null -ne $message.error) {
                throw "IPC method failed: $($message.error.message)"
            }

            return $message.result
        }
    }

    throw "Timed out waiting for IPC response $Id."
}

function Invoke-Rpc {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        $Params = $null
    )

    $script:nextId += 1
    $request = [ordered]@{
        jsonrpc = "2.0"
        id = $script:nextId
        method = $Method
    }
    if ($null -ne $Params) {
        $request.params = $Params
    }

    Write-Frame -Stream $pipe -Payload $request
    return Read-RpcResponse -Id $script:nextId
}

function Assert-ProgressCompleted {
    param([Parameter(Mandatory = $true)][string]$OperationId)

    $matching = @($progressEvents | Where-Object { $_.operation_id -eq $OperationId })
    Assert-Smoke ($matching.Count -gt 0) "No operation-progress events were emitted for $OperationId."
    Assert-Smoke (($matching | Where-Object { $_.status -eq "completed" } | Select-Object -First 1) -ne $null) "No completed progress event was emitted for $OperationId."
}

function Get-SevenZipCapability {
    $capabilities = Invoke-Rpc "get_archive_capabilities"
    $rows = @($capabilities.formats | Where-Object { $_.extension -eq ".7z" })
    Assert-Smoke ($rows.Count -eq 1) "get_archive_capabilities returned $($rows.Count) .7z rows; expected exactly one."
    return $rows[0]
}

function Get-SmokeTree {
    # Every file under $Root by "/"-separated relative path, with its UTF-8 text.
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $files = [ordered]@{}
    foreach ($file in ([System.IO.Directory]::GetFiles($rootFull, "*", [System.IO.SearchOption]::AllDirectories) | Sort-Object)) {
        $relative = $file.Substring($rootFull.Length).Replace('\', '/')
        $files[$relative] = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
    }
    return $files
}

function Invoke-SevenZipPluginSmoke {
    Assert-Smoke (Test-Path -LiteralPath $sevenZipBackendPath -PathType Leaf) "WinUI payload is missing the bundled 7-Zip plugin backend at $sevenZipBackendPath."
    $manifest = [System.IO.File]::ReadAllText((Join-Path $sevenZipPluginDir "plugin.json"), [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $row = @($manifest.contributions.archiveFormats | Where-Object { $_.extension -eq ".7z" })
    Assert-Smoke ($row.Count -eq 1) "The payload's 7-Zip plugin.json must declare exactly one .7z archive format."
    $row = $row[0]

    New-Item -ItemType Directory -Force -Path $sevenZipDataDir | Out-Null
    $script:sevenZipBackend = Start-Process `
        -FilePath $sevenZipBackendPath `
        -ArgumentList @("--pipe-name", $sevenZipPipeName, "--data-dir", "`"$sevenZipDataDir`"") `
        -WorkingDirectory $sevenZipPluginDir `
        -WindowStyle Hidden `
        -PassThru

    # The backend serves until its last client leaves, so hold one unhandshaken client for the whole smoke, as the
    # host does. Connecting also waits for the pipe to appear.
    $script:sevenZipKeepAlive = [System.IO.Pipes.NamedPipeClientStream]::new(".", $sevenZipPipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $script:sevenZipKeepAlive.Connect($TimeoutSeconds * 1000)

    Invoke-Rpc "register_archive_engine" @{
        pluginId = $sevenZipPluginId
        pipeName = $sevenZipPipeName
        formats = @(@{
            extension = [string]$row.extension
            canList = [bool]$row.canList
            canExtract = [bool]$row.canExtract
            canCreate = [bool]$row.canCreate
            canModify = [bool]$row.canModify
            engineName = [string]$row.engineName
        })
    } | Out-Null

    $registered = Get-SevenZipCapability
    Assert-Smoke ($registered.engine -eq "plugin:$sevenZipPluginId") "The .7z capability row has engine '$($registered.engine)'; expected plugin:$sevenZipPluginId."
    Assert-Smoke ($registered.can_list -and $registered.can_extract -and $registered.can_create -and $registered.can_modify) "The registered .7z capability row must allow list, extract, create and modify."

    # Unicode, spaces, brackets and '&' must survive create, list, browse and extract. Non-ASCII names are built
    # from code points because Windows PowerShell reads this BOM-less script as ANSI.
    $unicodeFolder = "caf" + [char]0x00E9 + " " + [char]0x8CC7 + [char]0x6599
    $punctuatedFile = "literal [brackets] & spaces.txt"
    $sevenZipSource = Join-Path $smokeRoot "sevenzip-source"
    New-SmokeFile -Path (Join-Path $sevenZipSource "docs\a.txt") -Content "alpha from the 7-Zip plugin"
    New-SmokeFile -Path (Join-Path (Join-Path $sevenZipSource $unicodeFolder) "hello.txt") -Content "hello from the 7-Zip plugin"
    New-SmokeFile -Path (Join-Path $sevenZipSource $punctuatedFile) -Content "brackets & spaces"
    $archivePath = Join-Path $smokeRoot "payload-smoke.7z"
    $sourcePaths = @([System.IO.Directory]::GetFileSystemEntries($sevenZipSource) | Sort-Object)

    Invoke-Rpc "create_archive" @{
        paths = $sourcePaths
        archivePath = $archivePath
        format = "7z"
        operationId = "smoke-7z-create"
    } | Out-Null
    Assert-Smoke ([System.IO.File]::Exists($archivePath)) "create_archive did not write $archivePath."
    Assert-ProgressCompleted "smoke-7z-create"

    $expectedFiles = @("docs/a.txt", "$unicodeFolder/hello.txt", $punctuatedFile)
    $listing = Invoke-Rpc "list_archive" @{ path = $archivePath }
    $listedFiles = @($listing.entries | Where-Object { -not $_.is_dir } | ForEach-Object { ([string]$_.path).Replace('\', '/') } | Sort-Object)
    Assert-Smoke ((@($expectedFiles | Sort-Object) -join "|") -ceq ($listedFiles -join "|")) "list_archive returned files [$($listedFiles -join ', ')]; expected [$($expectedFiles -join ', ')]."

    $browse = Invoke-Rpc "list_directory" @{ path = $archivePath; finalEntries = $true }
    $browsed = @($browse.entries | ForEach-Object { [string]$_.name } | Sort-Object)
    $expectedRoot = @("docs", $unicodeFolder, $punctuatedFile) | Sort-Object
    Assert-Smoke ((@($expectedRoot) -join "|") -ceq ($browsed -join "|")) "list_directory on the archive returned [$($browsed -join ', ')]; expected [$($expectedRoot -join ', ')]."

    $extracted = Join-Path $smokeRoot "sevenzip-extracted"
    Invoke-Rpc "extract_archive" @{
        archivePath = $archivePath
        destination = $extracted
        operationId = "smoke-7z-extract"
    } | Out-Null
    Assert-ProgressCompleted "smoke-7z-extract"
    $sourceTree = Get-SmokeTree -Root $sevenZipSource
    $extractedTree = Get-SmokeTree -Root $extracted
    Assert-Smoke ((@($sourceTree.Keys) -join "|") -ceq (@($extractedTree.Keys) -join "|")) "extract_archive wrote [$(@($extractedTree.Keys) -join ', ')]; expected [$(@($sourceTree.Keys) -join ', ')]."
    foreach ($name in $sourceTree.Keys) {
        Assert-Smoke ($sourceTree[$name] -ceq $extractedTree[$name]) "extract_archive wrote different content for $name."
    }

    Invoke-Rpc "unregister_archive_engine" @{ pluginId = $sevenZipPluginId } | Out-Null
    $unregistered = Get-SevenZipCapability
    Assert-Smoke ($unregistered.engine -eq "unavailable") "After unregister the .7z capability row has engine '$($unregistered.engine)'; expected unavailable."
    $note = if (Test-JsonProperty -Value $unregistered -Name "note") { [string]$unregistered.note } else { "" }
    Assert-Smoke ($note -ceq $sevenZipNote) "After unregister the .7z capability note is '$note'; expected '$sevenZipNote'."
    Assert-Smoke (-not ($unregistered.can_list -or $unregistered.can_extract -or $unregistered.can_create -or $unregistered.can_modify)) "After unregister the .7z capability row must allow nothing."

    Write-Host "Payload 7-Zip plugin smoke passed."
}

function Stop-SevenZipBackend {
    if ($script:sevenZipKeepAlive) {
        $script:sevenZipKeepAlive.Dispose()
        $script:sevenZipKeepAlive = $null
    }

    if ($script:sevenZipBackend) {
        $script:sevenZipBackend.Refresh()
        if (-not $script:sevenZipBackend.HasExited) {
            try {
                $script:sevenZipBackend.Kill()
                $script:sevenZipBackend.WaitForExit(5000) | Out-Null
            }
            catch {
                Write-Warning "Could not stop sumafile-sevenzip-backend.exe: $($_.Exception.Message)"
            }
        }

        $script:sevenZipBackend.Dispose()
        $script:sevenZipBackend = $null
    }
}

function Invoke-PluginRpc {
    # One request to a plugin backend on $Stream, as the service would send it. With the service's handshake the
    # backend never sends host.* requests or events; any request that arrives anyway is refused so it cannot hang.
    param(
        [Parameter(Mandatory = $true)][System.IO.Stream]$Stream,
        [Parameter(Mandatory = $true)][string]$Method,
        $Params = $null
    )

    $script:nextId += 1
    $id = $script:nextId
    $request = [ordered]@{ jsonrpc = "2.0"; id = $id; method = $Method }
    if ($null -ne $Params) {
        $request.params = $Params
    }
    Write-Frame -Stream $Stream -Payload $request

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $message = Read-Frame -Stream $Stream
        $isRequest = (Test-JsonProperty -Value $message -Name "method")
        if ($isRequest) {
            if (Test-JsonProperty -Value $message -Name "id") {
                Write-Frame -Stream $Stream -Payload ([ordered]@{
                    jsonrpc = "2.0"
                    id = $message.id
                    error = [ordered]@{ code = -32601; message = "The payload smoke does not serve $($message.method)." }
                })
            }
            continue
        }

        if ((Test-JsonProperty -Value $message -Name "id") -and [int]$message.id -eq $id) {
            if ((Test-JsonProperty -Value $message -Name "error") -and $null -ne $message.error) {
                throw "Plugin method $Method failed: $($message.error.message)"
            }

            return $message.result
        }
    }

    throw "Timed out waiting for the plugin's reply to $Method."
}

function Invoke-RemotePluginSmoke {
    Assert-Smoke (Test-Path -LiteralPath $remoteBackendPath -PathType Leaf) "WinUI payload is missing the bundled remote plugin backend at $remoteBackendPath."
    New-Item -ItemType Directory -Force -Path $remoteDataDir | Out-Null

    # The legacy import reads a disposable database that does not exist, never the user's metadata.db.
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $remoteBackendPath
    $start.Arguments = "--pipe-name $remotePipeName --data-dir `"$remoteDataDir`""
    $start.WorkingDirectory = $remotePluginDir
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables["SIMPLEFILE_METADATA_DB"] = Join-Path $smokeRoot "none.db"
    $script:remoteBackend = [System.Diagnostics.Process]::Start($start)

    $script:remotePipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $remotePipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $script:remotePipe.Connect($TimeoutSeconds * 1000)

    # The service's client name: every host.* call but log fails fast, so nothing waits on this smoke.
    $handshake = Invoke-PluginRpc -Stream $script:remotePipe -Method "plugin.handshake" -Params ([ordered]@{
        pluginId = $remotePluginId
        hostVersion = "1.0.4"
        sdkVersion = "1.0.0"
        client = "simplefile-service"
    })
    Assert-Smoke ($handshake.pluginId -eq $remotePluginId) "The remote backend's handshake names '$($handshake.pluginId)'; expected $remotePluginId."

    $profiles = @(Invoke-PluginRpc -Stream $script:remotePipe -Method "profiles.list")
    Assert-Smoke ($profiles.Count -eq 0) "profiles.list returned $($profiles.Count) profiles before any save; expected none."

    $saved = Invoke-PluginRpc -Stream $script:remotePipe -Method "profiles.save" -Params ([ordered]@{
        profile = [ordered]@{
            name = "Payload smoke SFTP"
            protocol = "sftp"
            host = "127.0.0.1"
            port = 22
            username = "smoke"
            root_path = "/"
            auth_kind = "password"
            insecure_plain_ftp = $false
            passive_mode = $false
        }
    })
    $profileId = [string]$saved.id
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace($profileId)) "profiles.save returned no profile id."
    $expectedTarget = "SumaFile.Remote.$profileId"
    Assert-Smoke ([string]$saved.credential_target -ceq $expectedTarget) "profiles.save returned credential_target '$($saved.credential_target)'; expected $expectedTarget."

    $profiles = @(Invoke-PluginRpc -Stream $script:remotePipe -Method "profiles.list")
    Assert-Smoke ($profiles.Count -eq 1 -and [string]$profiles[0].id -ceq $profileId) "profiles.list after the save returned [$(@($profiles | ForEach-Object { $_.id }) -join ', ')]; expected [$profileId]."

    # No secret was saved, so the delete removes no credential this smoke did not write.
    Invoke-PluginRpc -Stream $script:remotePipe -Method "profiles.delete" -Params ([ordered]@{ profileId = $profileId }) | Out-Null
    $profiles = @(Invoke-PluginRpc -Stream $script:remotePipe -Method "profiles.list")
    Assert-Smoke ($profiles.Count -eq 0) "profiles.list after the delete returned $($profiles.Count) profiles; expected none."

    Write-Host "Payload remote plugin smoke passed."
}

function Stop-RemoteBackend {
    if ($script:remotePipe) {
        $script:remotePipe.Dispose()
        $script:remotePipe = $null
    }

    if ($script:remoteBackend) {
        $script:remoteBackend.Refresh()
        if (-not $script:remoteBackend.HasExited) {
            try {
                $script:remoteBackend.Kill()
                $script:remoteBackend.WaitForExit(5000) | Out-Null
            }
            catch {
                Write-Warning "Could not stop sumafile-remote-backend.exe: $($_.Exception.Message)"
            }
        }

        $script:remoteBackend.Dispose()
        $script:remoteBackend = $null
    }
}

function ConvertTo-NativeArgument {
    # One argument of a Windows command line, quoted the way the C runtime splits it.
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Set-SmokeGitIsolation {
    # The isolation every git process of the Git plugin smoke runs with.
    param([Parameter(Mandatory = $true)][System.Diagnostics.ProcessStartInfo]$StartInfo)

    $StartInfo.EnvironmentVariables["GIT_CONFIG_GLOBAL"] = $gitSmokeConfig
    $StartInfo.EnvironmentVariables["GIT_CONFIG_NOSYSTEM"] = "1"
    $StartInfo.EnvironmentVariables["GIT_CEILING_DIRECTORIES"] = $smokeRoot
    $StartInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0"
}

function Invoke-SmokeGit {
    # Runs git in -Directory with the smoke's isolation and returns its stdout; throws with git's stderr on failure.
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $script:gitExe
    $start.Arguments = (@(@("-C", $Directory) + $Arguments) | ForEach-Object { ConvertTo-NativeArgument $_ }) -join " "
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $start.StandardErrorEncoding = New-Object System.Text.UTF8Encoding $false
    Set-SmokeGitIsolation -StartInfo $start
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch {}
            throw "git $($Arguments -join ' ') did not finish within $TimeoutSeconds seconds."
        }

        $process.WaitForExit()
        if ($process.ExitCode -ne 0) {
            throw "git $($Arguments -join ' ') in $Directory failed with exit code $($process.ExitCode): $($stderr.Result)"
        }

        return $stdout.Result
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-GitPluginSmoke {
    Assert-Smoke (Test-Path -LiteralPath $gitBackendPath -PathType Leaf) "WinUI payload is missing the bundled Git plugin backend at $gitBackendPath."
    $gitCommand = Get-Command "git.exe" -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    Assert-Smoke ($null -ne $gitCommand) "The payload Git plugin smoke needs git.exe on PATH."
    $script:gitExe = $gitCommand.Path
    [System.IO.File]::WriteAllText($gitSmokeConfig, "")

    # A repository on main with its own identity and one commit, and a folder outside any repository.
    New-Item -ItemType Directory -Force -Path $gitRepoDir, $gitPlainDir | Out-Null
    Invoke-SmokeGit -Directory $gitRepoDir -Arguments @("init", "-b", "main") | Out-Null
    Invoke-SmokeGit -Directory $gitRepoDir -Arguments @("config", "user.name", "SumaFile Smoke") | Out-Null
    Invoke-SmokeGit -Directory $gitRepoDir -Arguments @("config", "user.email", "smoke@sumafile.invalid") | Out-Null
    New-SmokeFile -Path (Join-Path $gitRepoDir "README.md") -Content "committed by the payload Git plugin smoke"
    Invoke-SmokeGit -Directory $gitRepoDir -Arguments @("add", "--", "README.md") | Out-Null
    Invoke-SmokeGit -Directory $gitRepoDir -Arguments @("commit", "-m", "initial") | Out-Null
    New-SmokeFile -Path (Join-Path $gitPlainDir "plain.txt") -Content "not in a repository"

    # Launched as the host launches it: its data folder, TMP and TEMP in that folder, and the smoke's git isolation.
    $gitTempDir = Join-Path $gitDataDir "tmp"
    New-Item -ItemType Directory -Force -Path $gitTempDir | Out-Null
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $gitBackendPath
    $start.Arguments = "--pipe-name $gitPipeName --data-dir $(ConvertTo-NativeArgument $gitDataDir)"
    $start.WorkingDirectory = $gitPluginDir
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.EnvironmentVariables["TMP"] = $gitTempDir
    $start.EnvironmentVariables["TEMP"] = $gitTempDir
    Set-SmokeGitIsolation -StartInfo $start
    $script:gitBackend = [System.Diagnostics.Process]::Start($start)

    $script:gitPipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $gitPipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $script:gitPipe.Connect($TimeoutSeconds * 1000)

    # The service's client name, as for the remote plugin: every host.* call but log fails fast, so nothing waits on
    # this smoke.
    $handshake = Invoke-PluginRpc -Stream $script:gitPipe -Method "plugin.handshake" -Params ([ordered]@{
        pluginId = $gitPluginId
        hostVersion = "1.0.4"
        sdkVersion = "1.0.0"
        client = "simplefile-service"
    })
    Assert-Smoke ($handshake.pluginId -eq $gitPluginId) "The Git backend's handshake names '$($handshake.pluginId)'; expected $gitPluginId."

    $status = Invoke-PluginRpc -Stream $script:gitPipe -Method "repo.status" -Params ([ordered]@{ path = $gitRepoDir })
    Assert-Smoke ([bool]$status.is_repo) "repo.status reported $gitRepoDir as no repository."
    Assert-Smoke ([string]$status.branch -ceq "main") "repo.status reported branch '$($status.branch)'; expected main."

    $outside = Invoke-PluginRpc -Stream $script:gitPipe -Method "repo.decorations" -Params ([ordered]@{ folder = $gitPlainDir })
    Assert-Smoke (-not [bool]$outside.in_repository) "repo.decorations reported $gitPlainDir as inside a repository."
    Assert-Smoke (@($outside.rows).Count -eq 0) "repo.decorations returned $(@($outside.rows).Count) rows outside a repository; expected none."

    $stagedFile = Join-Path $gitRepoDir "staged.txt"
    New-SmokeFile -Path $stagedFile -Content "staged by the payload Git plugin smoke"
    $staged = Invoke-PluginRpc -Stream $script:gitPipe -Method "paths.stage" -Params ([ordered]@{
        path = $gitRepoDir
        paths = @($stagedFile)
    })
    Assert-Smoke ([int]$staged.exit_code -eq 0) "paths.stage exited with $($staged.exit_code): $($staged.stderr)"
    $cached = @((Invoke-SmokeGit -Directory $gitRepoDir -Arguments @("diff", "--cached", "--name-only")) -split "`r?`n" | Where-Object { $_ })
    Assert-Smoke (($cached -join "|") -ceq "staged.txt") "git diff --cached lists [$($cached -join ', ')] after paths.stage; expected [staged.txt]."

    Write-Host "Payload Git plugin smoke passed."
}

function Stop-GitBackend {
    if ($script:gitPipe) {
        $script:gitPipe.Dispose()
        $script:gitPipe = $null
    }

    if ($script:gitBackend) {
        $script:gitBackend.Refresh()
        if (-not $script:gitBackend.HasExited) {
            try {
                $script:gitBackend.Kill()
                $script:gitBackend.WaitForExit(5000) | Out-Null
            }
            catch {
                Write-Warning "Could not stop sumafile-git-backend.exe: $($_.Exception.Message)"
            }
        }

        $script:gitBackend.Dispose()
        $script:gitBackend = $null
    }
}

function Remove-SmokeRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or $resolved.Length -le $tempRoot.Length) {
        throw "Refusing to remove smoke directory outside the temp root: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

try {
    New-Item -ItemType Directory -Force -Path $srcDir, $dstDir | Out-Null

    New-SmokeFile -Path (Join-Path $srcDir "copy.txt") -Content "copy from packaged service"
    New-SmokeFile -Path (Join-Path $srcDir "cut-paste.txt") -Content "move from packaged service"
    New-SmokeFile -Path (Join-Path $srcDir "drop.txt") -Content "drop target copy"
    New-SmokeFile -Path (Join-Path $srcDir "conflict.txt") -Content "new conflict content"
    New-SmokeFile -Path (Join-Path $dstDir "conflict.txt") -Content "existing conflict content"
    New-SmokeFile -Path (Join-Path $srcDir "replace.txt") -Content "replacement content"
    New-SmokeFile -Path (Join-Path $dstDir "replace.txt") -Content "old replace content"
    $dropTarget = Join-Path $dstDir "drop-target"
    New-Item -ItemType Directory -Force -Path $dropTarget | Out-Null

    [System.IO.File]::WriteAllText($tokenFile, "$authToken`n", [System.Text.Encoding]::ASCII)
    $service = Start-Process `
        -FilePath $servicePath `
        -ArgumentList @("--pipe-name", $pipeName) `
        -WorkingDirectory $payloadDir `
        -RedirectStandardInput $tokenFile `
        -RedirectStandardError $serviceErrorFile `
        -WindowStyle Hidden `
        -PassThru

    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $pipe.Connect($TimeoutSeconds * 1000)

    Invoke-Rpc "ipc.handshake" @{
        protocolVersion = 1
        clientName = "SumaFile.FileOpsSmoke"
        authToken = $authToken
        binaryHotFrames = $false
    } | Out-Null

    Invoke-Rpc "copy_with_progress" @{
        sources = @((Join-Path $srcDir "copy.txt"))
        destination = $dstDir
        operationId = "smoke-copy"
        conflictAction = "error"
    } | Out-Null
    Assert-Smoke (Test-Path -LiteralPath (Join-Path $dstDir "copy.txt")) "Copy smoke did not create destination file."
    Assert-Smoke ((Get-Content -LiteralPath (Join-Path $dstDir "copy.txt") -Raw) -like "*copy from packaged service*") "Copy smoke wrote unexpected file content."
    Assert-ProgressCompleted "smoke-copy"

    Invoke-Rpc "move_with_progress" @{
        sources = @((Join-Path $srcDir "cut-paste.txt"))
        destination = $dstDir
        operationId = "smoke-cut-paste"
        conflictAction = "error"
    } | Out-Null
    Assert-Smoke (-not (Test-Path -LiteralPath (Join-Path $srcDir "cut-paste.txt"))) "Move smoke left the source file behind."
    Assert-Smoke (Test-Path -LiteralPath (Join-Path $dstDir "cut-paste.txt")) "Move smoke did not create destination file."
    Assert-ProgressCompleted "smoke-cut-paste"

    Invoke-Rpc "copy_with_progress" @{
        sources = @((Join-Path $srcDir "drop.txt"))
        destination = $dropTarget
        operationId = "smoke-drop-target"
        conflictAction = "error"
    } | Out-Null
    Assert-Smoke (Test-Path -LiteralPath (Join-Path $dropTarget "drop.txt")) "Drop-target smoke did not copy into the hovered folder destination."
    Assert-ProgressCompleted "smoke-drop-target"

    $keepBoth = @(Invoke-Rpc "copy_with_progress" @{
        sources = @((Join-Path $srcDir "conflict.txt"))
        destination = $dstDir
        operationId = "smoke-keep-both"
        conflictAction = "keep-both"
    })
    Assert-Smoke ((Get-Content -LiteralPath (Join-Path $dstDir "conflict.txt") -Raw) -like "*existing conflict content*") "Keep-both smoke replaced the existing destination."
    Assert-Smoke ($keepBoth[0].destination -ne (Join-Path $dstDir "conflict.txt")) "Keep-both smoke did not choose a unique destination."
    Assert-Smoke (Test-Path -LiteralPath $keepBoth[0].destination) "Keep-both smoke did not create the unique destination."
    Assert-ProgressCompleted "smoke-keep-both"

    Invoke-Rpc "copy_with_progress" @{
        sources = @((Join-Path $srcDir "replace.txt"))
        destination = $dstDir
        operationId = "smoke-replace"
        conflictAction = "replace"
    } | Out-Null
    Assert-Smoke ((Get-Content -LiteralPath (Join-Path $dstDir "replace.txt") -Raw) -like "*replacement content*") "Replace smoke did not overwrite the existing destination."
    Assert-ProgressCompleted "smoke-replace"

    $listing = Invoke-Rpc "list_directory" @{ path = $dstDir }
    $names = @($listing.entries | ForEach-Object { $_.name })
    foreach ($expected in @("copy.txt", "cut-paste.txt", "drop-target", "conflict.txt", "replace.txt")) {
        Assert-Smoke ($names -contains $expected) "Final listing did not contain $expected."
    }

    Write-Host "WinUI packaged file-operation smoke passed: copy, cut/paste move, drop target, conflicts, and progress."

    Invoke-SevenZipPluginSmoke
    Invoke-RemotePluginSmoke
    Invoke-GitPluginSmoke
}
finally {
    Stop-SevenZipBackend
    Stop-RemoteBackend
    Stop-GitBackend

    if ($pipe) {
        try {
            if ($pipe.IsConnected) {
                try { Invoke-Rpc "ipc.shutdown" | Out-Null } catch {}
            }
        }
        finally {
            $pipe.Dispose()
        }
    }

    if ($service) {
        $service.Refresh()
        if (-not $service.HasExited) {
            if (-not $service.WaitForExit(2000)) {
                try {
                    $service.Kill()
                    $service.WaitForExit(5000) | Out-Null
                }
                catch {
                    Write-Warning "Could not force-stop simplefile-service.exe: $($_.Exception.Message)"
                }
            }
        }

        $service.Dispose()
    }

    Remove-SmokeRoot -Path $smokeRoot
}
