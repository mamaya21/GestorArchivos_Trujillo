using GestorArchivos.Sync.Windows.State;
using GestorArchivos.Sync.Windows.Storage;
using Xunit;

namespace GestorArchivos.Sync.Windows.Tests;

public sealed class LocalStateRestartTests
{
    [Fact]
    public async Task Restart_LoadsSameDeviceCursorAndInitialSyncCheckpoint()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "state.json");
        var device = Guid.NewGuid();
        var checkpoint = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        var firstProcess = new LocalStateStore(path);
        await firstProcess.SaveAsync(new SyncRuntimeState
        {
            DeviceId = device,
            LastChangeId = 492,
            LastSuccessfulSyncUtc = checkpoint,
            IsInitialSyncCompleted = true,
            IsOnline = true
        });

        // Nueva instancia: el estado debe recuperarse desde disco, no de memoria.
        var restartedProcess = new LocalStateStore(path);
        var restored = await restartedProcess.LoadAsync();

        Assert.Equal(device, restored.DeviceId);
        Assert.Equal(492, restored.LastChangeId);
        Assert.Equal(checkpoint, restored.LastSuccessfulSyncUtc);
        Assert.True(restored.IsInitialSyncCompleted);
    }

    [Fact]
    public async Task Restart_WithMissingState_DoesNotInventCompletedSyncOrCursor()
    {
        using var temp = new TemporaryDirectory();
        var restored = await new LocalStateStore(
            Path.Combine(temp.Path, "state.json")).LoadAsync();

        Assert.Null(restored.DeviceId);
        Assert.Equal(0, restored.LastChangeId);
        Assert.False(restored.IsInitialSyncCompleted);
        Assert.Null(restored.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task ConsecutiveSaves_ReplaceCheckpointWithoutLeavingTemporaryFile()
    {
        using var temp = new TemporaryDirectory();
        var path = Path.Combine(temp.Path, "state.json");
        var device = Guid.NewGuid();
        var store = new LocalStateStore(path);

        await store.SaveAsync(new SyncRuntimeState
        {
            DeviceId = device,
            LastChangeId = 12
        });
        await store.SaveAsync(new SyncRuntimeState
        {
            DeviceId = device,
            LastChangeId = 25,
            IsInitialSyncCompleted = true
        });

        var restarted = await new LocalStateStore(path).LoadAsync();
        Assert.Equal(device, restarted.DeviceId);
        Assert.Equal(25, restarted.LastChangeId);
        Assert.True(restarted.IsInitialSyncCompleted);
        Assert.False(File.Exists(path + ".tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "GestorSyncStateTests", Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
