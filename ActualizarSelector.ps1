# Ejecutar desde la raíz del repositorio: .\ActualizarSelector.ps1
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'clients\GestorArchivos.Sync.Selector.Windows\GestorArchivos.Sync.Selector.Windows.csproj'
$exe = Join-Path $PSScriptRoot 'clients\GestorArchivos.Sync.Selector.Windows\bin\Release\net8.0-windows\GestorArchivos.Sync.Selector.Windows.exe'
Get-Process -Name 'GestorArchivos.Sync.Selector.Windows' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el selector. El agente no se iniciará.' }
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $registry -Force | Out-Null
Set-ItemProperty -Path $registry -Name 'GestorArchivosSelectorAgent' -Value ('"' + $exe + '" --agent')
Start-Process -FilePath $exe -ArgumentList '--agent'
Write-Host 'Selector actualizado. Puedes abrirlo desde la web.' -ForegroundColor Green
