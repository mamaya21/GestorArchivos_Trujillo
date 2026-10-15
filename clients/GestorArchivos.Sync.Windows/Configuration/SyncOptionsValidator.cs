using Microsoft.Extensions.Options;

namespace GestorArchivos.Sync.Windows.Configuration;

public sealed class SyncOptionsValidator : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        SyncOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiBaseUrl))
            return ValidateOptionsResult.Fail("Sync:ApiBaseUrl es obligatorio.");

        if (!Uri.TryCreate(
                options.ApiBaseUrl,
                UriKind.Absolute,
                out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                "Sync:ApiBaseUrl debe ser una URL HTTP/HTTPS válida.");
        }

        if (string.IsNullOrWhiteSpace(options.LocalRootPath))
            return ValidateOptionsResult.Fail("Sync:LocalRootPath es obligatorio.");

        if (options.PollIntervalSeconds < 1)
            return ValidateOptionsResult.Fail(
                "Sync:PollIntervalSeconds debe ser mayor o igual a 1.");

        if (options.ChangeBatchSize is < 1 or > 500)
            return ValidateOptionsResult.Fail(
                "Sync:ChangeBatchSize debe estar entre 1 y 500.");

        return ValidateOptionsResult.Success;
    }
}