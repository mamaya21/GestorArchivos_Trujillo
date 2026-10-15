using System.Net;
using Xunit;

namespace GestorArchivos.IntegrationTests;

public class SyncIntegrationTests :
    IClassFixture<GestorArchivosWebApplicationFactory>
{
    private readonly GestorArchivosWebApplicationFactory _factory;

    public SyncIntegrationTests(
        GestorArchivosWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Api_Debe_Iniciar_Correctamente()
    {
        using var client = _factory.CreateClient(
            new()
            {
                AllowAutoRedirect = false
            });

        var response = await client.GetAsync("/api/sync/status");

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            response.StatusCode);
    }
}