# Gestor Archivos Desktop (WPF nativo)

Cliente Windows sin WebView2, con login mediante la API y explorador de archivos.

## Edición nativa

Al abrir un documento, Desktop obtiene su contenido y versión del servidor, prepara una copia administrada en %LOCALAPPDATA%/GestorArchivos/Desktop/Cache y la abre mediante la aplicación predeterminada de Windows.

Desktop observa cambios guardados, prepara una instantánea estable y la envía mediante PUT /api/files/{id}/desktop-content. La API verifica el permiso Modificar y la versión esperada en una transacción, conserva la versión anterior y registra FILE_UPDATED para sincronización. Si existe un conflicto, no reemplaza la versión del servidor ni borra la copia local.

No se reutiliza una copia local que difiera del hash del servidor sin intervención del usuario. No cerrar Desktop antes de confirmar el mensaje de guardado.

## Funciones disponibles

Login, logout, navegación, búsqueda en la carpeta actual, creación de carpetas y archivos .txt, subida multipart, renombrado, papelera, apertura nativa y guardado automático de cambios detectados.

## Pendientes para paridad con la web y entrega final

- Gestión completa de conflictos, recuperación de cambios pendientes y persistencia de sesiones tras reinicio.
- Bloqueos de edición distribuidos con renovación y liberación, y visibilidad del editor.
- Papelera visual, restauración, permisos administrativos, copia/movimiento, drag & drop, historial de versiones en interfaz, operaciones masivas avanzadas.
- Transferencias grandes resumibles y MSI con configuración para el servidor y Cloudflare.
- Compilación y pruebas funcionales de la nueva API y Desktop en Windows.

## Inicio

`dotnet run --project clients/GestorArchivos.Desktop.Windows/GestorArchivos.Desktop.Windows.csproj`

Configurar `desktopsettings.json` con la URL HTTPS del servidor (HTTP solo para localhost).
