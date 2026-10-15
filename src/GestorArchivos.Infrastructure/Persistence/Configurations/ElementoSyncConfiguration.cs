using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations;

public class ElementoSyncConfiguration
    : IEntityTypeConfiguration<ElementoSync>
{
    public void Configure(EntityTypeBuilder<ElementoSync> builder)
    {
        builder.ToTable("ElementosSync");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.RutaLocal)
            .HasMaxLength(2048)
            .IsRequired();

        builder.Property(x => x.Tipo)
            .IsRequired();

        builder.HasOne(x => x.Dispositivo)
            .WithMany()
            .HasForeignKey(x => x.DispositivoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.CarpetaRemota)
            .WithMany()
            .HasForeignKey(x => x.CarpetaRemotaId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ArchivoRemoto)
            .WithMany()
            .HasForeignKey(x => x.ArchivoRemotoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.DispositivoId);

        builder.HasIndex(x => x.CarpetaRemotaId);

        builder.HasIndex(x => x.ArchivoRemotoId);

        // Permitir reutilizar rutas de asociaciones inactivas,
        // conservando su historial.
        builder.HasIndex(x => new
        {
            x.DispositivoId,
            x.RutaLocal
        })
        .IsUnique()
        .HasFilter("[Activo] = 1");
    }
}