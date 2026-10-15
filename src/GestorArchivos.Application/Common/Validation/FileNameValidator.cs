using System.Text;

namespace GestorArchivos.Application.Common.Validation;

public static class FileNameValidator
{
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9"
        };

    private static readonly char[] InvalidCharacters =
    {
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    };

    public static bool TryValidate(
        string? value,
        out string normalizedName,
        out string error)
    {
        normalizedName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "El nombre es obligatorio.";
            return false;
        }

        if (value.StartsWith(' ') ||
            value.EndsWith(' ') ||
            value.EndsWith('.'))
        {
            error = "El nombre no puede comenzar o terminar en espacio ni terminar en punto.";
            return false;
        }

        normalizedName =
            value.Normalize(NormalizationForm.FormC);

        if (normalizedName.Length > 255)
        {
            error = "El nombre supera los 255 caracteres.";
            return false;
        }

        if (normalizedName is "." or "..")
        {
            error = "El nombre no es válido.";
            return false;
        }

        if (normalizedName.IndexOfAny(
                InvalidCharacters) >= 0)
        {
            error =
                "El nombre contiene caracteres no permitidos.";
            return false;
        }

        if (normalizedName.Any(char.IsControl))
        {
            error =
                "El nombre contiene caracteres de control.";
            return false;
        }

        var baseName =
            normalizedName.Split('.')[0];

        if (ReservedNames.Contains(baseName))
        {
            error =
                "El nombre está reservado por Windows.";
            return false;
        }

        return true;
    }
}