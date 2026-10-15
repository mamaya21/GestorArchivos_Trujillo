using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations;

public class BloqueoArchivoConfiguration
    : IEntityTypeConfiguration<BloqueoArchivo>
{
    public void Configure(EntityTypeBuilder<BloqueoArchivo> builder)
    {
        builder.ToTable("BloqueosArchivos");

        builder.HasKey(x => x.Id);

        builder.HasOne(x => x.Archivo)
            .WithMany()
            .HasForeignKey(x => x.ArchivoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Dispositivo)
            .WithMany()
            .HasForeignKey(x => x.DispositivoId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.ArchivoId)
            .HasFilter("[Activo] = 1")
            .IsUnique();

        builder.HasIndex(x => x.UsuarioId);

        builder.HasIndex(x => x.DispositivoId);
    }
}