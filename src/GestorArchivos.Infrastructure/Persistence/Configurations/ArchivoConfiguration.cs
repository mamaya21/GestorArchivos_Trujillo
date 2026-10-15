using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations;

public class ArchivoConfiguration : IEntityTypeConfiguration<Archivo>
{
    public void Configure(EntityTypeBuilder<Archivo> builder)
    {
        builder.ToTable("Archivos");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Nombre)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.NombreFisico)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.Extension)
            .HasMaxLength(50);

        builder.Property(x => x.TipoContenido)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.HashSha256)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(x => x.Version)
            .IsConcurrencyToken();

        builder.Property(x => x.Estado)
            .HasConversion<int>();

        builder.HasOne(x => x.Carpeta)
            .WithMany(x => x.Archivos)
            .HasForeignKey(x => x.CarpetaId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.CarpetaId);
        builder.HasIndex(x => x.CreadoPorUsuarioId);
        builder.HasIndex(x => x.HashSha256);
        builder.HasIndex(x => new
            {
                x.CarpetaId,
                x.Nombre
            })
        .IsUnique()
        .HasFilter("[Estado] = 1");
    }
}