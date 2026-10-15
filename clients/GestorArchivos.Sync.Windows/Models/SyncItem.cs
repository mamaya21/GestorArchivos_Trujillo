namespace GestorArchivos.Sync.Windows.Models;

public sealed class SyncItem
{
    public Guid SyncItemId { get; set; }
    public string RutaLocal { get; set; } = string.Empty;
    public SyncItemType Tipo { get; set; }
    public Guid? CarpetaRemotaId { get; set; }
    public Guid? ArchivoRemotoId { get; set; }
    public DateTime FechaCreacion { get; set; }
    public DateTime? UltimaSincronizacionUtc { get; set; }
}