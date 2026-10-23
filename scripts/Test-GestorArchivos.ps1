[CmdletBinding()]
param(
    [switch]$IntegrationTests,
    [switch]$LargeTransferTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    Write-Host 'Building .NET solution...'
    dotnet build .\GestorArchivos.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build .NET failed.' }
    Push-Location (Join-Path $root 'frontend')
    try {
        Write-Host 'Checking frontend...'
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
        npm run lint
        if ($LASTEXITCODE -ne 0) { throw 'Frontend lint failed.' }
    } finally { Pop-Location }
    if ($IntegrationTests) {
        if ($env:GESTOR_TEST_SQL_ISOLATED -ne '1') {
            throw 'Integration tests require GESTOR_TEST_SQL_ISOLATED=1 and a dedicated local .\\SQLEXPRESS instance. They can modify the GestorArchivosDb_IntegrationTests database.'
        }
        Write-Host 'Running SQL Server integration tests...'
        dotnet test .\tests\GestorArchivos.IntegrationTests\GestorArchivos.IntegrationTests.csproj -c Release --no-build --logger 'trx;LogFileName=integration.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    }
    if ($LargeTransferTests) {
        throw '4 GB transfer tests are not implemented yet; no real user data was touched.'
    }
    Write-Host 'Completed requested checks.'
} finally { Pop-Location }
