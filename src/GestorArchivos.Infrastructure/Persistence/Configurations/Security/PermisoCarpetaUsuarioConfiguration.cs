using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations.Security;

public class PermisoCarpetaUsuarioConfiguration
    : IEntityTypeConfiguration<PermisoCarpetaUsuario>
{
    public void Configure(EntityTypeBuilder<PermisoCarpetaUsuario> builder)
    {
        builder.ToTable("PermisosCarpetasUsuarios");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Permisos)
            .HasConversion<int>();

        builder.HasOne(x => x.Carpeta)
            .WithMany()
            .HasForeignKey(x => x.CarpetaId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new
        {
            x.CarpetaId,
            x.UsuarioId
        })
        .IsUnique();

        builder.HasIndex(x => x.UsuarioId);
    }
}