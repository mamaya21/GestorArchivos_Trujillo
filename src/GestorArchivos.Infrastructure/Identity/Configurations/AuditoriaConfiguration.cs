using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Identity.Configurations;

public sealed class AuditoriaConfiguration :
    IEntityTypeConfiguration<Auditoria>
{
    public void Configure(
        EntityTypeBuilder<Auditoria> builder)
    {
        builder.ToTable("Auditorias");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Accion)
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Entidad)
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(x => x.EntidadId)
            .HasMaxLength(450)
            .IsRequired();

        builder.Property(x => x.DatosAnteriores)
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.DatosNuevos)
            .HasColumnType("nvarchar(max)");

        builder.Property(x => x.FechaUtc)
            .IsRequired();

        builder.HasIndex(x => x.FechaUtc);

        builder.HasIndex(x => x.UsuarioId);

        builder.HasIndex(x => new
        {
            x.Entidad,
            x.EntidadId
        });
    }
}