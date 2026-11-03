using System.Net;
using System.Text.Json;
using GestorArchivos.Sync.Windows.Models;
using GestorArchivos.Sync.Windows.Services;

namespace GestorArchivos.Sync.Windows.Activation;

public sealed class ActivationListenerService : BackgroundService
{
    private static readonly string AllowedOrigin = Environment.GetEnvironmentVariable("GESTOR_ARCHIVOS_WEB_ORIGIN")?.TrimEnd('/') ?? "http://localhost:5173";
    private static readonly string PausePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos", "Sync", "paused.flag");

    private static readonly string SyncDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GestorArchivos", "Sync");
    private readonly IActivationService _activationService;
    private readonly SyncCancellationCoordinator _cancellation;
    private readonly IDeviceRegistrationService _registrations;
    private readonly ILogger<ActivationListenerService> _logger;

    public ActivationListenerService(
        IActivationService activationService,
        SyncCancellationCoordinator cancellation,
        IDeviceRegistrationService registrations,
        ILogger<ActivationListenerService> logger)
    {
        _activationService = activationService;
        _cancellation = cancellation;
        _registrations = registrations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        using var listener = new HttpListener();

        listener.Prefixes.Add("http://127.0.0.1:47831/");
        listener.Start();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync()
                    .WaitAsync(stoppingToken);

                await ProcessRequestAsync(context, stoppingToken);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task ProcessRequestAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var origin = context.Request.Headers["Origin"];

            if (!string.Equals(
                    origin,
                    AllowedOrigin,
                    StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.Forbidden;

                context.Response.Close();
                return;
            }

            if (!IPAddress.IsLoopback(
                    context.Request.RemoteEndPoint.Address))
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.Forbidden;

                context.Response.Close();
                return;
            }

            AddCorsHeaders(context.Response);

            if (context.Request.HttpMethod == "OPTIONS" &&
                (context.Request.Url?.AbsolutePath == "/activate" ||
                 context.Request.Url?.AbsolutePath == "/control" ||
                 context.Request.Url?.AbsolutePath == "/deactivate" ||
                 context.Request.Url?.AbsolutePath == "/quiesce" ||
                 context.Request.Url?.AbsolutePath == "/selections"))
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.NoContent;

                context.Response.Close();
                return;
            }

