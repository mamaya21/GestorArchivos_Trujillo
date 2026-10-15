namespace GestorArchivos.Sync.Windows.Storage;

public interface ILocalFileStore
{
    string RootPath { get; }

    string GetFullPath(string relativePath);

    bool FileExists(string relativePath);

    bool DirectoryExists(string relativePath);

    Task<Stream> OpenReadAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        string relativePath,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<bool> WriteIfUnchangedAsync(
        string relativePath,
        Stream content,
        string? expectedLocalHash,
        CancellationToken cancellationToken = default);

    void CreateDirectory(string relativePath);

    void DeleteFile(string relativePath);

    void DeleteDirectory(string relativePath, bool recursive = true);

    void MoveFile(string sourceRelativePath, string destinationRelativePath);

    void MoveDirectory(string sourceRelativePath, string destinationRelativePath);
}