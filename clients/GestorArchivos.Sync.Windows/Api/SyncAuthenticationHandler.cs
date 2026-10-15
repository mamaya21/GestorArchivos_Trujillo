using System.Net.Http.Headers;
using GestorArchivos.Sync.Windows.Security;

namespace GestorArchivos.Sync.Windows.Api;

public sealed class SyncAuthenticationHandler : DelegatingHandler
{
    private readonly ICredentialStore _credentialStore;

    public SyncAuthenticationHandler(ICredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _credentialStore.GetTokenAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}