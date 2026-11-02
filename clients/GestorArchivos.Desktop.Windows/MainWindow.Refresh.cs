namespace GestorArchivos.Desktop.Windows;

public partial class MainWindow
{
    private async Task RefreshExistingEditAsync(ExplorerItem item, Services.DesktopEditSession existing)
    {
        existing.CheckForChanges();
        await Task.CompletedTask;
    }
}
