using GestorArchivos.Sync.Windows.Api;
using System.Net;

namespace GestorArchivos.Sync.Windows.Sync;

public sealed class FileLockService
{
    private readonly HttpClient _httpClient;

    public FileLockService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task AcquireAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            $"api/sync/files/{fileId}/lock",
            null,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RenewAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(
            $"api/sync/files/{fileId}/lock/renew",
            null,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task ReleaseAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.DeleteAsync(
            $"api/sync/files/{fileId}/lock",
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(
            cancellationToken);

        throw new SyncApiException(
            response.StatusCode,
            body);
    }
}