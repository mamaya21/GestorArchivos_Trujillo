using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations;

public class DispositivoSyncConfiguration
    : IEntityTypeConfiguration<DispositivoSync>
{
    public void Configure(EntityTypeBuilder<DispositivoSync> builder)
    {
        builder.ToTable("DispositivosSync");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Nombre)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(x => x.SistemaOperativo)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasOne(x => x.CarpetaRaiz)
            .WithMany()
            .HasForeignKey(x => x.CarpetaRaizId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.UsuarioId);

        builder.HasIndex(x => x.CarpetaRaizId)
            .IsUnique();
    }
}