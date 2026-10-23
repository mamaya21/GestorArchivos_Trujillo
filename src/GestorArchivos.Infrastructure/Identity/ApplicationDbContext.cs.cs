using System.Text.Json;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace GestorArchivos.Infrastructure.Identity;

public class ApplicationDbContext
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    private readonly ICurrentUserService? _currentUserService;

    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options,
        ICurrentUserService? currentUserService = null)
        : base(options)
    {
        _currentUserService = currentUserService;
    }

    public DbSet<Carpeta> Carpetas => Set<Carpeta>();

    public DbSet<Archivo> Archivos => Set<Archivo>();

    public DbSet<ArchivoVersion> ArchivoVersiones => Set<ArchivoVersion>();

    public DbSet<Grupo> Grupos => Set<Grupo>();

    public DbSet<GrupoUsuario> GruposUsuarios => Set<GrupoUsuario>();

    public DbSet<PermisoCarpetaUsuario> PermisosCarpetasUsuarios =>
        Set<PermisoCarpetaUsuario>();

    public DbSet<PermisoCarpetaGrupo> PermisosCarpetasGrupos =>
        Set<PermisoCarpetaGrupo>();

    public DbSet<Auditoria> Auditorias => Set<Auditoria>();

    public DbSet<DispositivoSync> DispositivosSync => Set<DispositivoSync>();

    public DbSet<ElementoSync> ElementosSync => Set<ElementoSync>();

    public DbSet<BloqueoArchivo> BloqueosArchivos => Set<BloqueoArchivo>();

    public DbSet<CredencialDispositivoSync> CredencialesDispositivosSync => Set<CredencialDispositivoSync>();

    public DbSet<CambioSync> CambiosSync => Set<CambioSync>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ArchivoVersion>(entity =>
        {
            entity.ToTable("ArchivoVersiones");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NombreFisico).HasMaxLength(255).IsRequired();
            entity.Property(x => x.HashSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TipoContenido).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => new { x.ArchivoId, x.Numero }).IsUnique();
        });

        builder.ApplyConfigurationsFromAssembly(
            typeof(ApplicationDbContext).Assembly);
    }

    public override int SaveChanges()
    {
        AddAuditEntries();
        return base.SaveChanges();
    }

    public override int SaveChanges(
        bool acceptAllChangesOnSuccess)
    {
        AddAuditEntries();

        return base.SaveChanges(
            acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        AddAuditEntries();

        return base.SaveChangesAsync(
            cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        AddAuditEntries();

        return base.SaveChangesAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
    }

    private void AddAuditEntries()
    {
        ChangeTracker.DetectChanges();

        var entries = ChangeTracker
            .Entries()
            .Where(x =>
                x.Entity is not Auditoria &&
                x.State is EntityState.Added or
                    EntityState.Modified or
                    EntityState.Deleted)
            .ToList();

        if (entries.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var userId = _currentUserService?.UserId;

        foreach (var entry in entries)
        {
            var audit = CreateAudit(
                entry,
                userId,
                now);

            if (audit is not null)
                Auditorias.Add(audit);
        }
    }

    private static Auditoria? CreateAudit(
        EntityEntry entry,
        Guid? userId,
        DateTime now)
    {
        var entityName =
            entry.Metadata.ClrType.Name;

        var keyValues = entry.Properties
            .Where(x => x.Metadata.IsPrimaryKey())
            .Select(x =>
                x.CurrentValue?.ToString() ??
                x.OriginalValue?.ToString() ??
                string.Empty)
            .ToArray();

        var entityId =
            string.Join("|", keyValues);

        var action = entry.State switch
        {
            EntityState.Added => "CREATE",
            EntityState.Modified => "UPDATE",
            EntityState.Deleted => "DELETE",
            _ => null
        };

        if (action is null)
            return null;

        string? oldValues = null;
        string? newValues = null;

        if (entry.State == EntityState.Added)
        {
            newValues = SerializeValues(
                entry,
                useOriginalValues: false,
                onlyModified: false);
        }
        else if (entry.State == EntityState.Deleted)
        {
            oldValues = SerializeValues(
                entry,
                useOriginalValues: true,
                onlyModified: false);
        }
        else
        {
            oldValues = SerializeValues(
                entry,
                useOriginalValues: true,
                onlyModified: true);

            newValues = SerializeValues(
                entry,
                useOriginalValues: false,
                onlyModified: true);
        }

        return new Auditoria
        {
            UsuarioId = userId,
            Accion = action,
            Entidad = entityName,
            EntidadId = entityId,
            DatosAnteriores = oldValues,
            DatosNuevos = newValues,
            FechaUtc = now
        };
    }

    private static string? SerializeValues(
        EntityEntry entry,
        bool useOriginalValues,
        bool onlyModified)
    {
        var values =
            new Dictionary<string, object?>();

        foreach (var property in entry.Properties)
        {
            if (onlyModified &&
                !property.IsModified)
            {
                continue;
            }

            if (ShouldExcludeProperty(
                    property.Metadata.Name))
            {
                continue;
            }

            values[property.Metadata.Name] =
                useOriginalValues
                    ? property.OriginalValue
                    : property.CurrentValue;
        }

        return values.Count == 0
            ? null
            : JsonSerializer.Serialize(values);
    }

    private static bool ShouldExcludeProperty(
        string propertyName)
    {
        return propertyName.Equals(
                   "PasswordHash",
                   StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals(
                   "SecurityStamp",
                   StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals(
                   "ConcurrencyStamp",
                   StringComparison.OrdinalIgnoreCase) ||
               propertyName.Equals(
                   "AuthenticatorKey",
                   StringComparison.OrdinalIgnoreCase);
    }
}