namespace GestorArchivos.Sync.Windows.Infrastructure;

public sealed class ConnectivityService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ConnectivityService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<bool> IsAvailableAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("SyncConnectivity");

            using var request = new HttpRequestMessage(
                HttpMethod.Head,
                string.Empty);

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return true;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}