using GestorArchivos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GestorArchivos.Infrastructure.Persistence.Configurations.Security;

public class GrupoUsuarioConfiguration : IEntityTypeConfiguration<GrupoUsuario>
{
    public void Configure(EntityTypeBuilder<GrupoUsuario> builder)
    {
        builder.ToTable("GruposUsuarios");

        builder.HasKey(x => new
        {
            x.GrupoId,
            x.UsuarioId
        });

        builder.HasOne(x => x.Grupo)
            .WithMany(x => x.Usuarios)
            .HasForeignKey(x => x.GrupoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.UsuarioId);
    }
}