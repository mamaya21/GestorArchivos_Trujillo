using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace GestorArchivos.Desktop.Windows.Services;

/// <summary>
/// Monitors a single managed local copy. Uploads only stable content and uses
/// the server version to prevent overwriting another user's changes.
/// </summary>
public sealed class DesktopEditSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly Guid _fileId;
    private readonly string _path;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string> _status;
    private int _version;
    private string _lastHash;
    private int _pending;
    private bool _disposed;

    public DesktopEditSession(HttpClient http, Guid fileId, string path,
        int version, string initialHash, Action<string> status)
    {
        _http = http;
        _fileId = fileId;
        _path = path;
        _version = version;
        _lastHash = initialHash;
        _status = status;
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                           NotifyFilters.FileName | NotifyFilters.CreationTime
        };
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Renamed += Renamed;
        _watcher.EnableRaisingEvents = true;
    }

    private void Changed(object sender, FileSystemEventArgs e) => Queue();
    private void Renamed(object sender, RenamedEventArgs e) => Queue();

    private void Queue()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pending, 1);
        _ = DrainAsync();
    }

    public void CheckForChanges() => Queue();

    private async Task DrainAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            while (Interlocked.Exchange(ref _pending, 0) == 1 && !_stop.IsCancellationRequested)
            {
                await Task.Delay(1500, _stop.Token);
                try { await UploadIfChangedAsync(_stop.Token); }
                catch (IOException)
                {
                    _status("El documento sigue ocupado. Se reintentará al guardar.");
                    Interlocked.Exchange(ref _pending, 1);
                    await Task.Delay(2500, _stop.Token);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    _status("Conflicto de edición: el servidor tiene otra versión. Tu copia local se conserva.");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _status("No se pudo guardar el cambio en el servidor. La copia local se conserva.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { _gate.Release(); }
    }

    private async Task UploadIfChangedAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return;

        // Congelar una instantánea para evitar subir contenido que cambia
        // mientras Office guarda el archivo.
        var before = new FileInfo(_path);
        var originalLength = before.Length;
        var originalWrite = before.LastWriteTimeUtc;
        var snapshot = Path.Combine(Path.GetTempPath(),
            "gestor-desktop-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var source = new FileStream(_path, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024, FileOptions.Asynchronous))
            await using (var copy = new FileStream(snapshot, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                await source.CopyToAsync(copy, token);

            var after = new FileInfo(_path);
            if (after.Length != originalLength ||
                after.LastWriteTimeUtc != originalWrite)
            {
                Interlocked.Exchange(ref _pending, 1);
                return;
            }

            await using var stream = new FileStream(snapshot, FileMode.Open,
                FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(await sha.ComputeHashAsync(stream, token));
            if (hash == _lastHash) return;
            stream.Position = 0;

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(_version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "expectedVersion");
            form.Add(new StreamContent(stream), "file", Path.GetFileName(_path));
            using var request = new HttpRequestMessage(HttpMethod.Put,
                $"api/files/{_fileId}/desktop-content") { Content = form };
            using var response = await _http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Error guardando el documento", null, response.StatusCode);

            using var result = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            _version = result.RootElement.GetProperty("version").GetInt32();
            _lastHash = hash;
            DesktopCacheState.Write(_path, hash, _version);
            _status("Cambios guardados en el servidor correctamente.");
        }
        finally
        {
            try { if (File.Exists(snapshot)) File.Delete(snapshot); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _stop.Cancel();
        // Gate and CTS are not disposed while a background upload may still be using them.
    }
}
