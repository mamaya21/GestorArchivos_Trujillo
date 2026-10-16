using Microsoft.Win32;

namespace GestorArchivos.Sync.Selector.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        RegisterProtocol();
        Application.Run(new Form1());
    }

    private static void RegisterProtocol()
    {
        // HKCU no requiere permisos de administrador y no modifica archivos del usuario.
        // El navegador solicita confirmación antes de abrir el protocolo externo.
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\gestorarchivos-sync");
            if (key is null) return;
            key.SetValue(string.Empty, "Gestor Archivos - Selector de sincronización");
            key.SetValue("URL Protocol", string.Empty);
            using var command = key.CreateSubKey(@"shell\open\command");
            command?.SetValue(string.Empty, $"\"{Application.ExecutablePath}\" \"%1\"");
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("No se pudo registrar el enlace del selector. Podrás seguir abriéndolo desde Windows.",
                "Gestor Archivos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
