using System.Net.Http;
using System.Text.Json;

namespace GestorArchivos.Desktop.Windows.Services;

internal static class SharedFileCopyService
{
    public static async Task<(Guid Id, string Name)> CreateAsync(
        HttpClient http, Guid sourceId, Guid folderId, string sourceName)
    {
        using var response = await http.GetAsync("api/files/" + sourceId + "/download",
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var name = Path.GetFileNameWithoutExtension(sourceName);
        var ext = Path.GetExtension(sourceName);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        // Let the server enforce uniqueness. Retry only name conflicts.
        for (var number = 1; number <= 100; number++)
        {
            var copyName = name + " - copia" + (number == 1 ? "" : " (" + number + ")") + ext;
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(folderId.ToString()), "folderId");
            form.Add(new ByteArrayContent(bytes), "file", copyName);
            using var uploaded = await http.PostAsync("api/files/upload", form);
            if (uploaded.StatusCode == System.Net.HttpStatusCode.Conflict)
                continue;
            uploaded.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await uploaded.Content.ReadAsStreamAsync());
            return (doc.RootElement.GetProperty("id").GetGuid(), copyName);
        }
        throw new IOException("No se pudo encontrar un nombre disponible para la copia.");
    }
}
