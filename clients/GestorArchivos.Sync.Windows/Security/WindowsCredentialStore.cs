using System.Security.Cryptography;
using System.Text;

namespace GestorArchivos.Sync.Windows.Security;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private readonly string _credentialPath;

    public WindowsCredentialStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GestorArchivos",
            "Sync");

        Directory.CreateDirectory(directory);

        _credentialPath = Path.Combine(directory, "credentials.dat");
    }

    public async Task<string?> GetTokenAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_credentialPath))
            return null;

        var encrypted = await File.ReadAllBytesAsync(
            _credentialPath,
            cancellationToken);

        if (encrypted.Length == 0)
            return null;

        var decrypted = ProtectedData.Unprotect(
            encrypted,
            null,
            DataProtectionScope.LocalMachine);

        return Encoding.UTF8.GetString(decrypted);
    }

    public async Task SaveTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var plain = Encoding.UTF8.GetBytes(token);

        var encrypted = ProtectedData.Protect(
            plain,
            null,
            DataProtectionScope.LocalMachine);

        var directory = Path.GetDirectoryName(_credentialPath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = _credentialPath + ".tmp";

        await File.WriteAllBytesAsync(
            temporaryPath,
            encrypted,
            cancellationToken);

        File.Move(
            temporaryPath,
            _credentialPath,
            true);
    }

    public Task DeleteTokenAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(_credentialPath))
            File.Delete(_credentialPath);

        return Task.CompletedTask;
    }
}