#requires -Version 5.1
<#
Prueba real de recuperación del servicio Windows Sync.
Requiere PowerShell elevado, servicio activo y una carpeta seleccionada.
No modifica credenciales, selecciones ni datos existentes.
#>
[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://localhost:5015',
    [int]$SizeMB = 256,
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
if ($SizeMB -lt 129 -or $SizeMB -gt 2048) { throw 'SizeMB debe estar entre 129 y 2048.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ejecuta PowerShell como administrador: la prueba debe detener y reiniciar el servicio real.'
}
$serviceInfo = Get-CimInstance Win32_Service | Where-Object {
    $_.DisplayName -eq 'Gestor Archivos Sync' -or $_.Name -eq 'Gestor Archivos Sync'
} | Select-Object -First 1
if (-not $serviceInfo) { throw 'No se encontró el servicio Gestor Archivos Sync instalado.' }
$service = Get-Service -Name $serviceInfo.Name
if ($service.Status -ne 'Running' -or $serviceInfo.ProcessId -le 0) {
    throw 'El servicio Windows Sync debe estar ejecutándose antes de la prueba.'
}
$selectionPath = Join-Path $env:ProgramData 'GestorArchivos\Sync\selections.json'
if (-not (Test-Path -LiteralPath $selectionPath)) { throw 'No existe selections.json en ProgramData; no se alterarán selecciones.' }
$selections = @(Get-Content -LiteralPath $selectionPath -Raw | ConvertFrom-Json)
$folder = $selections | Where-Object { [int]$_.Kind -eq 1 -and (Test-Path -LiteralPath $_.Path -PathType Container) } | Select-Object -First 1
if (-not $folder) { throw 'No hay una carpeta seleccionada y existente. Selecciona una carpeta de prueba en Windows Sync antes de ejecutar.' }
$testRoot = [IO.Path]::GetFullPath([string]$folder.Path)
if ((Get-Item -LiteralPath $testRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'No se utilizarán carpetas que sean enlaces o puntos de reanálisis.'
}
$credentialPath = Join-Path $env:ProgramData 'GestorArchivos\Sync\credentials.dat'
if (-not (Test-Path -LiteralPath $credentialPath)) { throw 'No existe la credencial local.' }
Add-Type -AssemblyName System.Security
$token = [Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($credentialPath), $null,
        [Security.Cryptography.DataProtectionScope]::LocalMachine))
if ([string]::IsNullOrWhiteSpace($token)) { throw 'La credencial está vacía.' }
$headers = @{ Authorization = "Bearer $token" }
$base = $ApiBaseUrl.TrimEnd('/')
$device = Invoke-RestMethod -Uri "$base/api/sync/status" -Headers $headers
if (-not $device.carpetaRaizId) { throw 'No se pudo confirmar el dispositivo actual.' }
$profiles = @($env:LOCALAPPDATA,
    "$env:windir\System32\config\systemprofile\AppData\Local",
    "$env:windir\ServiceProfiles\LocalService\AppData\Local",
    "$env:windir\ServiceProfiles\NetworkService\AppData\Local")
$profiles += @(Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath } | ForEach-Object {
    Join-Path $_.LocalPath 'AppData\Local'
})
$checkpointDirs = @($profiles | Where-Object { $_ } | Select-Object -Unique | ForEach-Object {
    Join-Path $_ 'GestorArchivos\Sync\uploads'
})
$name = 'real-service-crash-' + [guid]::NewGuid().ToString('N') + '.bin'
$localPath = Join-Path $testRoot $name
$staging = Join-Path $env:TEMP ($name + '.staging')
$download = Join-Path $env:TEMP ($name + '.download')
$interrupted = $false
$restarted = $false
$uploadId = $null
$checkpointFile = $null
try {
    Write-Host "Generando $SizeMB MB para prueba en: $testRoot"
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $stream = [IO.File]::Open($staging, [IO.FileMode]::CreateNew)
        try {
            $buffer = New-Object byte[] (1MB)
            for ($i = 0; $i -lt $SizeMB; $i++) {
                $random.GetBytes($buffer)
                $stream.Write($buffer, 0, $buffer.Length)
            }
        } finally { $stream.Dispose() }
    } finally { $random.Dispose() }
    $expected = (Get-FileHash -LiteralPath $staging -Algorithm SHA256).Hash
    Move-Item -LiteralPath $staging -Destination $localPath -ErrorAction Stop
    Write-Host 'Esperando primer fragmento confirmado por la API...'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $service.Refresh()
        if ($service.Status -ne 'Running') { throw 'Windows Sync se detuvo antes de la interrupción controlada.' }
        foreach ($dir in $checkpointDirs) {
            if (-not (Test-Path -LiteralPath $dir)) { continue }
            foreach ($candidate in @(Get-ChildItem -LiteralPath $dir -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
                try {
                    $data = Get-Content -LiteralPath $candidate.FullName -Raw -ErrorAction Stop | ConvertFrom-Json
                    if ($data.FileName -cne $name -or -not $data.UploadId) { continue }
                    $uploadId = [guid]$data.UploadId
                    $checkpointFile = $candidate.FullName
                    $status = Invoke-RestMethod -Uri "$base/api/sync/uploads/$uploadId/status" -Headers $headers -ErrorAction Stop
                    $received = @($status.receivedChunks)
                    if ($received.Count -gt 0 -and $received.Count -lt [int]$status.expectedChunkCount) {
                        $live = Get-CimInstance Win32_Service -Filter "Name='$($serviceInfo.Name.Replace("'","''"))'"
                        if (-not $live -or $live.ProcessId -le 0) { throw 'El servicio ya no tiene un proceso activo.' }
                        Write-Host "Interrumpiendo proceso REAL PID $($live.ProcessId), con $($received.Count) fragmento(s) confirmado(s)."
                        Stop-Process -Id $live.ProcessId -Force -ErrorAction Stop
                        $interrupted = $true
                        break
                    }
                    if ($received.Count -ge [int]$status.expectedChunkCount) {
                        throw 'La transferencia terminó antes de interrumpirla. Prueba inconclusa; aumenta -SizeMB.'
                    }
                } catch {
                    if ($_.Exception.Message -like 'La transferencia terminó*') { throw }
                    # Una escritura parcial de checkpoint o consulta transitoria se reintenta.
                }
            }
            if ($interrupted) { break }
        }
        if ($interrupted) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $interrupted) { throw 'No se observó una carga parcial dentro del tiempo establecido; no se detuvo el servicio.' }
    Start-Sleep -Seconds 2
    $service.Refresh()
    if ($service.Status -eq 'Running') {
        throw 'El servicio se reinició automáticamente antes del reinicio controlado. Verifica la recuperación de Windows.'
    }
    Start-Service -Name $serviceInfo.Name
    $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    $restarted = $true
    Write-Host 'Servicio Windows Sync reiniciado. Esperando la asociación remota...'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $remoteId = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $items = @(Invoke-RestMethod -Uri "$base/api/sync/items" -Headers $headers)
        $matches = @($items | Where-Object { $_.rutaLocal -eq $localPath -and $_.archivoRemotoId })
        if ($matches.Count -gt 1) { throw 'Se detectaron asociaciones duplicadas.' }
        if ($matches.Count -eq 1) {
            $remoteId = [guid]$matches[0].archivoRemotoId
            break
        }
        Start-Sleep -Seconds 2
    }
    if (-not $remoteId) { throw 'El cliente no completó la carga después de reiniciarse.' }
    Invoke-WebRequest -Uri "$base/api/sync/read/files/$remoteId/download" -Headers $headers -OutFile $download -UseBasicParsing | Out-Null
    $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "SHA256 diferente. Esperado=$expected; recibido=$actual" }
    if (Test-Path -LiteralPath $checkpointFile) { throw 'El checkpoint de la carga no se eliminó tras finalizar.' }
    Write-Host "APROBADO: proceso Windows Sync real interrumpido y reiniciado; $SizeMB MB íntegros; asociación única. SHA256=$expected"
    Write-Host "Archivo de prueba conservado: $localPath"
} finally {
    $service.Refresh()
    if ($service.Status -ne 'Running') {
        try { Start-Service -Name $serviceInfo.Name; Write-Warning 'Servicio restaurado tras la prueba.' }
        catch { Write-Warning "No se pudo restaurar el servicio: $($_.Exception.Message)" }
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Force }
    if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    if (Test-Path -LiteralPath $localPath) {
        Write-Host "Archivo local conservado para evitar borrado remoto: $localPath"
    }
}
