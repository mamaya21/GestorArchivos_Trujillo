namespace GestorArchivos.Domain.Entities;

public class BloqueoArchivo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ArchivoId { get; set; }

    public Archivo Archivo { get; set; } = null!;

    public Guid UsuarioId { get; set; }

    public Guid? DispositivoId { get; set; }

    public DispositivoSync? Dispositivo { get; set; }

    public DateTime FechaBloqueoUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiraUtc { get; set; }

    public bool Activo { get; set; } = true;
}