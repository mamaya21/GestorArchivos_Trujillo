using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations;

public class CarpetaConfiguration : IEntityTypeConfiguration<Carpeta>
{
    public void Configure(EntityTypeBuilder<Carpeta> builder)
    {
        builder.ToTable("Carpetas");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Nombre)
            .HasMaxLength(255)
            .IsRequired();

        builder.HasOne(x => x.CarpetaPadre)
            .WithMany(x => x.Subcarpetas)
            .HasForeignKey(x => x.CarpetaPadreId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.CarpetaPadreId);
        builder.HasIndex(x => x.CreadoPorUsuarioId);
        builder.HasIndex(x => new
            {
                x.CarpetaPadreId,
                x.Nombre
            })
        .IsUnique()
        .HasFilter("[EnPapelera] = 0");
    }
}