# Gestor Archivos Desktop (WPF nativo)

Cliente Windows sin WebView2 ni interfaz embebida. Reutiliza la API existente y el inicio de sesión por cookie de ASP.NET Core Identity.

## Implementado en esta etapa

- Login y cierre de sesión nativos.
- Explorador de carpetas y archivos, navegación y actualización.
- Crear y renombrar carpetas y archivos.
- Selección múltiple para enviar elementos a la papelera.
- Subir archivos y abrir archivos mediante su programa asociado en Windows.

## Limitaciones actuales

- La apertura utiliza una copia local administrada. **No** implementa todavía subida automática de cambios, bloqueo distribuido, control de versiones de edición, portapapeles, papelera visual ni paridad completa con la web.
- La subida actual utiliza el endpoint multipart; para archivos grandes se debe integrar el protocolo resumible existente.
- Se requiere HTTPS para el servidor remoto. HTTP solo está permitido en localhost para desarrollo.
- No se ha ejecutado compilación ni prueba funcional en Windows desde GitHub.

## Ejecución

`dotnet run --project clients/GestorArchivos.Desktop.Windows/GestorArchivos.Desktop.Windows.csproj`

Configure `desktopsettings.json` con la URL del servidor o del frontend local que redirige /api al backend.
