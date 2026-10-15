using System.Text.Json;
using System.Text.Json.Serialization;

namespace GestorArchivos.Sync.Windows.Models;

public sealed class SyncChange
{
    public long Id { get; set; }
    public Guid? ArchivoId { get; set; }
    public Guid? CarpetaId { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    [JsonConverter(typeof(SyncChangeTypeConverter))]
    public int TipoCambio { get; set; }

    public long? VersionArchivo { get; set; }
    public Guid? DispositivoOrigenId { get; set; }
    public DateTime FechaUtc { get; set; }
}

public sealed class SyncChangesResponse
{
    public List<SyncChange> Changes { get; set; } = [];
    public long LastId { get; set; }
    public bool HasMore { get; set; }
}

public sealed class SyncChangeTypeConverter
    : JsonConverter<int>
{
    public override int Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.GetInt32();

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();

            if (int.TryParse(value, out var number))
                return number;

            // El cliente no utiliza TipoCambio para decidir
            // cómo aplicar cambios: utiliza ArchivoId/CarpetaId.
            return 0;
        }

        throw new JsonException(
            "TipoCambio tiene un formato no válido.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        int value,
        JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}