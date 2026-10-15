
using GestorArchivos.Api.Services;
using GestorArchivos.Domain.Entities;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GestorArchivos.IntegrationTests;

public sealed class OrphanFileCleanupTests
    : IClassFixture<GestorArchivosWebApplicationFactory>
{
    private readonly GestorArchivosWebApplicationFactory _factory;

    public OrphanFileCleanupTests(
        GestorArchivosWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Archivo_No_Vencido_No_Debe_Eliminarse()
    {
        await ExecuteScenarioAsync(
            expired: false,
            expectedFileExists: true,
            expectedQueueExists: true);
    }

    [Fact]
    public async Task Archivo_Vencido_Sin_Referencia_Debe_Eliminarse()
    {
        await ExecuteScenarioAsync(
            expired: true,
            expectedFileExists: false,
            expectedQueueExists: false);
    }

    [Fact]
    public async Task Archivo_Abierto_No_Debe_Eliminarse()
    {
        using var scope = _factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        var service = scope.ServiceProvider
            .GetRequiredService<OrphanFileCleanupService>();

        var relativePath = Path.Combine(
            "cleanup-tests",
            $"{Guid.NewGuid():N}.bin");

        var fullPath = Path.Combine(
            _factory.StorageRoot,
            relativePath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)!);

        await File.WriteAllTextAsync(fullPath, "Contenido original");

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
            await using (var reader = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                await service.CleanupAsync(CancellationToken.None);

                Assert.True(File.Exists(fullPath));

                Assert.True(await db
                    .Set<ArchivoFisicoPendienteEliminacion>()
                    .AsNoTracking()
                    .AnyAsync(x => x.Id == entry.Id));
            }

            // El primer intento debe haber programado un reintento.
            // Para comprobarlo inmediatamente se adelanta SOLO
            // la fecha de esta entrada de prueba.
            db.ChangeTracker.Clear();

            var pending = await db
                .Set<ArchivoFisicoPendienteEliminacion>()
                .FindAsync(entry.Id);

            Assert.NotNull(pending);

            pending!.EliminarDespuesUtc =
                DateTime.UtcNow.AddMinutes(-1);

            await db.SaveChangesAsync();

            await service.CleanupAsync(CancellationToken.None);

            Assert.False(File.Exists(fullPath));

            Assert.False(await db
                .Set<ArchivoFisicoPendienteEliminacion>()
                .AsNoTracking()
                .AnyAsync(x => x.Id == entry.Id));
        }
        finally
        {
            await RemoveTestEntryAsync(db, entry.Id);

            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
    }

    private async Task ExecuteScenarioAsync(
        bool expired,
        bool expectedFileExists,
        bool expectedQueueExists)
    {
        using var scope = _factory.Services.CreateScope();

        var db = scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        var service = scope.ServiceProvider
            .GetRequiredService<OrphanFileCleanupService>();

        var relativePath = Path.Combine(
            "cleanup-tests",
            $"{Guid.NewGuid():N}.bin");

        var fullPath = Path.Combine(
            _factory.StorageRoot,
            relativePath);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)!);

        await File.WriteAllTextAsync(fullPath, "Archivo de prueba");

        var entry = new ArchivoFisicoPendienteEliminacion
        {
            Id = Guid.NewGuid(),
            RutaFisica = relativePath,
            FechaRegistroUtc = DateTime.UtcNow.AddDays(-8),
            EliminarDespuesUtc = expired
                ? DateTime.UtcNow.AddMinutes(-1)
                : DateTime.UtcNow.AddDays(7)
        };

        db.Set<ArchivoFisicoPendienteEliminacion>().Add(entry);
        await db.SaveChangesAsync();

        try
        {
            await service.CleanupAsync(CancellationToken.None);

            Assert.Equal(
                expectedFileExists,
                File.Exists(fullPath));

            var queueExists = await db
                .Set<ArchivoFisicoPendienteEliminacion>()
                .AsNoTracking()
                .AnyAsync(x => x.Id == entry.Id);

            Assert.Equal(
                expectedQueueExists,
                queueExists);
        }
        finally
        {
            await RemoveTestEntryAsync(db, entry.Id);

            if (File.Exists(fullPath))
                File.Delete(fullPath);
        }
    }

    private static async Task RemoveTestEntryAsync(
        ApplicationDbContext db,
        Guid id)
    {
        db.ChangeTracker.Clear();

        var entry = await db
            .Set<ArchivoFisicoPendienteEliminacion>()
            .FindAsync(id);

        if (entry is null)
            return;

        db.Remove(entry);
        await db.SaveChangesAsync();
    }
}
