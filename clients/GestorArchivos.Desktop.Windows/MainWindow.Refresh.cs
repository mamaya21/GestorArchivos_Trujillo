using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GestorArchivos.Desktop.Windows.Services;

namespace GestorArchivos.Desktop.Windows;

public partial class MainWindow
{
    private async Task RefreshExistingEditAsync(ExplorerItem item, DesktopEditSession existing)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GestorArchivos", "Desktop", "Cache", item.Id.ToString("N"), Path.GetFileName(item.Name));
        using var response = await SendAsync(HttpMethod.Get, $"files/{item.Id}");
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var serverHash = doc.RootElement.GetProperty("hashSha256").GetString() ?? "";
        var version = doc.RootElement.GetProperty("version").GetInt32();
        var baseline = DesktopCacheState.Read(path);
        if (baseline is null || !File.Exists(path)) { existing.CheckForChanges(); return; }
        string localHash;
        await using (var local = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
            localHash = Convert.ToHexString(await SHA256.HashDataAsync(local));
        if (string.Equals(localHash, serverHash, StringComparison.OrdinalIgnoreCase))
        { existing.CheckForChanges(); return; }
        if (!string.Equals(localHash, baseline.Value.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Existen cambios locales pendientes. No se reemplazó el archivo.");
        existing.Dispose();
        _editSessions.Remove(item.Id);
        await DownloadServerCopyAsync(item.Id, path);
        string actual;
        await using (var refreshed = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(refreshed));
        if (!string.Equals(actual, serverHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("La descarga no coincide con el servidor.");
        DesktopCacheState.Write(path, actual, version);
        _editSessions[item.Id] = new DesktopEditSession(_http, item.Id, path, version, actual,
            message => Dispatcher.InvokeAsync(() => StatusText.Text = message));
        StatusText.Text = "Versión actualizada desde el servidor.";
    }
}
