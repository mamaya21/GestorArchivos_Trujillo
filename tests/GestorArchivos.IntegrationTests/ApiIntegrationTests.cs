using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GestorArchivos.IntegrationTests;

public sealed class ApiIntegrationTests :
    IClassFixture<GestorArchivosWebApplicationFactory>
{
    private const string AdminPassword = "IntegrationTest123!";

    private readonly GestorArchivosWebApplicationFactory _factory;

    public ApiIntegrationTests(
        GestorArchivosWebApplicationFactory factory)
    {
        _factory = factory;
    }


    public static IEnumerable<object[]> TodosLosEndpoints()
    {
        yield return new object[] { "GET", "/api/admin/audit" };
        yield return new object[] { "POST", "/api/auth/login" };
        yield return new object[] { "GET", "/api/auth/me" };
        yield return new object[] { "POST", "/api/auth/change-password" };
        yield return new object[] { "POST", "/api/auth/logout" };
        yield return new object[] { "POST", "/api/files/upload" };
        yield return new object[] { "GET", "/api/files/{id}" };
        yield return new object[] { "DELETE", "/api/files/{id}" };
        yield return new object[] { "GET", "/api/files/{id}/download" };
        yield return new object[] { "GET", "/api/files/{id}/open" };
        yield return new object[] { "PUT", "/api/files/{id}/rename" };
        yield return new object[] { "GET", "/api/admin/folders/{folderId}/permissions" };
        yield return new object[] { "PUT", "/api/admin/folders/{folderId}/permissions/users/{userId}" };
        yield return new object[] { "DELETE", "/api/admin/folders/{folderId}/permissions/users/{userId}" };
        yield return new object[] { "PUT", "/api/admin/folders/{folderId}/permissions/groups/{groupId}" };
        yield return new object[] { "DELETE", "/api/admin/folders/{folderId}/permissions/groups/{groupId}" };
        yield return new object[] { "GET", "/api/admin/folders/{folderId}/permissions/effective/{userId}" };
        yield return new object[] { "GET", "/api/folders" };
        yield return new object[] { "POST", "/api/folders" };
        yield return new object[] { "GET", "/api/folders/{id}" };
        yield return new object[] { "DELETE", "/api/folders/{id}" };
        yield return new object[] { "PUT", "/api/folders/{id}/rename" };
        yield return new object[] { "GET", "/api/admin/groups" };
        yield return new object[] { "POST", "/api/admin/groups" };
        yield return new object[] { "PUT", "/api/admin/groups/{id}" };
        yield return new object[] { "POST", "/api/admin/groups/{groupId}/users/{userId}" };
        yield return new object[] { "DELETE", "/api/admin/groups/{groupId}/users/{userId}" };
        yield return new object[] { "GET", "/api/admin/groups/{groupId}/users" };
        yield return new object[] { "GET", "/api/sync/changes" };
        yield return new object[] { "GET", "/api/sync/status" };
        yield return new object[] { "GET", "/api/sync/items" };
        yield return new object[] { "POST", "/api/sync/items" };
        yield return new object[] { "PUT", "/api/sync/items/{syncItemId}/path" };
        yield return new object[] { "PUT", "/api/sync/items/{syncItemId}/synced" };
        yield return new object[] { "DELETE", "/api/sync/items/{syncItemId}" };
        yield return new object[] { "GET", "/api/sync/snapshot" };
        yield return new object[] { "POST", "/api/sync/auth/validate" };
        yield return new object[] { "POST", "/api/sync/devices/register" };
        yield return new object[] { "POST", "/api/sync/files" };
        yield return new object[] { "PUT", "/api/sync/files/{fileId}/content" };
        yield return new object[] { "POST", "/api/sync/files/{fileId}/lock" };
        yield return new object[] { "DELETE", "/api/sync/files/{fileId}/lock" };
        yield return new object[] { "POST", "/api/sync/files/{fileId}/lock/renew" };
        yield return new object[] { "PUT", "/api/sync/files/{fileId}/rename" };
        yield return new object[] { "PUT", "/api/sync/files/{fileId}/move" };
        yield return new object[] { "DELETE", "/api/sync/files/{fileId}" };
        yield return new object[] { "POST", "/api/sync/folders" };
        yield return new object[] { "PUT", "/api/sync/folders/{folderId}/rename" };
        yield return new object[] { "PUT", "/api/sync/folders/{folderId}/move" };
        yield return new object[] { "DELETE", "/api/sync/folders/{folderId}" };
        yield return new object[] { "GET", "/api/sync/read/files/{fileId}" };
        yield return new object[] { "GET", "/api/sync/read/files/{fileId}/download" };
        yield return new object[] { "GET", "/api/sync/read/folders/{folderId}" };
        yield return new object[] { "GET", "/api/sync/read/folders/{folderId}/contents" };
        yield return new object[] { "GET", "/api/trash" };
        yield return new object[] { "POST", "/api/trash/folders/{id}/restore" };
        yield return new object[] { "POST", "/api/trash/files/{id}/restore" };
        yield return new object[] { "DELETE", "/api/trash/files/{id}" };
        yield return new object[] { "DELETE", "/api/trash/folders/{id}" };
        yield return new object[] { "POST", "/api/uploads/initialize" };
        yield return new object[] { "PUT", "/api/uploads/{uploadId}/chunks/{chunkIndex}" };
        yield return new object[] { "GET", "/api/uploads/{uploadId}/status" };
        yield return new object[] { "POST", "/api/uploads/{uploadId}/complete" };
        yield return new object[] { "DELETE", "/api/uploads/{uploadId}" };
        yield return new object[] { "GET", "/api/admin/users" };
        yield return new object[] { "POST", "/api/admin/users" };
        yield return new object[] { "GET", "/api/admin/users/{id}" };
        yield return new object[] { "PUT", "/api/admin/users/{id}" };
        yield return new object[] { "PUT", "/api/admin/users/{id}/role" };
        yield return new object[] { "PUT", "/api/admin/users/{id}/status" };
        yield return new object[] { "POST", "/api/admin/users/{id}/reset-password" };
    }

    [Theory]
    [MemberData(nameof(TodosLosEndpoints))]
    public async Task Contrato_Todos_Los_Endpoints_Estan_Registrados(
        string method,
        string routeTemplate)
    {
        using var client = CreateClient();
        var id = Guid.NewGuid().ToString();
        var route = routeTemplate
            .Replace("{id}", id)
            .Replace("{folderId}", id)
            .Replace("{userId}", id)
            .Replace("{groupId}", id)
            .Replace("{syncItemId}", id)
            .Replace("{fileId}", id)
            .Replace("{uploadId}", id)
            .Replace("{chunkIndex}", "0");

        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            route);

        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TODOS_Los_70_Endpoints_Deben_Funcionar()
    {
        await PrepararUsuariosAsync();

        using var anonymous = CreateClient();

        await AssertStatus(
            anonymous.GetAsync("/api/auth/me"),
            HttpStatusCode.Unauthorized);

        await AssertStatus(
            anonymous.GetAsync("/api/folders"),
            HttpStatusCode.Unauthorized);

        await AssertStatus(
            anonymous.GetAsync("/api/sync/status"),
            HttpStatusCode.Unauthorized);

        await AssertStatus(
            anonymous.PostAsJsonAsync(
                "/api/auth/login",
                new
                {
                    userName = "admin",
                    password = "Incorrecta123!"
                }),
            HttpStatusCode.Unauthorized);

        using var admin = CreateClient();

        await Login(admin, "admin", AdminPassword);

        var me = await GetJson(admin, "/api/auth/me");

        Assert.Equal(
            "admin",
            me.RootElement.GetProperty("userName").GetString());

        await AssertSuccess(
            admin.GetAsync("/api/admin/users"));

        await AssertSuccess(
            admin.GetAsync("/api/admin/groups"));

        await AssertSuccess(
            admin.GetAsync("/api/admin/audit"));

        await ProbarAdministracionUsuarios(admin);
        await ProbarCambioPassword();

        var rootFolderId =
            await CrearCarpetaRaiz(admin);

        var childFolderId =
            await CrearCarpeta(admin, rootFolderId);

        await AssertSuccess(
            admin.GetAsync(
                $"/api/folders?parentId={rootFolderId}"));

        await AssertSuccess(
            admin.GetAsync(
                $"/api/folders/{rootFolderId}"));

        await AssertSuccess(
            admin.PutAsJsonAsync(
                $"/api/folders/{childFolderId}/rename",
                new
                {
                    name = "SubcarpetaRenombrada"
                }));

        var userId =
            await ObtenerUsuarioIdAsync("integration.user1");

        await ProbarGruposYPermisos(admin, rootFolderId, userId);

        await AssertSuccess(
            admin.PutAsJsonAsync(
                $"/api/admin/folders/{rootFolderId}/permissions/users/{userId}",
                new
                {
                    permissions = 31,
                    inheritToSubfolders = true
                }));

        await AssertSuccess(
            admin.GetAsync(
                $"/api/admin/folders/{rootFolderId}/permissions"));

        await AssertSuccess(
            admin.GetAsync(
                $"/api/admin/folders/{rootFolderId}/permissions/effective/{userId}"));

        using var user = CreateClient();

        await Login(
            user,
            "integration.user1",
            "IntegrationUser123!");

        await AssertSuccess(
            user.GetAsync(
                $"/api/folders?parentId={rootFolderId}"));

        var fileId =
            await SubirArchivo(
                user,
                rootFolderId,
                "integracion.txt",
                "contenido inicial");

        await AssertSuccess(
            user.GetAsync($"/api/files/{fileId}"));

        var download =
            await user.GetAsync(
                $"/api/files/{fileId}/download");

        Assert.Equal(
            HttpStatusCode.OK,
            download.StatusCode);

        Assert.Equal(
            "contenido inicial",
            await download.Content.ReadAsStringAsync());

        await AssertSuccess(
            user.GetAsync(
                $"/api/files/{fileId}/open"));

        await AssertSuccess(
            user.PutAsJsonAsync(
                $"/api/files/{fileId}/rename",
                new
                {
                    name = "integracion-renombrado.txt"
                }));

        await ProbarUploadChunked(
            user,
            rootFolderId);

        await ProbarCancelacionUpload(user, rootFolderId);
        await ProbarPapeleraCarpeta(user, rootFolderId);

        var device =
            await RegistrarDispositivo(
                user,
                "PC-INTEGRATION-01");

        var device2 =
            await RegistrarDispositivo(
                user,
                "PC-INTEGRATION-02");

        await ValidarToken(
            device.Token,
            device.DeviceId);

        using var sync =
            CreateSyncClient(device.Token);

        using var sync2 =
            CreateSyncClient(device2.Token);

        await AssertSuccess(
            sync.GetAsync("/api/sync/status"));

        await AssertSuccess(
            sync.GetAsync("/api/sync/items"));

        await AssertSuccess(
            sync.GetAsync("/api/sync/snapshot"));

        await AssertSuccess(
            sync.GetAsync(
                "/api/sync/changes?afterId=0&limit=100"));

        var syncFolderId =
            await CrearCarpetaSync(
                sync,
                device.RootFolderId,
                "SyncFolder");

        await AssertSuccess(
            sync.GetAsync(
                $"/api/sync/read/folders/{syncFolderId}"));

        await AssertSuccess(
            sync.GetAsync(
                $"/api/sync/read/folders/{device.RootFolderId}/contents"));

        var syncFileId =
            await CrearArchivoSync(
                sync,
                device.RootFolderId);

        await ProbarSyncItems(sync, syncFolderId, syncFileId);
        await ProbarMovimientoCarpetaSync(sync, device.RootFolderId);

        await AssertSuccess(
            sync.GetAsync(
                $"/api/sync/read/files/{syncFileId}"));

        await AssertSuccess(
            sync.GetAsync(
                $"/api/sync/read/files/{syncFileId}/download"));

        await AssertSuccess(
            sync.PostAsync(
                $"/api/sync/files/{syncFileId}/lock",
                null));

        var competingLock =
            await sync2.PostAsync(
                $"/api/sync/files/{syncFileId}/lock",
                null);

        Assert.True(
            competingLock.StatusCode is
                HttpStatusCode.Conflict or
                HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound);

        await AssertSuccess(
            sync.PostAsync(
                $"/api/sync/files/{syncFileId}/lock/renew",
                null));

        var metadata =
            await GetJson(
                sync,
                $"/api/sync/read/files/{syncFileId}");

        var version =
            metadata.RootElement.GetProperty("version").GetInt64();

        var hash =
            metadata.RootElement.GetProperty("hashSha256").GetString();

        using (var content =
               new ByteArrayContent(
                   Encoding.UTF8.GetBytes(
                       "contenido actualizado por sync")))
        {
            content.Headers.ContentType =
                new MediaTypeHeaderValue("text/plain");

            await AssertSuccess(
                sync.PutAsync(
                    $"/api/sync/files/{syncFileId}/content?expectedVersion={version}&expectedHash={Uri.EscapeDataString(hash ?? string.Empty)}",
                    content));
        }

        await AssertSuccess(
            sync.DeleteAsync(
                $"/api/sync/files/{syncFileId}/lock"));

        await AssertSuccess(
            sync.PutAsJsonAsync(
                $"/api/sync/files/{syncFileId}/rename",
                new
                {
                    name = "sync-renombrado.txt"
                }));

        await AssertSuccess(
            sync.PutAsJsonAsync(
                $"/api/sync/files/{syncFileId}/move",
                new
                {
                    destinationFolderId = syncFolderId
                }));

        await AssertSuccess(
            sync.PutAsJsonAsync(
                $"/api/sync/folders/{syncFolderId}/rename",
                new
                {
                    name = "SyncFolderRenamed"
                }));

        await AssertSuccess(
            sync.GetAsync(
                "/api/sync/changes?afterId=0&limit=100"));

        await AssertSuccess(
            sync.DeleteAsync(
                $"/api/sync/files/{syncFileId}"));

        await AssertSuccess(
            sync.DeleteAsync(
                $"/api/sync/folders/{syncFolderId}"));

        await AssertSuccess(
            user.DeleteAsync(
                $"/api/files/{fileId}"));

        await AssertSuccess(
            user.GetAsync("/api/trash"));

        await AssertSuccess(
            user.PostAsync(
                $"/api/trash/files/{fileId}/restore",
                null));

        await AssertSuccess(
            user.DeleteAsync(
                $"/api/files/{fileId}"));

        await AssertSuccess(
            user.DeleteAsync(
                $"/api/trash/files/{fileId}"));

        await AssertSuccess(
            admin.DeleteAsync(
                $"/api/admin/folders/{rootFolderId}/permissions/users/{userId}"));

        await AssertSuccess(
            admin.GetAsync("/api/admin/audit"));

        await AssertStatus(
            user.GetAsync(
                $"/api/folders?parentId={rootFolderId}"),
            HttpStatusCode.Forbidden);

        await AssertSuccess(
            user.PostAsync(
                "/api/auth/logout",
                null));

        await AssertStatus(
            user.GetAsync("/api/auth/me"),
            HttpStatusCode.Unauthorized);

        await AssertSuccess(
            admin.PostAsync(
                "/api/auth/logout",
                null));

        await AssertStatus(
            admin.GetAsync("/api/auth/me"),
            HttpStatusCode.Unauthorized);
    }

    private static async Task ProbarAdministracionUsuarios(HttpClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var userName = $"api.user.{suffix}";
        var password = "ApiUser12345!";
        var newPassword = "ApiUser54321!";

        var create = await admin.PostAsJsonAsync(
            "/api/admin/users",
            new
            {
                userName,
                email = $"{userName}@test.local",
                nombre = "Api",
                apellido = "Test",
                password,
                role = "Usuario"
            });
        await EnsureSuccess(create);
        var id = await ReadId(create);

        await AssertSuccess(admin.GetAsync($"/api/admin/users/{id}"));

        await AssertSuccess(admin.PutAsJsonAsync(
            $"/api/admin/users/{id}",
            new
            {
                email = $"{userName}.updated@test.local",
                nombre = "Api Actualizado",
                apellido = "Test Actualizado"
            }));

        await AssertSuccess(admin.PutAsJsonAsync(
            $"/api/admin/users/{id}/role",
            new { role = "Administrador" }));

        await AssertSuccess(admin.PutAsJsonAsync(
            $"/api/admin/users/{id}/role",
            new { role = "Usuario" }));

        await AssertSuccess(admin.PutAsJsonAsync(
            $"/api/admin/users/{id}/status",
            new { active = false }));

        await AssertSuccess(admin.PutAsJsonAsync(
            $"/api/admin/users/{id}/status",
            new { active = true }));

        await AssertSuccess(admin.PostAsJsonAsync(
            $"/api/admin/users/{id}/reset-password",
            new { newPassword }));
    }

    private async Task ProbarCambioPassword()
    {
        using var client = CreateClient();
        const string original = "IntegrationUser456!";
        const string temporal = "IntegrationUser789!";

        await Login(client, "integration.user2", original);

        await AssertSuccess(client.PostAsJsonAsync(
            "/api/auth/change-password",
            new
            {
                currentPassword = original,
                newPassword = temporal
            }));

        await AssertSuccess(client.PostAsync("/api/auth/logout", null));
        await Login(client, "integration.user2", temporal);

        await AssertSuccess(client.PostAsJsonAsync(
            "/api/auth/change-password",
            new
            {
                currentPassword = temporal,
                newPassword = original
            }));

        await AssertSuccess(client.PostAsync("/api/auth/logout", null));
    }

    private static async Task ProbarGruposYPermisos(
        HttpClient admin,
        Guid folderId,
        Guid userId)
    {
        var name = $"Grupo-{Guid.NewGuid():N}";
        var create = await admin.PostAsJsonAsync(
            "/api/admin/groups",
            new { name, description = "Integración" });
        await EnsureSuccess(create);
        var groupId = await ReadId(create);

        await AssertSuccess(admin.PutAsJsonAsync(
            $"/api/admin/groups/{groupId}",
            new
            {
                name = $"{name}-Editado",
                description = "Integración editada",
                active = true
            }));

        await AssertSuccess(admin.PostAsync(
            $"/api/admin/groups/{groupId}/users/{userId}", null));

        await AssertSuccess(admin.GetAsync(
            $"/api/admin/groups/{groupId}/users"));

        await AssertSuccess(admin.PutAsJsonAsync(
            $"/api/admin/folders/{folderId}/permissions/groups/{groupId}",
            new
            {
                permissions = 31,
                inheritToSubfolders = true
            }));

        await AssertSuccess(admin.GetAsync(
            $"/api/admin/folders/{folderId}/permissions/effective/{userId}"));

        await AssertSuccess(admin.DeleteAsync(
            $"/api/admin/folders/{folderId}/permissions/groups/{groupId}"));

        await AssertSuccess(admin.DeleteAsync(
            $"/api/admin/groups/{groupId}/users/{userId}"));
    }

    private static async Task ProbarCancelacionUpload(
        HttpClient client,
        Guid folderId)
    {
        var initialize = await client.PostAsJsonAsync(
            "/api/uploads/initialize",
            new
            {
                folderId,
                fileName = $"cancel-{Guid.NewGuid():N}.txt",
                fileSize = 4L,
                contentType = "text/plain"
            });
        await EnsureSuccess(initialize);

        using var json = JsonDocument.Parse(
            await initialize.Content.ReadAsStringAsync());
        var uploadId = json.RootElement.GetProperty("uploadId").GetGuid();

        await AssertSuccess(client.DeleteAsync($"/api/uploads/{uploadId}"));
    }

    private static async Task ProbarPapeleraCarpeta(
        HttpClient client,
        Guid parentId)
    {
        var folderId = await CrearCarpeta(client, parentId);

        await AssertSuccess(client.DeleteAsync($"/api/folders/{folderId}"));
        await AssertSuccess(client.GetAsync("/api/trash"));
        await AssertSuccess(client.PostAsync(
            $"/api/trash/folders/{folderId}/restore", null));

        await AssertSuccess(client.DeleteAsync($"/api/folders/{folderId}"));
        await AssertSuccess(client.DeleteAsync(
            $"/api/trash/folders/{folderId}"));
    }

    private static async Task ProbarSyncItems(
        HttpClient sync,
        Guid folderId,
        Guid fileId)
    {
        var folderItemResponse = await sync.PostAsJsonAsync(
            "/api/sync/items",
            new
            {
                localPath = $@"C:\Sync\Folder-{Guid.NewGuid():N}",
                type = 2,
                remoteFolderId = folderId,
                remoteFileId = (Guid?)null
            });
        await EnsureSuccess(folderItemResponse);
        var folderItemId = await ReadId(folderItemResponse);

        await AssertSuccess(sync.PutAsJsonAsync(
            $"/api/sync/items/{folderItemId}/path",
            new { localPath = $@"C:\Sync\FolderMoved-{Guid.NewGuid():N}" }));

        await AssertSuccess(sync.PutAsync(
            $"/api/sync/items/{folderItemId}/synced", null));

        await AssertSuccess(sync.DeleteAsync(
            $"/api/sync/items/{folderItemId}"));

        var fileItemResponse = await sync.PostAsJsonAsync(
            "/api/sync/items",
            new
            {
                localPath = $@"C:\Sync\File-{Guid.NewGuid():N}.txt",
                type = 1,
                remoteFolderId = (Guid?)null,
                remoteFileId = fileId
            });
        await EnsureSuccess(fileItemResponse);
        var fileItemId = await ReadId(fileItemResponse);

        await AssertSuccess(sync.DeleteAsync(
            $"/api/sync/items/{fileItemId}"));
    }

    private static async Task ProbarMovimientoCarpetaSync(
        HttpClient sync,
        Guid rootFolderId)
    {
        var sourceId = await CrearCarpetaSync(
            sync, rootFolderId, $"MoveSource-{Guid.NewGuid():N}");
        var destinationId = await CrearCarpetaSync(
            sync, rootFolderId, $"MoveDest-{Guid.NewGuid():N}");

        await AssertSuccess(sync.PutAsJsonAsync(
            $"/api/sync/folders/{sourceId}/move",
            new { destinationFolderId = destinationId }));

        await AssertSuccess(sync.DeleteAsync(
            $"/api/sync/folders/{sourceId}"));

        await AssertSuccess(sync.DeleteAsync(
            $"/api/sync/folders/{destinationId}"));
    }

    private HttpClient CreateClient()
    {
        return _factory.CreateClient(
            new()
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
                BaseAddress = new Uri("https://localhost")
            });
    }

    private HttpClient CreateSyncClient(
        string token)
    {
        var client = CreateClient();

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                token);

        return client;
    }

    private static async Task Login(
        HttpClient client,
        string userName,
        string password)
    {
        var response =
            await client.PostAsJsonAsync(
                "/api/auth/login",
                new
                {
                    userName,
                    password
                });

        Assert.Equal(
            HttpStatusCode.OK,
            response.StatusCode);
    }

    private async Task PrepararUsuariosAsync()
    {
        using var scope =
            _factory.Services.CreateScope();

        var userManager =
            scope.ServiceProvider
                .GetRequiredService<
                    UserManager<ApplicationUser>>();

        await EnsureUser(
            userManager,
            "integration.user1",
            "integration.user1@test.local",
            "IntegrationUser123!");

        await EnsureUser(
            userManager,
            "integration.user2",
            "integration.user2@test.local",
            "IntegrationUser456!");
    }

    private static async Task EnsureUser(
        UserManager<ApplicationUser> userManager,
        string userName,
        string email,
        string password)
    {
        var existing =
            await userManager.FindByNameAsync(
                userName);

        if (existing is not null)
        {
            existing.Activo = true;

            await userManager.UpdateAsync(
                existing);

            var token =
                await userManager
                    .GeneratePasswordResetTokenAsync(
                        existing);

            var reset =
                await userManager.ResetPasswordAsync(
                    existing,
                    token,
                    password);

            Assert.True(
                reset.Succeeded,
                string.Join(
                    "; ",
                    reset.Errors.Select(
                        x => x.Description)));

            return;
        }

        var user =
            new ApplicationUser
            {
                UserName = userName,
                Email = email,
                Nombre = "Usuario",
                Apellido = "Integración",
                Activo = true,
                EmailConfirmed = true
            };

        var result =
            await userManager.CreateAsync(
                user,
                password);

        Assert.True(
            result.Succeeded,
            string.Join(
                "; ",
                result.Errors.Select(
                    x => x.Description)));
    }

    private async Task<Guid> ObtenerUsuarioIdAsync(
        string userName)
    {
        using var scope =
            _factory.Services.CreateScope();

        var userManager =
            scope.ServiceProvider
                .GetRequiredService<
                    UserManager<ApplicationUser>>();

        var user =
            await userManager.FindByNameAsync(
                userName);

        Assert.NotNull(user);

        return user!.Id;
    }

    private static async Task<Guid>
        CrearCarpetaRaiz(
            HttpClient admin)
    {
        var response =
            await admin.PostAsJsonAsync(
                "/api/folders",
                new
                {
                    name =
                        $"IT-{Guid.NewGuid():N}"
                });

        await EnsureSuccess(response);

        return await ReadId(response);
    }

    private static async Task<Guid>
        CrearCarpeta(
            HttpClient client,
            Guid parentId)
    {
        var response =
            await client.PostAsJsonAsync(
                "/api/folders",
                new
                {
                    name =
                        $"Sub-{Guid.NewGuid():N}",
                    parentId
                });

        await EnsureSuccess(response);

        return await ReadId(response);
    }

    private static async Task<Guid>
        SubirArchivo(
            HttpClient client,
            Guid folderId,
            string name,
            string text)
    {
        using var form =
            new MultipartFormDataContent();

        using var bytes =
            new ByteArrayContent(
                Encoding.UTF8.GetBytes(text));

        bytes.Headers.ContentType =
            new MediaTypeHeaderValue(
                "text/plain");

        form.Add(
            bytes,
            "file",
            name);

        form.Add(
            new StringContent(
                folderId.ToString()),
            "folderId");

        var response =
            await client.PostAsync(
                "/api/files/upload",
                form);

        await EnsureSuccess(response);

        return await ReadId(response);
    }

    private static async Task
        ProbarUploadChunked(
            HttpClient client,
            Guid folderId)
    {
        var bytes =
            Encoding.UTF8.GetBytes(
                "archivo por chunks");

        var initialize =
            await client.PostAsJsonAsync(
                "/api/uploads/initialize",
                new
                {
                    folderId,
                    fileName =
                        $"chunk-{Guid.NewGuid():N}.txt",
                    fileSize =
                        (long)bytes.Length,
                    contentType =
                        "text/plain"
                });

        await EnsureSuccess(initialize);

        using var json =
            JsonDocument.Parse(
                await initialize.Content
                    .ReadAsStringAsync());

        var uploadId =
            json.RootElement
                .GetProperty("uploadId")
                .GetGuid();

        using var chunk =
            new ByteArrayContent(bytes);

        var uploadChunk =
            await client.PutAsync(
                $"/api/uploads/{uploadId}/chunks/0",
                chunk);

        await EnsureSuccess(uploadChunk);

        await AssertSuccess(
            client.GetAsync(
                $"/api/uploads/{uploadId}/status"));

        await AssertSuccess(
            client.PostAsync(
                $"/api/uploads/{uploadId}/complete",
                null));
    }

    private static async Task<SyncDevice>
        RegistrarDispositivo(
            HttpClient client,
            string name)
    {
        var response =
            await client.PostAsJsonAsync(
                "/api/sync/devices/register",
                new
                {
                    name =
                        $"{name}-{Guid.NewGuid():N}",
                    operatingSystem =
                        "Windows"
                });

        await EnsureSuccess(response);

        using var json =
            JsonDocument.Parse(
                await response.Content
                    .ReadAsStringAsync());

        var root = json.RootElement;

        return new SyncDevice(
            root.GetProperty("deviceId").GetGuid(),
            root.GetProperty("rootFolderId").GetGuid(),
            root.GetProperty("token").GetString()
                ?? throw new InvalidOperationException(
                    "Token Sync vacío."));
    }

    private async Task ValidarToken(
        string token,
        Guid expectedDeviceId)
    {
        using var client = CreateClient();

        var response =
            await client.PostAsJsonAsync(
                "/api/sync/auth/validate",
                new
                {
                    token
                });

        await EnsureSuccess(response);

        using var json =
            JsonDocument.Parse(
                await response.Content
                    .ReadAsStringAsync());

        Assert.Equal(
            expectedDeviceId,
            json.RootElement
                .GetProperty("deviceId")
                .GetGuid());
    }

    private static async Task<Guid>
        CrearCarpetaSync(
            HttpClient sync,
            Guid parentId,
            string name)
    {
        var response =
            await sync.PostAsJsonAsync(
                "/api/sync/folders",
                new
                {
                    parentFolderId = parentId,
                    name
                });

        await EnsureSuccess(response);

        return await ReadId(response);
    }

    private static async Task<Guid>
        CrearArchivoSync(
            HttpClient sync,
            Guid folderId)
    {
        using var form =
            new MultipartFormDataContent();

        using var content =
            new ByteArrayContent(
                Encoding.UTF8.GetBytes(
                    "contenido creado directamente por Sync API"));

        content.Headers.ContentType =
            new MediaTypeHeaderValue("text/plain");

        form.Add(
            content,
            "file",
            $"sync-create-{Guid.NewGuid():N}.txt");

        form.Add(
            new StringContent(folderId.ToString()),
            "folderId");

        var response =
            await sync.PostAsync(
                "/api/sync/files",
                form);

        await EnsureSuccess(response);

        return await ReadId(response);
    }

    private async Task<Guid>
        CrearArchivoFisicoEnCarpetaSync(
            Guid folderId)
    {
        using var client = CreateClient();

        await Login(
            client,
            "integration.user1",
            "IntegrationUser123!");

        return await SubirArchivo(
            client,
            folderId,
            $"sync-{Guid.NewGuid():N}.txt",
            "contenido sync inicial");
    }

    private static async Task<JsonDocument>
        GetJson(
            HttpClient client,
            string url)
    {
        var response =
            await client.GetAsync(url);

        await EnsureSuccess(response);

        return JsonDocument.Parse(
            await response.Content
                .ReadAsStringAsync());
    }

    private static async Task<Guid>
        ReadId(
            HttpResponseMessage response)
    {
        using var json =
            JsonDocument.Parse(
                await response.Content
                    .ReadAsStringAsync());

        return FindGuid(
            json.RootElement,
            "id",
            "folderId",
            "fileId",
            "groupId",
            "userId",
            "syncItemId");
    }

    private static Guid FindGuid(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(
                    name,
                    out var value) &&
                value.ValueKind ==
                    JsonValueKind.String &&
                Guid.TryParse(
                    value.GetString(),
                    out var id))
            {
                return id;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"No se encontró ID en: {element}");
    }

    private static async Task AssertSuccess(
        Task<HttpResponseMessage> task)
    {
        var response = await task;

        await EnsureSuccess(response);
    }

    private static async Task AssertStatus(
        Task<HttpResponseMessage> task,
        HttpStatusCode expected)
    {
        var response = await task;

        var body =
            await response.Content
                .ReadAsStringAsync();

        Assert.True(
            response.StatusCode == expected,
            $"Esperado {(int)expected} {expected}, " +
            $"recibido {(int)response.StatusCode} " +
            $"{response.StatusCode}. Body: {body}");
    }

    private static async Task EnsureSuccess(
        HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body =
            await response.Content
                .ReadAsStringAsync();

        throw new Xunit.Sdk.XunitException(
            $"HTTP {(int)response.StatusCode} " +
            $"{response.StatusCode}. Body: {body}");
    }

    private sealed record SyncDevice(
        Guid DeviceId,
        Guid RootFolderId,
        string Token);
}