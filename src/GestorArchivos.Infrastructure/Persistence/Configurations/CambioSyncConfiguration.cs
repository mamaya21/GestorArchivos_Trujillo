using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations;

public class CambioSyncConfiguration
    : IEntityTypeConfiguration<CambioSync>
{
    public void Configure(EntityTypeBuilder<CambioSync> builder)
    {
        builder.ToTable("CambiosSync");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .ValueGeneratedOnAdd();

        builder.Property(x => x.TipoCambio)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(x => new
        {
            x.CarpetaRaizId,
            x.Id
        });

        builder.HasIndex(x => x.ArchivoId);

        builder.HasIndex(x => x.CarpetaId);

        builder.HasIndex(x => x.DispositivoOrigenId);
    }
}