using System.Net;

namespace GestorArchivos.Sync.Windows.Api;

public sealed class SyncApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ResponseBody { get; }

    public SyncApiException(
        HttpStatusCode statusCode,
        string? responseBody)
        : base($"Sync API respondió {(int)statusCode} ({statusCode}). Detalle: {responseBody?.Substring(0, Math.Min(responseBody.Length, 1024))}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}