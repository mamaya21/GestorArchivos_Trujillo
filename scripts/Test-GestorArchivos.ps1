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
        # Never run npm ci as part of a local validation: it removes node_modules
        # and can fail when Vite/Rolldown native modules are loaded by the dev server.
        # npm ci belongs to the isolated GitHub Actions runner.
        $vite = Join-Path (Get-Location) 'node_modules/.bin/vite.cmd'
        $tsc = Join-Path (Get-Location) 'node_modules/.bin/tsc.cmd'
        $oxlint = Join-Path (Get-Location) 'node_modules/.bin/oxlint.cmd'
        if (-not (Test-Path $vite) -or -not (Test-Path $tsc) -or -not (Test-Path $oxlint)) {
            Write-Warning 'Frontend tools are missing or incomplete. Skipping frontend checks to avoid changing locked node_modules. Run npm ci only after stopping the frontend dev server.'
        } else {
            Write-Host 'Using installed frontend tools.'
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
        npm run lint
        if ($LASTEXITCODE -ne 0) { throw 'Frontend lint failed.' }
        }
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
