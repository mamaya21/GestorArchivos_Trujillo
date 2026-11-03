using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace GestorArchivos.Desktop.Windows;

public partial class MainWindow
{
    private sealed record DesktopClipboard(Guid[] FileIds, Guid[] FolderIds, Guid? SourceFolder, bool Move);
    private DesktopClipboard? _clipboard;

    private void Copy_Click(object sender, RoutedEventArgs e) => CaptureClipboard(false);
    private void Cut_Click(object sender, RoutedEventArgs e) => CaptureClipboard(true);

    private void CaptureClipboard(bool move)
    {
        var selection = ItemsList.SelectedItems.Cast<ExplorerItem>().ToArray();
        if (selection.Length == 0) return;
        if (move && !_folderId.HasValue)
        {
            MessageBox.Show("No puedes cortar elementos de la raíz.", "Raíz protegida");
            return;
        }
        _clipboard = new DesktopClipboard(
            selection.Where(x => !x.IsFolder).Select(x => x.Id).ToArray(),
            selection.Where(x => x.IsFolder).Select(x => x.Id).ToArray(),
            _folderId, move);
        StatusText.Text = $"{selection.Length} elemento(s) {(move ? "cortados" : "copiados")}. Abre la carpeta de destino y selecciona Pegar.";
    }

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var clipboard = _clipboard;
        if (clipboard is null) return;
        if (!_folderId.HasValue)
        {
            MessageBox.Show("Selecciona una carpeta de destino. No se puede pegar en la raíz.", "Raíz protegida");
            return;
        }
        var destination = _folderId.Value;
        if (clipboard.Move && clipboard.SourceFolder == destination)
        {
            MessageBox.Show("Los elementos ya están en esta carpeta.", "Mover");
            return;
        }
        try
        {
            if (clipboard.Move)
            {
                if (!clipboard.SourceFolder.HasValue)
                    throw new InvalidOperationException("No se puede mover desde la raíz.");
                var sourceRoot = await GetRootAsync(clipboard.SourceFolder.Value);
                var destinationRoot = await GetRootAsync(destination);
                if (sourceRoot != destinationRoot)
                    throw new InvalidOperationException("Solo puedes mover elementos dentro de la misma raíz.");
                foreach (var folder in clipboard.FolderIds)
                {
                    var visited = new HashSet<Guid>();
                    Guid? cursor = destination;
                    while (cursor.HasValue && visited.Add(cursor.Value))
                    {
                        if (cursor == folder)
                            throw new InvalidOperationException("No puedes mover una carpeta dentro de sí misma o de una subcarpeta.");
                        cursor = await GetParentAsync(cursor.Value);
                    }
                }
                if (MessageBox.Show($"¿Mover {clipboard.FileIds.Length + clipboard.FolderIds.Length} elemento(s)?",
                        "Confirmar movimiento", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }

            var entries = clipboard.FolderIds.Select(id => (Id: id, Folder: true))
                .Concat(clipboard.FileIds.Select(id => (Id: id, Folder: false))).ToArray();
            var completed = 0;
            var skipped = 0;
            string? applyToAll = null;
            foreach (var entry in entries)
            {
                var endpoint = entry.Folder ? "folders" : "files";
                using var metadata = await SendAsync(HttpMethod.Get, $"{endpoint}/{entry.Id}");
                using var doc = await JsonDocument.ParseAsync(await metadata.Content.ReadAsStreamAsync());
                var name = doc.RootElement.GetProperty("nombre").GetString() ?? "";
                string? resolution = applyToAll == "replace" && entry.Folder ? null : applyToAll;
                if (clipboard.Move)
                {
                    using var listing = await SendAsync(HttpMethod.Get, $"folders?parentId={destination}");
                    using var listDoc = await JsonDocument.ParseAsync(await listing.Content.ReadAsStreamAsync());
                    var exists = new[] { "folders", "files" }.Any(key =>
                        listDoc.RootElement.GetProperty(key).EnumerateArray().Any(item =>
                            string.Equals(item.GetProperty("nombre").GetString(), name, StringComparison.OrdinalIgnoreCase)));
                    if (exists && resolution is null)
                    {
                        resolution = ChooseConflict(name, entry.Folder, out var all);
                        if (resolution is null) break;
                        if (all) applyToAll = resolution;
                    }
                    using var response = await SendAsync(HttpMethod.Put, $"{endpoint}/{entry.Id}/move",
                        new { destinationFolderId = destination, conflictResolution = resolution });
                    if (await WasSkippedAsync(response)) skipped++; else completed++;
                }
                else
                {
                    try
                    {
                        using var response = await SendAsync(HttpMethod.Post, "files/copy-selection",
                            new { destinationFolderId = destination,
                                fileIds = entry.Folder ? Array.Empty<Guid>() : new[] { entry.Id },
                                folderIds = entry.Folder ? new[] { entry.Id } : Array.Empty<Guid>(),
                                conflictResolution = resolution });
                        if (await WasSkippedAsync(response)) skipped++; else completed++;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict && resolution is null)
                    {
                        resolution = ChooseConflict(name, entry.Folder, out var all);
                        if (resolution is null) break;
                        if (all) applyToAll = resolution;
                        using var response = await SendAsync(HttpMethod.Post, "files/copy-selection",
                            new { destinationFolderId = destination,
                                fileIds = entry.Folder ? Array.Empty<Guid>() : new[] { entry.Id },
                                folderIds = entry.Folder ? new[] { entry.Id } : Array.Empty<Guid>(),
                                conflictResolution = resolution });
                        if (await WasSkippedAsync(response)) skipped++; else completed++;
                    }
                }
            }
            _clipboard = null;
            await LoadFolderAsync(_folderId);
            StatusText.Text = $"{completed} {(clipboard.Move ? "movido(s)" : "copiado(s)")}, {skipped} omitido(s).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static async Task<bool> WasSkippedAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content)) return false;
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("skipped", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private string? ChooseConflict(string name, bool folder, out bool applyToAll)
    {
        applyToAll = false;
        var choices = folder
            ? "Escribe B (Conservar ambos), O (Omitir) o C (Cancelar)."
            : "Escribe B (Conservar ambos), R (Reemplazar), O (Omitir) o C (Cancelar).";
        var answer = Microsoft.VisualBasic.Interaction.InputBox(
            $"Ya existe \"{name}\" en el destino.\n{choices}\nAgrega * para aplicar a todos (ejemplo B*).",
            "Resolver conflicto", "B").Trim().ToUpperInvariant();
        applyToAll = answer.EndsWith('*');
        var choice = answer.TrimEnd('*');
        return choice switch
        {
            "B" => "keepBoth",
            "R" when !folder => "replace",
            "O" => "skip",
            _ => null
        };
    }

    private async Task<Guid> GetRootAsync(Guid folder)
    {
        var seen = new HashSet<Guid>();
        var cursor = folder;
        while (seen.Add(cursor))
        {
            var parent = await GetParentAsync(cursor);
            if (!parent.HasValue) return cursor;
            cursor = parent.Value;
        }
        throw new InvalidOperationException("Se detectó un ciclo entre carpetas.");
    }

    private async Task<Guid?> GetParentAsync(Guid folder)
    {
        using var response = await SendAsync(HttpMethod.Get, $"folders/{folder}");
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var value = doc.RootElement.GetProperty("carpetaPadreId");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetGuid();
    }
}
