using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations.Security;

public class PermisoCarpetaGrupoConfiguration
    : IEntityTypeConfiguration<PermisoCarpetaGrupo>
{
    public void Configure(EntityTypeBuilder<PermisoCarpetaGrupo> builder)
    {
        builder.ToTable("PermisosCarpetasGrupos");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Permisos)
            .HasConversion<int>();

        builder.HasOne(x => x.Carpeta)
            .WithMany()
            .HasForeignKey(x => x.CarpetaId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Grupo)
            .WithMany(x => x.Permisos)
            .HasForeignKey(x => x.GrupoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new
        {
            x.CarpetaId,
            x.GrupoId
        })
        .IsUnique();
    }
}