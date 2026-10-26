# GestorArchivos.Desktop.Windows

Cliente de escritorio Windows (WPF + WebView2) que reutiliza la aplicación web y su autenticación existente, sin duplicar la API ni los permisos.

## Inicio local

1. Iniciar API y frontend con los comandos habituales del proyecto.
2. Ejecutar `dotnet run --project clients/GestorArchivos.Desktop.Windows/GestorArchivos.Desktop.Windows.csproj` en Windows.
3. Iniciar sesión dentro de la ventana Desktop con las credenciales habituales.

## Configuración

Editar `desktopsettings.json` y establecer `ServerUrl` con la dirección HTTPS del servidor publicado. Solo se permite HTTP para localhost durante desarrollo. La configuración se copia al directorio de salida.

## Alcance de esta primera base

La ventana, autenticación, navegación y funcionalidades web se reutilizan mediante WebView2. **Todavía no se implementa** la edición nativa de archivos con caché, guardado de vuelta al servidor, bloqueos centralizados, ni instalador MSI. Esos componentes necesitan implementación y pruebas adicionales; no se deben considerar terminados.
