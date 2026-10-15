namespace GestorArchivos.Application.Common.Interfaces;

public interface ISyncChangeService
{
    Task RegisterAsync(
        Guid folderId,
        Guid? fileId,
        Guid? changedFolderId,
        string changeType,
        long? fileVersion = null,
        Guid? sourceDeviceId = null,
        CancellationToken cancellationToken = default);
}