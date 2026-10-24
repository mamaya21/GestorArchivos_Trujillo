using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Xunit;

namespace GestorArchivos.IntegrationTests;

/// <summary>
/// Regression tests for the unauthenticated API trust boundary.
/// Requires the isolated integration-test SQL Server configured by the test factory.
/// These requests use random IDs and never mutate real user data.
/// </summary>
public sealed class AnonymousSecurityRegressionTests
    : IClassFixture<GestorArchivosWebApplicationFactory>
{
    private readonly GestorArchivosWebApplicationFactory _factory;

    public AnonymousSecurityRegressionTests(GestorArchivosWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public static IEnumerable<object[]> ProtectedReads()
    {
        var id = Guid.NewGuid();
        yield return new object[] { "/api/auth/me" };
        yield return new object[] { "/api/admin/users" };
        yield return new object[] { "/api/admin/groups" };
        yield return new object[] { "/api/admin/audit" };
        yield return new object[] { "/api/folders" };
        yield return new object[] { $"/api/folders/{id}" };
        yield return new object[] { $"/api/files/{id}" };
        yield return new object[] { $"/api/files/{id}/download" };
        yield return new object[] { $"/api/files/{id}/open" };
        yield return new object[] { "/api/trash" };
        yield return new object[] { $"/api/admin/folders/{id}/permissions" };
        yield return new object[] { $"/api/admin/folders/{id}/permissions/effective/{id}" };
        yield return new object[] { "/api/sync/status" };
        yield return new object[] { "/api/sync/items" };
        yield return new object[] { "/api/sync/snapshot" };
        yield return new object[] { "/api/sync/changes?afterId=0&limit=10" };
        yield return new object[] { $"/api/sync/read/files/{id}" };
        yield return new object[] { $"/api/sync/read/files/{id}/download" };
        yield return new object[] { $"/api/sync/read/folders/{id}" };
    }

    [Theory]
    [MemberData(nameof(ProtectedReads))]
    public async Task ProtectedRead_Rejects_Anonymous_Request(string route)
    {
        using var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await client.GetAsync(route);
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Anonymous GET {route} returned {(int)response.StatusCode} ({response.StatusCode}).");
    }

    [Theory]
    [InlineData("/api/folders")]
    [InlineData("/api/sync/folders")]
    [InlineData("/api/sync/files")]
    [InlineData("/api/uploads/initialize")]
    [InlineData("/api/admin/users")]
    [InlineData("/api/admin/groups")]
    public async Task ProtectedWrite_Rejects_Anonymous_Request(string route)
    {
        using var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await client.PostAsJsonAsync(route, new { name = "security-test" });
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Anonymous POST {route} returned {(int)response.StatusCode} ({response.StatusCode}).");
    }

    [Theory]
    [MemberData(nameof(ProtectedReads))]
    public async Task InvalidBearerToken_CannotReadProtectedData(string route)
    {
        using var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "invalid-security-regression-token");
        using var response = await client.GetAsync(route);
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Invalid token GET {route} returned {(int)response.StatusCode} ({response.StatusCode}).");
    }
}
