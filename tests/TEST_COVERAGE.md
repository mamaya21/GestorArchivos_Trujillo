# Gestor Archivos — coverage and validation ledger

This file is the source of truth for regression scope. Never mark a scenario automated unless a runnable test asserts its result. A user-confirmed manual pass is distinct from an automated pass.

## Previously confirmed manually

- Web: download files and folders; copy, move, cut/paste and delete multiple files/folders
- Web trash: multi-select, restore, permanent deletion
- Windows sync: bidirectional sync; pause/resume; local-to-web file/folder moves and renames (including empty folders and nested structures)
- Windows sync: local folder deletion to web trash, including empty folder after re-registering the device while keeping remote data
- Windows sync: web-to-local folder moves and renames

## Existing automated tests

- `tests/GestorArchivos.IntegrationTests`: API, sync and orphan cleanup test classes; requires isolated SQL Server Express (`.\\SQLEXPRESS`), see factory source.
- CI: .NET solution build, frontend TypeScript/Vite build, frontend lint. These are quality gates, **not** proof of functional behavior.

## Remaining coverage to implement

| Area | Required automated assertions | Status |
| --- | --- | --- |
| Permissions | User/group allow/deny, inheritance, effective permissions, forbidden API operations | Not automated |
| Disconnection/recovery | Offline edits, reconnect, restart with pending changes, idempotence | Not automated |
| Conflicts/version history | Both changes retained, conflict handling, version list and restore | Not automated |
| Transfers | 4 GB upload/download, SHA-256, interrupted upload/download, resume semantics | Not automated |
| Windows Explorer | Open file using associated application, double click, mount/shell integration | Not automated |
| Local agent security | Origin validation, hostile localhost requests, activation/pause/deactivation auth, secrets storage | Not automated |
| Browser UX | Navigation, bulk actions, trash and permissions via Playwright | Not automated |

## How to run

`powershell -ExecutionPolicy Bypass -File .\scripts\Test-GestorArchivos.ps1`

On a dedicated Windows test host with a **verified isolated** SQL Express instance and test database, explicitly opt in:

`$env:GESTOR_TEST_SQL_ISOLATED='1'`
`powershell -ExecutionPolicy Bypass -File .\scripts\Test-GestorArchivos.ps1 -IntegrationTests`

Never point test configuration at production storage, credentials, or databases. Large-transfer testing must use generated disposable data only. Do not claim these pending suites pass until they exist and run successfully.
