using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GestorArchivos.Desktop.Windows;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GestorArchivos", "Desktop", "startup-errors.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, DateTimeOffset.Now + ": " + ex + Environment.NewLine);
            }
            catch (IOException) { }
            MessageBox.Show("No se pudo iniciar Gestor Archivos: " + ex.Message +
                Environment.NewLine + "Registro: " + path, "Error de inicio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
