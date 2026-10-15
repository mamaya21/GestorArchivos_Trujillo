
using GestorArchivos.Api.Services;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GestorArchivos.IntegrationTests;

public sealed class OrphanFileCleanupSafetyTests
    : IClassFixture<GestorArchivosWebApplicationFactory>
{
    private readonly GestorArchivosWebApplicationFactory _factory;

    public OrphanFileCleanupSafetyTests(
        GestorArchivosWebApplicationFactory factory)
    {
        _factory = factory;
    }


    [Fact]
    public async Task Archivo_Referenciado_No_Debe_Eliminarse()
    {
        using var scope = _factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        var cleanup = scope.ServiceProvider
            .GetRequiredService<OrphanFileCleanupService>();

        var referencedFile = await db.Archivos
            .AsNoTracking()
            .Where(x => x.NombreFisico != null && x.NombreFisico != "")
            .OrderByDescending(x => x.FechaCreacion)
            .FirstOrDefaultAsync();

        Assert.NotNull(referencedFile);

        var relativePath = referencedFile!.NombreFisico;
        var fullPath = Path.Combine(_factory.StorageRoot, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var existedBefore = File.Exists(fullPath);

        if (!existedBefore)
            await File.WriteAllTextAsync(fullPath, "Contenido protegido");

        var entry = new ArchivoFisicoPendienteEliminacion
        {
            Id = Guid.NewGuid(),
            RutaFisica = relativePath,
            FechaRegistroUtc = DateTime.UtcNow.AddDays(-8),
            EliminarDespuesUtc = DateTime.UtcNow.AddMinutes(-1)
        };

        db.Set<ArchivoFisicoPendienteEliminacion>().Add(entry);
        await db.SaveChangesAsync();

        try
        {
            await cleanup.CleanupAsync(CancellationToken.None);

            Assert.True(File.Exists(fullPath));

            Assert.True(await db
                .Set<ArchivoFisicoPendienteEliminacion>()
                .AsNoTracking()
                .AnyAsync(x => x.Id == entry.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();

            var pending = await db
                .Set<ArchivoFisicoPendienteEliminacion>()
                .FindAsync(entry.Id);

            if (pending is not null)
            {
                db.Remove(pending);
                await db.SaveChangesAsync();
            }

            if (!existedBefore && File.Exists(fullPath))
                File.Delete(fullPath);
        }
    }


    [Fact]
    public async Task Ejecuciones_Concurrentes_No_Deben_Provocar_Errores()
    {
        using var scope = _factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        var cleanup = scope.ServiceProvider
            .GetRequiredService<OrphanFileCleanupService>();

        var relativePath = Path.Combine(
            "cleanup-tests",
            $"{Guid.NewGuid():N}.bin");

        var fullPath = Path.Combine(
            _factory.StorageRoot,
            relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "Contenido obsoleto");

        var pending = new ArchivoFisicoPendienteEliminacion
        {
            Id = Guid.NewGuid(),
            RutaFisica = relativePath,
            FechaRegistroUtc = DateTime.UtcNow.AddDays(-8),
            EliminarDespuesUtc = DateTime.UtcNow.AddMinutes(-1)
        };

        db.Set<ArchivoFisicoPendienteEliminacion>().Add(pending);
        await db.SaveChangesAsync();

        try
        {
            await Task.WhenAll(
                cleanup.CleanupAsync(CancellationToken.None),
                cleanup.CleanupAsync(CancellationToken.None));

            Assert.False(File.Exists(fullPath));

            Assert.False(await db
                .Set<ArchivoFisicoPendienteEliminacion>()
                .AsNoTracking()
                .AnyAsync(x => x.Id == pending.Id));
        }
        finally
        {
            db.ChangeTracker.Clear();

            var entry = await db
                .Set<ArchivoFisicoPendienteEliminacion>()
                .FindAsync(pending.Id);

            if (entry is not null)
            {
                db.Remove(entry);
                await db.SaveChangesAsync();
            }

            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
    }
}
