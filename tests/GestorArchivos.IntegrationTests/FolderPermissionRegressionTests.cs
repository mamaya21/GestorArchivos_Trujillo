using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using GestorArchivos.Domain.Security;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GestorArchivos.IntegrationTests;

/// <summary>
/// End-to-end permission API regression using only the isolated test database.
/// All folders, users and groups have unique test-only identifiers.
/// </summary>
public sealed class FolderPermissionRegressionTests
    : IClassFixture<GestorArchivosWebApplicationFactory>
{
    private readonly GestorArchivosWebApplicationFactory _factory;

    public FolderPermissionRegressionTests(GestorArchivosWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UserAndGroupPermissions_Inheritance_Revocation_AndWriteBoundary()
    {
        const string password = "IntegrationPermission123!";
        var username = $"permission.{Guid.NewGuid():N}";
        Guid userId;

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = new ApplicationUser
            {
                UserName = username,
                Email = $"{username}@test.local",
                Nombre = "Permisos",
                Apellido = "Integracion",
                Activo = true,
                EmailConfirmed = true
            };
            var result = await users.CreateAsync(created, password);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(x => x.Description)));
            userId = created.Id;
        }

        using var admin = Client();
        using var user = Client();
        await Login(admin, "admin", "IntegrationTest123!");
        await Login(user, username, password);

        var parent = await CreateId(admin, "/api/folders",
            new { name = $"Permissions-{Guid.NewGuid():N}" });
        var child = await CreateId(admin, "/api/folders",
            new { name = $"Child-{Guid.NewGuid():N}", parentId = parent });
        var group = await CreateId(admin, "/api/admin/groups",
            new { name = $"PermissionGroup-{Guid.NewGuid():N}", description = "Automated regression" });

        var userRoute = $"/api/admin/folders/{parent}/permissions/users/{userId}";
        var groupRoute = $"/api/admin/folders/{parent}/permissions/groups/{group}";
        var effectiveParent = $"/api/admin/folders/{parent}/permissions/effective/{userId}";
        var effectiveChild = $"/api/admin/folders/{child}/permissions/effective/{userId}";

        // Invalid flag values must never be persisted.
        await Status(admin.PutAsJsonAsync(userRoute,
            new { permissions = 64, inheritToSubfolders = true }), HttpStatusCode.BadRequest);

        // Explicit read-only user grant must not allow creation.
        await Success(admin.PutAsJsonAsync(userRoute,
            new { permissions = (int)PermisoCarpeta.Leer, inheritToSubfolders = true }));
        Assert.Equal((int)PermisoCarpeta.Leer, await Effective(admin, effectiveParent));
        Assert.Equal((int)PermisoCarpeta.Leer, await Effective(admin, effectiveChild));
        await Success(user.GetAsync($"/api/folders?parentId={child}"));
        await RejectedWrite(user, child);

        // Device Sync must reject file creation with read-only permissions.
        using var registration = await user.PostAsJsonAsync(
            "/api/sync/devices/register",
            new { name = $"ACL-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await EnsureSuccess(registration);
        using var registrationJson = JsonDocument.Parse(
            await registration.Content.ReadAsStringAsync());
        var token = registrationJson.RootElement.GetProperty("token").GetString();
        var syncRoot = registrationJson.RootElement.GetProperty("rootFolderId").GetGuid();
        Assert.False(string.IsNullOrWhiteSpace(token));
        var syncPermissionRoute =
            $"/api/admin/folders/{syncRoot}/permissions/users/{userId}";
        await Success(admin.PutAsJsonAsync(syncPermissionRoute,
            new { permissions = (int)PermisoCarpeta.Leer, inheritToSubfolders = true }));
        using var sync = Client();
        sync.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        using (var form = new MultipartFormDataContent())
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("denegado"));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(content, "file", "read-only.txt");
            form.Add(new StringContent(syncRoot.ToString()), "folderId");
            await Status(sync.PostAsync("/api/sync/files", form), HttpStatusCode.Forbidden);
        }
        // Create a real file while creation is permitted, then revoke modification.
        await Success(admin.PutAsJsonAsync(syncPermissionRoute,
            new { permissions = (int)PermisoCarpeta.Crear, inheritToSubfolders = true }));
        const string originalContent = "contenido original protegido";
        Guid protectedFileId;
        using (var form = new MultipartFormDataContent())
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(originalContent));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            form.Add(content, "file", "protected.txt");
            form.Add(new StringContent(syncRoot.ToString()), "folderId");
            using var createdFile = await sync.PostAsync("/api/sync/files", form);
            await EnsureSuccess(createdFile);
            using var createdJson = JsonDocument.Parse(
                await createdFile.Content.ReadAsStringAsync());
            protectedFileId = createdJson.RootElement.GetProperty("id").GetGuid();
        }

        long originalVersion;
        string originalHash;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var original = await db.Archivos.AsNoTracking()
                .SingleAsync(x => x.Id == protectedFileId);
            originalVersion = original.Version;
            originalHash = original.HashSha256;
        }

        await Success(admin.PutAsJsonAsync(syncPermissionRoute,
            new { permissions = (int)PermisoCarpeta.Leer, inheritToSubfolders = true }));

        using (var modifiedContent = new ByteArrayContent(
            Encoding.UTF8.GetBytes("contenido no autorizado")))
        {
            modifiedContent.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
            await Status(sync.PutAsync(
                $"/api/sync/files/{protectedFileId}/content?expectedVersion={originalVersion}&expectedHash={originalHash}",
                modifiedContent), HttpStatusCode.Forbidden);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var unchanged = await db.Archivos.AsNoTracking()
                .SingleAsync(x => x.Id == protectedFileId);
            Assert.Equal(originalVersion, unchanged.Version);
            Assert.Equal(originalHash, unchanged.HashSha256);
            Assert.Equal(Encoding.UTF8.GetByteCount(originalContent), unchanged.TamanoBytes);
        }

        using (var download = await admin.GetAsync(
            $"/api/files/{protectedFileId}/download"))
        {
            await EnsureSuccess(download);
            Assert.Equal(originalContent, await download.Content.ReadAsStringAsync());
        }
        await Success(admin.DeleteAsync(syncPermissionRoute));

        // Removing inheritance removes the descendant grant, but not the parent's.
        await Success(admin.PutAsJsonAsync(userRoute,
            new { permissions = (int)PermisoCarpeta.Leer, inheritToSubfolders = false }));
        Assert.Equal((int)PermisoCarpeta.Leer, await Effective(admin, effectiveParent));
        Assert.Equal(0, await Effective(admin, effectiveChild));

        // Group grant applies through membership and inheritance.
        await Success(admin.PostAsync($"/api/admin/groups/{group}/users/{userId}", null));
        await Success(admin.PutAsJsonAsync(groupRoute,
            new { permissions = (int)PermisoCarpeta.ControlTotal, inheritToSubfolders = true }));
        Assert.Equal((int)PermisoCarpeta.ControlTotal, await Effective(admin, effectiveChild));

        // A group rule can be edited and must affect effective permissions.
        await Success(admin.PutAsJsonAsync(groupRoute,
            new { permissions = (int)PermisoCarpeta.Leer, inheritToSubfolders = true }));
        Assert.Equal((int)PermisoCarpeta.Leer, await Effective(admin, effectiveChild));
        await RejectedWrite(user, child);

        // Revoking the group rule removes inherited access.
        await Success(admin.DeleteAsync(groupRoute));
        Assert.Equal(0, await Effective(admin, effectiveChild));

        // Explicit user grant can also be revoked.
        await Success(admin.DeleteAsync(userRoute));
        Assert.Equal(0, await Effective(admin, effectiveParent));

        // Non-administrators cannot edit ACLs.
        await Status(user.PutAsJsonAsync(userRoute,
            new { permissions = 31, inheritToSubfolders = true }), HttpStatusCode.Forbidden);
        await Success(admin.DeleteAsync($"/api/admin/groups/{group}/users/{userId}"));
    }

    private HttpClient Client() => _factory.CreateClient(new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("https://localhost")
    });

    private static async Task Login(HttpClient client, string username, string password)
    {
        await Status(client.PostAsJsonAsync("/api/auth/login",
            new { userName = username, password }), HttpStatusCode.OK);
    }

    private static async Task<Guid> CreateId(HttpClient client, string route, object payload)
    {
        using var response = await client.PostAsJsonAsync(route, payload);
        await EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var key in new[] { "id", "folderId", "groupId" })
        {
            if (json.RootElement.TryGetProperty(key, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                Guid.TryParse(value.GetString(), out var id))
                return id;
        }
        throw new Xunit.Sdk.XunitException($"Missing ID in {route}: {json.RootElement}");
    }

    private static async Task<int> Effective(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route);
        await EnsureSuccess(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("value").GetInt32();
    }

    private static async Task RejectedWrite(HttpClient user, Guid parentId)
    {
        using var response = await user.PostAsJsonAsync("/api/folders",
            new { name = $"Denied-{Guid.NewGuid():N}", parentId });
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized,
            $"Read-only user created folder: HTTP {(int)response.StatusCode}.");
    }

    private static async Task Status(Task<HttpResponseMessage> task, HttpStatusCode expected)
    {
        using var response = await task;
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task Success(Task<HttpResponseMessage> task)
    {
        using var response = await task;
        await EnsureSuccess(response);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
