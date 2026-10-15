using System.Net;
using System.Text.Json;
using GestorArchivos.Sync.Windows.Models;

namespace GestorArchivos.Sync.Windows.Activation;

public sealed class ActivationListenerService : BackgroundService
{
    private const string AllowedOrigin = "http://localhost:5173";

    private readonly IActivationService _activationService;
    private readonly ILogger<ActivationListenerService> _logger;

    public ActivationListenerService(
        IActivationService activationService,
        ILogger<ActivationListenerService> logger)
    {
        _activationService = activationService;
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

            AddCorsHeaders(context.Response);

            if (context.Request.HttpMethod == "OPTIONS" &&
                context.Request.Url?.AbsolutePath == "/activate")
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.NoContent;

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

            if (!IPAddress.IsLoopback(
                    context.Request.RemoteEndPoint.Address))
            {
                context.Response.StatusCode =
                    (int)HttpStatusCode.Forbidden;

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

            await _activationService.ActivateAsync(
                device,
                cancellationToken);

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

    private static void AddCorsHeaders(
        HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] =
            AllowedOrigin;

        response.Headers["Access-Control-Allow-Methods"] =
            "POST, OPTIONS";

        response.Headers["Access-Control-Allow-Headers"] =
            "Content-Type";
    }
}