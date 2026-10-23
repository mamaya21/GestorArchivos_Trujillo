using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GestorArchivos.IntegrationTests;

/// <summary>
/// API-level reconnect and retry contracts. These tests do not simulate a
/// Windows process crash, network adapter failure, or its local durable queue.
/// </summary>
public sealed class SyncReconnectRegressionTests
    : IClassFixture<GestorArchivosWebApplicationFactory>
{
    private readonly GestorArchivosWebApplicationFactory _factory;

    public SyncReconnectRegressionTests(GestorArchivosWebApplicationFactory factory)
        => _factory = factory;

    [Fact]
    public async Task Reconnect_WithSameToken_PreservesDeviceIdentity_AndDoesNotDuplicateRegistration()
    {
        using var owner = Client();
        await Login(owner);

        var name = $"Reconnect-{Guid.NewGuid():N}";
        using var registration = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name, operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var registered = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var id = registered.RootElement.GetProperty("deviceId").GetGuid();
        var root = registered.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = registered.RootElement.GetProperty("token").GetString()!;

        // A lost response followed by the same registration request must not
        // silently create a second active device/root.
        using var retry = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name, operatingSystem = "Windows" });
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);

        // Recreate the HTTP client to model a disconnected/restarted transport.
        using (var firstConnection = SyncClient(token))
        {
            using var response = await firstConnection.GetAsync("/api/sync/status");
            await RequireSuccess(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(id, json.RootElement.GetProperty("id").GetGuid());
        }

        using (var reconnected = SyncClient(token))
        {
            using var response = await reconnected.GetAsync("/api/sync/status");
            await RequireSuccess(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(id, json.RootElement.GetProperty("id").GetGuid());

            // Re-reading an unchanged change cursor must be stable across connections.
            using var changes = await reconnected.GetAsync("/api/sync/changes?afterId=0&limit=100");
            await RequireSuccess(changes);
        }

        using var devices = await owner.GetAsync("/api/sync/devices");
        await RequireSuccess(devices);
        using var listing = JsonDocument.Parse(await devices.Content.ReadAsStringAsync());
        var matches = listing.RootElement.EnumerateArray()
            .Where(x => x.GetProperty("deviceId").GetGuid() == id).ToList();
        Assert.Single(matches);
        Assert.Equal(root, matches[0].GetProperty("rootFolderId").GetGuid());
    }

    [Fact]
    public async Task UnlinkAndExplicitRelink_RevokesOldToken_AndReusesOriginalRoot()
    {
        using var owner = Client();
        await Login(owner);
        var name = $"Relink-{Guid.NewGuid():N}";
        using var registration = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name, operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var original = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var id = original.RootElement.GetProperty("deviceId").GetGuid();
        var root = original.RootElement.GetProperty("rootFolderId").GetGuid();
        var oldToken = original.RootElement.GetProperty("token").GetString()!;

        using (var unlink = await owner.PostAsync($"/api/sync/devices/{id}/unlink", null))
            Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);

        using (var stale = SyncClient(oldToken))
        using (var denied = await stale.GetAsync("/api/sync/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var relink = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name, operatingSystem = "Windows", reuseDeviceId = id });
        await RequireSuccess(relink);
        using var renewed = JsonDocument.Parse(await relink.Content.ReadAsStringAsync());
        Assert.Equal(id, renewed.RootElement.GetProperty("deviceId").GetGuid());
        Assert.Equal(root, renewed.RootElement.GetProperty("rootFolderId").GetGuid());
        var newToken = renewed.RootElement.GetProperty("token").GetString()!;
        Assert.NotEqual(oldToken, newToken);

        using (var stale = SyncClient(oldToken))
        using (var denied = await stale.GetAsync("/api/sync/status"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using (var active = SyncClient(newToken))
        using (var status = await active.GetAsync("/api/sync/status"))
            await RequireSuccess(status);

        using var duplicate = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name, operatingSystem = "Windows", reuseDeviceId = id });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    private HttpClient Client() => _factory.CreateClient(new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost")
    });

    private HttpClient SyncClient(string token)
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task Login(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "admin", password = "IntegrationTest123!" });
        await RequireSuccess(response);
    }

    private static async Task RequireSuccess(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
