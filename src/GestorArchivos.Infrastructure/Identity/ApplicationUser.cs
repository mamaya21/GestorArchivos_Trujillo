using Microsoft.AspNetCore.Identity;

namespace GestorArchivos.Infrastructure.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public string Nombre { get; set; } = string.Empty;
    public string Apellido { get; set; } = string.Empty;
    public bool Activo { get; set; } = true;
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    public DateTime? UltimoAcceso { get; set; }
}