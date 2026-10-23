#requires -Version 5.1
<#
.SYNOPSIS
  End-to-end crash/restart of a separate PowerShell upload worker against the
  real Sync API. This validates durable server chunks, atomic completion,
  downloaded SHA256 and uniqueness. It does NOT launch the Windows Sync agent.
.EXAMPLE
  .\scripts\Test-SyncCrashResume.ps1 -ApiBaseUrl http://localhost:5000 -DeviceToken $token -FolderId $folderId
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ApiBaseUrl,
    [Parameter(Mandatory=$true)][string]$DeviceToken,
    [Parameter(Mandatory=$true)][guid]$FolderId
)
$ErrorActionPreference = 'Stop'
$base = $ApiBaseUrl.TrimEnd('/')
$headers = @{ Authorization = "Bearer $DeviceToken" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("GestorSyncE2E-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$name = "crash-resume-" + [guid]::NewGuid().ToString('N') + ".bin"
$localPath = "C:\\SyncE2E\\" + $name
$source = Join-Path $work $name
$download = Join-Path $work 'download.bin'
$workerFile = Join-Path $work 'worker.ps1'
$checkpoint = Join-Path $work 'checkpoint.txt'
$worker = $null
$uploadId = $null
$success = $false
try {
    $size = 256MB
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $stream = [IO.File]::Open($source, [IO.FileMode]::CreateNew)
        try {
            $buffer = New-Object byte[] (1MB)
            for ($i = 0; $i -lt 256; $i++) {
                $random.GetBytes($buffer)
                $stream.Write($buffer, 0, $buffer.Length)
            }
        } finally { $stream.Dispose() }
    } finally { $random.Dispose() }
    $expected = (Get-FileHash $source -Algorithm SHA256).Hash
    $initBody = @{ folderId = $FolderId; fileName = $name; fileSize = $size } | ConvertTo-Json
    $session = Invoke-RestMethod -Method Post -Uri "$base/api/sync/uploads/initialize" -Headers $headers -ContentType 'application/json' -Body $initBody
    $uploadId = [guid]$session.uploadId
    if ($session.chunkSize -ne 64MB -or $session.expectedChunkCount -ne 4) {
        throw "Tamaño de fragmentos inesperado: $($session.chunkSize)"
    }
    # The worker is a distinct OS process. Kill it immediately after it
    # acknowledges the first uploaded chunk, then resume from the parent.
    @'
param([string]$Base,[string]$Token,[string]$Id,[string]$Source,[string]$Marker)
$ErrorActionPreference = 'Stop'
$client = [Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$Token)
try {
    $stream = [IO.File]::OpenRead($Source)
    try {
        $buffer = New-Object byte[] (64MB)
        $read = 0
        while ($read -lt $buffer.Length) {
            $n = $stream.Read($buffer,$read,$buffer.Length-$read)
            if ($n -eq 0) { throw 'Unexpected EOF' }
            $read += $n
        }
        $payload = [Net.Http.ByteArrayContent]::new($buffer)
        try {
            $response = $client.PutAsync("$Base/api/sync/uploads/$Id/chunks/0",$payload).GetAwaiter().GetResult()
            try { $response.EnsureSuccessStatusCode() | Out-Null }
            finally { $response.Dispose() }
        } finally { $payload.Dispose() }
    } finally { $stream.Dispose() }
    [IO.File]::WriteAllText($Marker,'first-chunk-uploaded')
    Start-Sleep -Seconds 120
} finally { $client.Dispose() }
'@ | Set-Content -LiteralPath $workerFile -Encoding UTF8
    $powershell = (Get-Process -Id $PID).Path
    $worker = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',"`"$workerFile`"",'-Base',"`"$base`"",'-Token',"`"$DeviceToken`"",'-Id',"`"$uploadId`"",'-Source',"`"$source`"",'-Marker',"`"$checkpoint`"") -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    while (-not (Test-Path $checkpoint)) {
        if ($worker.HasExited) { throw "El proceso de transferencia finalizó con código $($worker.ExitCode)." }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Timeout esperando el primer fragmento.' }
        Start-Sleep -Milliseconds 250
        $worker.Refresh()
    }
    Stop-Process -Id $worker.Id -Force
    $worker.WaitForExit()
    $worker = $null
    $status = Invoke-RestMethod -Method Get -Uri "$base/api/sync/uploads/$uploadId/status" -Headers $headers
    if (@($status.receivedChunks).Count -ne 1 -or $status.receivedChunks[0] -ne 0) {
        throw 'El servidor no conservó el primer fragmento tras matar el proceso.'
    }
    $client = [Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    $client.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$DeviceToken)
    try {
        $stream = [IO.File]::OpenRead($source)
        try {
            $buffer = New-Object byte[] (64MB)
            for ($index = 1; $index -lt 4; $index++) {
                $stream.Position = [long]$index * 64MB
                $read = 0
                while ($read -lt $buffer.Length) {
                    $n = $stream.Read($buffer,$read,$buffer.Length-$read)
                    if ($n -eq 0) { throw "Unexpected EOF in chunk $index" }
                    $read += $n
                }
                $payload = [Net.Http.ByteArrayContent]::new($buffer)
                try {
                    $response = $client.PutAsync("$base/api/sync/uploads/$uploadId/chunks/$index",$payload).GetAwaiter().GetResult()
                    try { $response.EnsureSuccessStatusCode() | Out-Null }
                    finally { $response.Dispose() }
                } finally { $payload.Dispose() }
            }
        } finally { $stream.Dispose() }
        $body = @{ localPath = $localPath } | ConvertTo-Json
        $result = Invoke-RestMethod -Method Post -Uri "$base/api/sync/files/atomic/uploads/$uploadId/complete" -Headers $headers -ContentType 'application/json' -Body $body
        $remoteId = [guid]$result.remoteFileId
        if ($remoteId -eq [guid]::Empty) { throw 'No se recibió remoteFileId.' }
        Invoke-WebRequest -Uri "$base/api/sync/read/files/$remoteId/download" -Headers $headers -OutFile $download -UseBasicParsing | Out-Null
        $actual = (Get-FileHash $download -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "SHA256 no coincide: esperado $expected; recibido $actual" }
        $items = Invoke-RestMethod -Method Get -Uri "$base/api/sync/items" -Headers $headers
        $matches = @($items | Where-Object { $_.rutaLocal -eq $localPath })
        if ($matches.Count -ne 1) { throw "Asociaciones duplicadas o ausentes: $($matches.Count)" }
        $success = $true
        Write-Host "APROBADO: proceso interrumpido, fragmentos reanudados, 256 MB íntegros, una asociación. SHA256=$expected"
    } finally { $client.Dispose() }
} finally {
    if ($null -ne $worker -and -not $worker.HasExited) { Stop-Process -Id $worker.Id -Force }
    if (-not $success -and $null -ne $uploadId) {
        try { Invoke-RestMethod -Method Delete -Uri "$base/api/sync/uploads/$uploadId" -Headers $headers | Out-Null } catch {}
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
