namespace GestorArchivos.Application.Common.Storage;

public interface IFileStorage
{
    Task SaveAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default);

    Task ReplaceAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        string relativePath,
        CancellationToken cancellationToken = default);
}