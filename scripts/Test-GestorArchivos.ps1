[CmdletBinding()]
param(
    [switch]$IntegrationTests,
    [switch]$LargeTransferTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    $runningClients = @(Get-Process -Name 'GestorArchivos.Sync.Windows', 'GestorArchivos.Sync.Selector.Windows' -ErrorAction SilentlyContinue)
    if ($runningClients.Count -gt 0) {
        Write-Host 'Windows clients are running. Building API and integration test projects without replacing active Windows binaries...'
        dotnet build .\tests\GestorArchivos.IntegrationTests\GestorArchivos.IntegrationTests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'API/integration-test project build failed.' }
        Write-Warning 'Windows client compilation skipped because the applications are running. Full solution compilation remains required in CI or after closing the clients.'
    } else {
        Write-Host 'Building full .NET solution...'
        dotnet build .\GestorArchivos.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Full .NET solution build failed.' }
    }
    Push-Location (Join-Path $root 'frontend')
    try {
        Write-Host 'Checking frontend...'
        # Reuse installed dependencies during local checks. npm ci deletes and
        # reinstalls node_modules, which fails on Windows when the dev server
        # has loaded native modules (for example Rolldown).
        $requiredTools = @('node_modules/.bin/vite.cmd', 'node_modules/.bin/tsc.cmd', 'node_modules/.bin/eslint.cmd')
        $missingTools = @($requiredTools | Where-Object { -not (Test-Path $_) })
        if ($missingTools.Count -gt 0) {
            Write-Host 'Frontend dependencies missing; installing...'
            npm ci
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed. Close the frontend dev server before installing dependencies.' }
        } else {
            Write-Host 'Using existing frontend dependencies (no npm ci while the dev server is running).'
        }
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
