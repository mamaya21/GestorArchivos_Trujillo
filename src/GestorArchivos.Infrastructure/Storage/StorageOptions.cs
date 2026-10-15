namespace GestorArchivos.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = string.Empty;
}