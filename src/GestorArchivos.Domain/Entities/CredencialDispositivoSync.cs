namespace GestorArchivos.Domain.Entities;

public class CredencialDispositivoSync
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DispositivoId { get; set; }

    public DispositivoSync Dispositivo { get; set; } = null!;

    public string TokenHash { get; set; } = string.Empty;

    public DateTime FechaCreacionUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UltimoUsoUtc { get; set; }

    public DateTime? FechaRevocacionUtc { get; set; }

    public bool Revocada { get; set; }
}