namespace GestorArchivos.Sync.Windows.Security;

public interface ICredentialStore
{
    Task<string?> GetTokenAsync(
        CancellationToken cancellationToken = default);

    Task SaveTokenAsync(
        string token,
        CancellationToken cancellationToken = default);

    Task DeleteTokenAsync(
        CancellationToken cancellationToken = default);
}