# Gestor Archivos — matriz de cobertura (26-oct-2026)

Criterios: **Validado** = prueba automatizada ejecutada satisfactoriamente o comprobación manual confirmada; **Parcial** = implementación identificada sin cobertura integral; **Pendiente** = falta evidencia de validación. No equiparar compilación con comportamiento funcional.

## Validaciones confirmadas

| Área | Estado | Evidencia / límite |
| --- | --- | --- |
| API e integración, ACL de usuario y grupo, herencia, revocación, escrituras denegadas, operaciones Sync, aislamiento entre dispositivos | **Validado (automatizado)** | Usuario ejecutó el 26-oct-2026 la batería completa de `GestorArchivos.IntegrationTests`: **134 aprobadas, 0 errores, 0 omitidas**. No implica cobertura exhaustiva de toda la aplicación. |
| Web: descarga de archivos/carpetas, copia, movimiento, cortar/pegar y eliminación masiva | **Validado (manual)** | Usuario confirmó estas operaciones. |
| Papelera web: selección múltiple, restauración y eliminación definitiva | **Validado (manual)** | Usuario confirmó funcionalidad. |
| Sync Windows: sincronización bidireccional, pausa/reanudación, movimientos y renombres de archivos/carpetas, carpetas vacías, papelera remota | **Validado (manual, escenarios anteriores)** | Confirmaciones del usuario; no constituye certificación de resiliencia ni seguridad del cliente. |
| Persistencia local del checkpoint Sync tras reinicio, guardados consecutivos e interrupción | **Implementado (automatizado; ejecución no confirmada)** | Cinco pruebas en `tests/GestorArchivos.Sync.Windows.Tests/LocalStateRestartTests.cs`. No incluidas en el resultado de 134 pruebas de integración. |

## Brechas reales, por prioridad

| Prioridad | Área | Estado | Trabajo para cerrar |
| --- | --- | --- | --- |
| P0 | Agente local Windows: HttpListener, Origin, endpoints de activación/pausa/desactivación/control, tokens y almacenamiento de secretos | **Pendiente** | Auditoría de límites de confianza y pruebas hostiles desde localhost; corregir vulnerabilidades verificadas. |
| P0 | Reconexión Windows, fallos de proceso, cola pendiente y conflictos simultáneos | **Parcial** | Ejecutar pruebas de reinicio real y pérdida de red, verificar no pérdida de datos, reintentos idempotentes y conflictos. Existen `SyncReconnectRegressionTests`, `Test-SyncCrashResume.ps1` y `Test-RealWindowsSyncCrashResume.ps1`; revisar su alcance antes de añadir escenarios. |
| P1 | Integración Explorador Windows / aplicación asociada / doble clic | **Pendiente** | Validación de extremo a extremo con cliente instalado, sin inferir que el código por sí solo prueba integración shell. |
| P1 | Web: navegación, drag & drop, vista previa, doble clic, permisos y operaciones masivas | **Parcial** | Añadir pruebas E2E con navegador (no hay Playwright configurado en `frontend/package.json`); evitar repetir casos manuales aprobados salvo regresión. |
| P1 | Transferencias masivas: 4 GB, corte/reanudación, SHA-256, límites y memoria | **Parcial** | Probar con datos descartables; `scripts/Test-GestorArchivos.ps1 -LargeTransferTests` actualmente indica explícitamente que no está implementado. Ya hubo prueba manual satisfactoria de reanudación de 256 MB. |
| P1 | Historial de versiones, restauración y resolución de conflictos | **Pendiente de cobertura integral** | Verificar persistencia de ambas versiones, recuperación, consistencia de hash y concurrencia. |
| P2 | Administración: usuarios, grupos, auditoría y permisos en UI | **Parcial** | Las páginas existen; las ACL API están validadas, pero falta E2E de interfaz y permisos efectivos visibles. |
| P2 | Rendimiento y escalabilidad | **Pendiente** | Medir miles de elementos, árboles profundos, operaciones concurrentes y uso de recursos. |
| P2 | Operación y despliegue | **Parcial** | Verificar instalación, actualización, respaldo/restauración, recuperación y monitoreo. CI actual compila .NET, frontend y lint; no ejecuta pruebas SQL por depender de SQLEXPRESS. |

## Plan de ejecución agrupada

1. **Bloque A — Seguridad y resiliencia Windows (P0):** auditar agente y credenciales, cubrir reinicio/reconexión/operaciones pendientes y corregir fallos confirmados; un único ciclo de validación por bloque.
2. **Bloque B — Experiencia completa (P1):** pruebas E2E web + cliente Windows + operaciones de interfaz que no estén ya cubiertas.
3. **Bloque C — Integridad y volumen (P1):** conflictos, historial, restauración, transferencias grandes y concurrencia.
4. **Bloque D — Administración y producción (P2):** controles administrativos, rendimiento, despliegue, respaldo y recuperación.

## Comandos y restricciones

- Batería API confirmada: `$env:GESTOR_TEST_ALLOW_SHARED_INSTANCE="1"; dotnet test .\tests\GestorArchivos.IntegrationTests\GestorArchivos.IntegrationTests.csproj -c Release --no-restore`.
- El resultado de **134** solo corresponde a ese proyecto de integración, no a todas las pruebas de la solución.
- `scripts/Test-GestorArchivos.ps1` aún exige `GESTOR_TEST_SQL_ISOLATED=1` para su opción `-IntegrationTests`; revisar y armonizar con la fábrica actual antes de usarlo como lanzador único.
- No usar base de datos, credenciales ni archivos productivos para pruebas destructivas. Mantener la base temporal de integración y datos de prueba desechables.
- Una validación aprobada no se repite salvo cambios que afecten su comportamiento.
