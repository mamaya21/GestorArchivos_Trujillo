
using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Identity.Configurations;

public sealed class ArchivoFisicoPendienteEliminacionConfiguration
    : IEntityTypeConfiguration<ArchivoFisicoPendienteEliminacion>
{
    public void Configure(
        EntityTypeBuilder<ArchivoFisicoPendienteEliminacion> builder)
    {
        builder.ToTable("ArchivosFisicosPendientesEliminacion");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.RutaFisica)
            .IsRequired()
            .HasMaxLength(1024);

        builder.HasIndex(x => x.RutaFisica)
            .IsUnique();

        builder.HasIndex(x => x.EliminarDespuesUtc);

        builder.Property(x => x.UltimoError)
            .HasMaxLength(2000);
    }
}
