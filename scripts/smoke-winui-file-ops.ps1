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
}
finally {
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
