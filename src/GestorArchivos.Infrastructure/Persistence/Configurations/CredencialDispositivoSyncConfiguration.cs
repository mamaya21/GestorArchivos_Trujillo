using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations;

public class CredencialDispositivoSyncConfiguration
    : IEntityTypeConfiguration<CredencialDispositivoSync>
{
    public void Configure(EntityTypeBuilder<CredencialDispositivoSync> builder)
    {
        builder.ToTable("CredencialesDispositivosSync");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.HasOne(x => x.Dispositivo)
            .WithMany()
            .HasForeignKey(x => x.DispositivoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.TokenHash)
            .IsUnique();

        builder.HasIndex(x => x.DispositivoId);
    }
}