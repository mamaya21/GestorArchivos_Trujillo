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

    [Fact]
    public async Task LostFolderCreateResponse_RetryAfterReconnect_ReusesSameRemoteFolder()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name = $"RetryFolder-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var root = device.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = device.RootElement.GetProperty("token").GetString()!;
        var name = $"replay-{Guid.NewGuid():N}";
        var path = $"C:\\SyncReplay\\{name}";
        var request = new { parentFolderId = root, name, localPath = path };

        Guid originalFolder;
        Guid originalMapping;
        using (var first = SyncClient(token))
        using (var created = await first.PostAsJsonAsync("/api/sync/folders/atomic", request))
        {
            await RequireSuccess(created);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            originalFolder = json.RootElement.GetProperty("remoteFolderId").GetGuid();
            originalMapping = json.RootElement.GetProperty("syncItemId").GetGuid();
            Assert.False(json.RootElement.GetProperty("alreadyExisted").GetBoolean());
        }

        // Simula respuesta perdida: repetir exactamente la operación desde otra conexión.
        using (var restarted = SyncClient(token))
        using (var replay = await restarted.PostAsJsonAsync("/api/sync/folders/atomic", request))
        {
            await RequireSuccess(replay);
            using var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("alreadyExisted").GetBoolean());
            Assert.Equal(originalFolder, json.RootElement.GetProperty("remoteFolderId").GetGuid());
            Assert.Equal(originalMapping, json.RootElement.GetProperty("syncItemId").GetGuid());
        }
    }

    [Fact]
    public async Task LostFileCreateResponse_RetryAfterReconnect_ReusesSameRemoteFile()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name = $"RetryFile-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var root = device.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = device.RootElement.GetProperty("token").GetString()!;
        var name = $"replay-{Guid.NewGuid():N}.txt";
        var path = $"C:\\SyncReplay\\{name}";
        var payload = System.Text.Encoding.UTF8.GetBytes("Replay content must be unique and unchanged.");

        static MultipartFormDataContent Content(Guid folder, string localPath,
            string fileName, byte[] bytes)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(folder.ToString()), "folderId");
            form.Add(new StringContent(localPath), "localPath");
            form.Add(new ByteArrayContent(bytes), "file", fileName);
            return form;
        }

        Guid originalFile;
        Guid originalMapping;
        using (var first = SyncClient(token))
        using (var body = Content(root, path, name, payload))
        using (var created = await first.PostAsync("/api/sync/files/atomic", body))
        {
            await RequireSuccess(created);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            originalFile = json.RootElement.GetProperty("remoteFileId").GetGuid();
            originalMapping = json.RootElement.GetProperty("syncItemId").GetGuid();
            Assert.False(json.RootElement.GetProperty("alreadyExisted").GetBoolean());
        }

        using (var restarted = SyncClient(token))
        using (var body = Content(root, path, name, payload))
        using (var replay = await restarted.PostAsync("/api/sync/files/atomic", body))
        {
            await RequireSuccess(replay);
            using var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("alreadyExisted").GetBoolean());
            Assert.Equal(originalFile, json.RootElement.GetProperty("remoteFileId").GetGuid());
            Assert.Equal(originalMapping, json.RootElement.GetProperty("syncItemId").GetGuid());
        }
    }

    [Fact]
    public async Task LostFileCreateResponse_ChangedLocalBytes_RejectsUnsafeReplay()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name = $"ChangedReplay-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var root = device.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = device.RootElement.GetProperty("token").GetString()!;
        var name = $"changed-{Guid.NewGuid():N}.txt";
        var path = $"C:\\SyncReplay\\{name}";

        static MultipartFormDataContent Content(Guid folder, string localPath,
            string fileName, string body)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(folder.ToString()), "folderId");
            form.Add(new StringContent(localPath), "localPath");
            form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body)),
                "file", fileName);
            return form;
        }

        Guid originalId;
        using (var first = SyncClient(token))
        using (var content = Content(root, path, name, "Original content"))
        using (var created = await first.PostAsync("/api/sync/files/atomic", content))
        {
            await RequireSuccess(created);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            originalId = json.RootElement.GetProperty("remoteFileId").GetGuid();
        }

        using (var restarted = SyncClient(token))
        using (var changed = Content(root, path, name, "New local content"))
        using (var retry = await restarted.PostAsync("/api/sync/files/atomic", changed))
        {
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        }

        using (var verify = SyncClient(token))
        using (var itemsResponse = await verify.GetAsync("/api/sync/items"))
        {
            await RequireSuccess(itemsResponse);
            using var items = JsonDocument.Parse(await itemsResponse.Content.ReadAsStringAsync());
            var matches = items.RootElement.EnumerateArray()
                .Where(x => x.GetProperty("archivoRemotoId").ValueKind != JsonValueKind.Null &&
                            x.GetProperty("archivoRemotoId").GetGuid() == originalId)
                .ToList();
            Assert.Single(matches);
        }
    }

    [Fact]
    public async Task ChunkUpload_ReconnectAcceptsIdenticalBytes_RejectsChangedBytes()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync("/api/sync/devices/register",
            new { name = $"ChunkReplay-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var folderId = device.RootElement.GetProperty("rootFolderId").GetGuid();

        var original = System.Text.Encoding.UTF8.GetBytes("Chunk replay payload A");
        var changed = System.Text.Encoding.UTF8.GetBytes("Chunk replay payload B");
        using var init = await owner.PostAsJsonAsync("/api/uploads/initialize",
            new { folderId, fileName = $"chunk-{Guid.NewGuid():N}.txt",
                  fileSize = original.Length, contentType = "text/plain" });
        await RequireSuccess(init);
        using var initialized = JsonDocument.Parse(await init.Content.ReadAsStringAsync());
        var uploadId = initialized.RootElement.GetProperty("uploadId").GetGuid();
        var url = $"/api/uploads/{uploadId}/chunks/0";

        using (var first = new ByteArrayContent(original))
        using (var response = await owner.PutAsync(url, first))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var status = await owner.GetAsync($"/api/uploads/{uploadId}/status"))
        {
            await RequireSuccess(status);
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.Contains(0, json.RootElement.GetProperty("receivedChunks")
                .EnumerateArray().Select(x => x.GetInt32()));
        }

        using (var identical = new ByteArrayContent(original))
        using (var replay = await owner.PutAsync(url, identical))
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);

        using (var different = new ByteArrayContent(changed))
        using (var rejected = await owner.PutAsync(url, different))
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        using (var status = await owner.GetAsync($"/api/uploads/{uploadId}/status"))
        {
            await RequireSuccess(status);
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            Assert.Single(json.RootElement.GetProperty("receivedChunks").EnumerateArray());
        }

        using var cancel = await owner.DeleteAsync($"/api/uploads/{uploadId}");
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
    }

    [Fact]
    public async Task SyncUpload_IncompleteTransfer_DoesNotPublishFile()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync(
            "/api/sync/devices/register",
            new { name = $"Incomplete-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var root = device.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = device.RootElement.GetProperty("token").GetString()!;
        var name = $"incomplete-{Guid.NewGuid():N}.txt";
        using var sync = SyncClient(token);
        using var init = await sync.PostAsJsonAsync(
            "/api/sync/uploads/initialize",
            new { folderId = root, fileName = name, fileSize = 17 });
        await RequireSuccess(init);
        using var json = JsonDocument.Parse(await init.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("uploadId").GetGuid();
        using var complete = await sync.PostAsJsonAsync(
            $"/api/sync/files/atomic/uploads/{id}/complete",
            new { localPath = $"C:\\SyncReplay\\{name}" });
        Assert.Equal(HttpStatusCode.BadRequest, complete.StatusCode);
        using var status = await sync.GetAsync($"/api/sync/uploads/{id}/status");
        await RequireSuccess(status);
        using var items = await sync.GetAsync("/api/sync/items");
        await RequireSuccess(items);
        using var listing = JsonDocument.Parse(await items.Content.ReadAsStringAsync());
        Assert.DoesNotContain(listing.RootElement.EnumerateArray(),
            x => x.GetProperty("rutaLocal").GetString() == $"C:\\SyncReplay\\{name}");
    }

    [Fact]
    public async Task SyncUpload_DuplicateDestination_IsRejectedWithoutReplacingExistingFile()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync(
            "/api/sync/devices/register",
            new { name = $"DuplicateChunk-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var root = device.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = device.RootElement.GetProperty("token").GetString()!;
        var name = $"duplicate-{Guid.NewGuid():N}.txt";
        var path = $"C:\\SyncReplay\\{name}";
        using var sync = SyncClient(token);
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new StringContent(root.ToString()), "folderId");
            form.Add(new StringContent(path), "localPath");
            form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", name);
            using var created = await sync.PostAsync("/api/sync/files/atomic", form);
            await RequireSuccess(created);
        }
        using var init = await sync.PostAsJsonAsync(
            "/api/sync/uploads/initialize",
            new { folderId = root, fileName = name, fileSize = 3 });
        await RequireSuccess(init);
        using var json = JsonDocument.Parse(await init.Content.ReadAsStringAsync());
        var id = json.RootElement.GetProperty("uploadId").GetGuid();
        using (var chunk = new ByteArrayContent(new byte[] { 9, 8, 7 }))
        using (var sent = await sync.PutAsync(
            $"/api/sync/uploads/{id}/chunks/0", chunk))
            Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        using var complete = await sync.PostAsJsonAsync(
            $"/api/sync/files/atomic/uploads/{id}/complete", new { localPath = path });
        Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        using var status = await sync.GetAsync($"/api/sync/uploads/{id}/status");
        await RequireSuccess(status);
    }

    [Fact]
    public async Task SyncUpload_ReconnectAfterFirstChunk_ContinuesMissingChunk()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync(
            "/api/sync/devices/register",
            new { name = $"ChunkReconnect-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var root = device.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = device.RootElement.GetProperty("token").GetString()!;
        var name = $"resume-{Guid.NewGuid():N}.bin";
        var localPath = $"C:\\SyncReplay\\{name}";
        var size = 64 * 1024 * 1024 + 7;

        Guid uploadId;
        using (var first = SyncClient(token))
        {
            using var init = await first.PostAsJsonAsync(
                "/api/sync/uploads/initialize",
                new { folderId = root, fileName = name, fileSize = size });
            await RequireSuccess(init);
            using var json = JsonDocument.Parse(await init.Content.ReadAsStringAsync());
            uploadId = json.RootElement.GetProperty("uploadId").GetGuid();

            using var chunk = new ByteArrayContent(new byte[64 * 1024 * 1024]);
            using var sent = await first.PutAsync(
                $"/api/sync/uploads/{uploadId}/chunks/0", chunk);
            Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        }

        // A fresh HTTP client models a dropped connection after the first chunk.
        using (var reconnected = SyncClient(token))
        {
            using var status = await reconnected.GetAsync(
                $"/api/sync/uploads/{uploadId}/status");
            await RequireSuccess(status);
            using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            var received = json.RootElement.GetProperty("receivedChunks")
                .EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(new[] { 0 }, received);

            using var chunk = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            using var sent = await reconnected.PutAsync(
                $"/api/sync/uploads/{uploadId}/chunks/1", chunk);
            Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

            using var completed = await reconnected.PostAsJsonAsync(
                $"/api/sync/files/atomic/uploads/{uploadId}/complete",
                new { localPath });
            await RequireSuccess(completed);
            using var result = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
            Assert.NotEqual(Guid.Empty,
                result.RootElement.GetProperty("remoteFileId").GetGuid());
        }
    }

    [Fact]
    public async Task SyncUpload_CompletedChunks_CreateFileAndMappingAtomically()
    {
        using var owner = Client();
        await Login(owner);
        using var registration = await owner.PostAsJsonAsync(
            "/api/sync/devices/register",
            new { name = $"CompletedChunks-{Guid.NewGuid():N}", operatingSystem = "Windows" });
        await RequireSuccess(registration);
        using var device = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var root = device.RootElement.GetProperty("rootFolderId").GetGuid();
        var token = device.RootElement.GetProperty("token").GetString()!;
        var name = $"chunk-created-{Guid.NewGuid():N}.txt";
        var path = $"C:\\SyncReplay\\{name}";
        var bytes = System.Text.Encoding.UTF8.GetBytes("Atomic chunk publication");

        using var sync = SyncClient(token);
        using var initialized = await sync.PostAsJsonAsync(
            "/api/sync/uploads/initialize",
            new { folderId = root, fileName = name, fileSize = bytes.Length });
        await RequireSuccess(initialized);
        using var initJson = JsonDocument.Parse(await initialized.Content.ReadAsStringAsync());
        var uploadId = initJson.RootElement.GetProperty("uploadId").GetGuid();

        using (var chunk = new ByteArrayContent(bytes))
        using (var sent = await sync.PutAsync(
            $"/api/sync/uploads/{uploadId}/chunks/0", chunk))
            Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        using var completed = await sync.PostAsJsonAsync(
            $"/api/sync/files/atomic/uploads/{uploadId}/complete",
            new { localPath = path });
        await RequireSuccess(completed);
        using var result = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());
        var remoteId = result.RootElement.GetProperty("remoteFileId").GetGuid();
        Assert.NotEqual(Guid.Empty, remoteId);
        Assert.NotEqual(Guid.Empty, result.RootElement.GetProperty("syncItemId").GetGuid());
        Assert.Equal(name, result.RootElement.GetProperty("nombre").GetString());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            result.RootElement.GetProperty("hashSha256").GetString());

        using var file = await sync.GetAsync($"/api/sync/read/files/{remoteId}");
        await RequireSuccess(file);
        using var status = await sync.GetAsync($"/api/sync/uploads/{uploadId}/status");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }

    [Fact]
    public async Task SyncUpload_DeviceSession_IsNotAccessibleToAnotherDevice()
    {
        using var owner = Client();
        await Login(owner);

        async Task<(Guid Root, string Token)> Register(string name)
        {
            using var response = await owner.PostAsJsonAsync(
                "/api/sync/devices/register",
                new { name, operatingSystem = "Windows" });
            await RequireSuccess(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (json.RootElement.GetProperty("rootFolderId").GetGuid(),
                json.RootElement.GetProperty("token").GetString()!);
        }

        var first = await Register($"UploadOwner-{Guid.NewGuid():N}");
        var second = await Register($"UploadOther-{Guid.NewGuid():N}");
        using var firstDevice = SyncClient(first.Token);
        using var otherDevice = SyncClient(second.Token);

        using var initialize = await firstDevice.PostAsJsonAsync(
            "/api/sync/uploads/initialize",
            new { folderId = first.Root, fileName = "isolated.bin", fileSize = 4 });
        await RequireSuccess(initialize);
        using var json = JsonDocument.Parse(await initialize.Content.ReadAsStringAsync());
        var uploadId = json.RootElement.GetProperty("uploadId").GetGuid();

        using (var denied = await otherDevice.GetAsync(
            $"/api/sync/uploads/{uploadId}/status"))
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using (var payload = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }))
        using (var denied = await otherDevice.PutAsync(
            $"/api/sync/uploads/{uploadId}/chunks/0", payload))
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using (var status = await firstDevice.GetAsync(
            $"/api/sync/uploads/{uploadId}/status"))
            await RequireSuccess(status);

        using var cancel = await firstDevice.DeleteAsync(
            $"/api/sync/uploads/{uploadId}");
        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
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
