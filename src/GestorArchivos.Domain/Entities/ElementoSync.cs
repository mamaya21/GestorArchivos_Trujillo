namespace GestorArchivos.Domain.Entities;

public class ElementoSync
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DispositivoId { get; set; }

    public DispositivoSync Dispositivo { get; set; } = null!;

    public string RutaLocal { get; set; } = string.Empty;

    public TipoElementoSync Tipo { get; set; }

    public Guid? CarpetaRemotaId { get; set; }

    public Carpeta? CarpetaRemota { get; set; }

    public Guid? ArchivoRemotoId { get; set; }

    public Archivo? ArchivoRemoto { get; set; }

    public bool Activo { get; set; } = true;

    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    public DateTime? UltimaSincronizacionUtc { get; set; }
}

public enum TipoElementoSync
{
    Archivo = 1,
    Carpeta = 2
}