            if (context.Request.Url?.AbsolutePath == "/selections")
            {
                var selectionsPath = Path.Combine(SyncDirectory, "selections.json");
                var removalPath = Path.Combine(SyncDirectory, "removal-requests.json");
                if (context.Request.HttpMethod == "GET")
                {
                    context.Response.ContentType = "application/json";
                    var json = File.Exists(selectionsPath)
                        ? await File.ReadAllTextAsync(selectionsPath, cancellationToken) : "[]";
                    await using var writer = new StreamWriter(context.Response.OutputStream);
                    await writer.WriteAsync(json);
                    await writer.FlushAsync(cancellationToken);
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "POST")
                {
                    using var payload = await JsonDocument.ParseAsync(
                        context.Request.InputStream, cancellationToken: cancellationToken);
                    if (!payload.RootElement.TryGetProperty("selections", out var selections) ||
                        selections.ValueKind != JsonValueKind.Array ||
                        !payload.RootElement.TryGetProperty("removals", out var removals) ||
                        removals.ValueKind != JsonValueKind.Array)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        context.Response.Close();
                        return;
                    }

                    // Validar los datos antes de modificar la configuración persistida.
                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var selection in selections.EnumerateArray())
                    {
                        if (!selection.TryGetProperty("Path", out var path) ||
                            path.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(path.GetString()) ||
                            !Path.IsPathFullyQualified(path.GetString()!) ||
                            !selection.TryGetProperty("Kind", out var kind) ||
                            kind.ValueKind != JsonValueKind.Number ||
                            !kind.TryGetInt32(out var kindValue) || kindValue is < 0 or > 1 ||
                            !paths.Add(Path.GetFullPath(path.GetString()!)))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            context.Response.Close();
                            return;
                        }
                    }
                    foreach (var removal in removals.EnumerateArray())
                    {
                        if (!removal.TryGetProperty("Path", out var path) ||
                            path.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(path.GetString()) ||
                            !Path.IsPathFullyQualified(path.GetString()!) ||
                            !removal.TryGetProperty("Kind", out var kind) ||
                            kind.ValueKind != JsonValueKind.Number ||
                            !kind.TryGetInt32(out var kindValue) || kindValue is < 0 or > 1 ||
                            !removal.TryGetProperty("SendToTrash", out var trash) ||
                            (trash.ValueKind != JsonValueKind.True && trash.ValueKind != JsonValueKind.False))
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                            context.Response.Close();
                            return;
                        }
                    }

                    Directory.CreateDirectory(SyncDirectory);
                    var existingRemovals = File.Exists(removalPath)
                        ? JsonSerializer.Deserialize<List<JsonElement>>(
                            await File.ReadAllTextAsync(removalPath, cancellationToken)) ?? new()
                        : new List<JsonElement>();
                    var retained = existingRemovals.Where(x =>
                        x.TryGetProperty("Path", out var p) &&
                        !paths.Contains(Path.GetFullPath(p.GetString()!))).ToList();
                    foreach (var removal in removals.EnumerateArray())
                    {
                        var path = removal.GetProperty("Path").GetString()!;
                        if (!retained.Any(x => x.TryGetProperty("Path", out var p) &&
                            string.Equals(p.GetString(), path, StringComparison.OrdinalIgnoreCase)))
                            retained.Add(removal.Clone());
                    }

                    // Primero publicar selecciones, luego retiradas: el motor no debe
                    // procesar una retirada mientras la selección anterior sigue activa.
                    await WriteConfigurationAsync(selectionsPath, selections.GetRawText(), cancellationToken);
                    await WriteConfigurationAsync(removalPath, JsonSerializer.Serialize(retained), cancellationToken);
                    var pending = Path.Combine(SyncDirectory, "selection-pending.flag");
                    if (File.Exists(pending)) File.Delete(pending);
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.Close();
                    return;
                }
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            if (context.Request.Url?.AbsolutePath == "/control")
            {
                if (context.Request.HttpMethod == "GET")
                {
                    context.Response.ContentType = "application/json";
                    var paused = File.Exists(PausePath);
                    var registeredDevice = await _registrations.GetRegisteredDeviceAsync(cancellationToken);
                    await JsonSerializer.SerializeAsync(
                        context.Response.OutputStream,
                        new { paused, deviceId = registeredDevice?.DeviceId, hostName = Environment.MachineName },
                        cancellationToken: cancellationToken);
                    context.Response.Close();
                    return;
                }

                if (context.Request.HttpMethod == "POST")
                {
                    var command = await JsonSerializer.DeserializeAsync<SyncControlRequest>(
                        context.Request.InputStream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                        cancellationToken);

                    if (command?.Paused is not bool pause)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        context.Response.Close();
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(PausePath)!);
                    if (pause)
                        await File.WriteAllTextAsync(PausePath, "paused", cancellationToken);
                    else if (File.Exists(PausePath))
                        File.Delete(PausePath);

                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.Close();
                    return;
                }
            }

            if (context.Request.Url?.AbsolutePath == "/quiesce" &&
                context.Request.HttpMethod == "POST")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PausePath)!);
                await File.WriteAllTextAsync(PausePath, "paused", cancellationToken);
                await _cancellation.StopAndWaitAsync(cancellationToken);
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            if (context.Request.Url?.AbsolutePath == "/deactivate" &&
                context.Request.HttpMethod == "POST")
            {
                // El servidor debe revocar primero la credencial. Esta operación
                // solo borra el registro y token locales; nunca toca archivos.
                Directory.CreateDirectory(Path.GetDirectoryName(PausePath)!);
                await File.WriteAllTextAsync(PausePath, "paused", cancellationToken);
                await _cancellation.StopAndWaitAsync(cancellationToken);
                await _registrations.ClearRegistrationAsync(cancellationToken);
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod != "POST" ||
                context.Request.Url?.AbsolutePath != "/activate")
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.NotFound;

                context.Response.Close();
                return;
            }

            var request =
                await JsonSerializer.DeserializeAsync<ActivationRequest>(
                    context.Request.InputStream,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    },
                    cancellationToken);

            if (request is null ||
                request.DeviceId == Guid.Empty ||
                request.RootFolderId == Guid.Empty ||
                string.IsNullOrWhiteSpace(request.Token))
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.BadRequest;

                context.Response.Close();
                return;
            }

            var device = new SyncDevice
            {
                DeviceId = request.DeviceId,
                DeviceName = request.DeviceName,
                RootFolderId = request.RootFolderId,
                RootFolderName = request.RootFolderName,
                Token = request.Token
            };

            if (request.StartFresh)
            {
                // El nuevo dispositivo nunca debe heredar selecciones del anterior.
                // Bloquear el motor hasta que el usuario guarde una nueva selección.
                Directory.CreateDirectory(SyncDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(SyncDirectory, "selection-pending.flag"),
                    request.DeviceId.ToString(), cancellationToken);
                await File.WriteAllTextAsync(
                    Path.Combine(SyncDirectory, "selections.json"),
                    "[]", cancellationToken);
            }

            await _activationService.ActivateAsync(
                device,
                cancellationToken);

            // Una activación nueva reanuda el motor sin reiniciar el servicio.
            _cancellation.Reset();
            if (File.Exists(PausePath))
                File.Delete(PausePath);

            context.Response.StatusCode =
                (int)HttpStatusCode.NoContent;

            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error procesando activación local.");

            if (context.Response.OutputStream.CanWrite)
            {
                AddCorsHeaders(context.Response);

                context.Response.StatusCode =
                    (int)HttpStatusCode.InternalServerError;

                context.Response.Close();
            }
        }
    }

    private static async Task WriteConfigurationAsync(string path, string json, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, token);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void AddCorsHeaders(
        HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] =
            AllowedOrigin;

        response.Headers["Access-Control-Allow-Methods"] =
            "GET, POST, OPTIONS";

        response.Headers["Access-Control-Allow-Headers"] =
            "Content-Type";
    }
}
public sealed record SyncControlRequest(bool? Paused);
