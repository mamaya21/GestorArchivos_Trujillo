using System.IO;
using System.Text.Json;

namespace GestorArchivos.Desktop.Windows.Services;

/// <summary>Stores the last confirmed server content fingerprint per managed file.</summary>
public static class DesktopCacheState
{
    private sealed record State(string Hash, int Version);

    public static (string Hash, int Version)? Read(string filePath)
    {
        try
        {
            var path = filePath + ".gestor-state.json";
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            return state is null ? null : (state.Hash, state.Version);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Write(string filePath, string hash, int version)
    {
        var path = filePath + ".gestor-state.json";
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new State(hash, version)));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
