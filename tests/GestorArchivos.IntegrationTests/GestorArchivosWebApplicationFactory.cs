using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GestorArchivos.IntegrationTests;

public sealed class GestorArchivosWebApplicationFactory
    : WebApplicationFactory<Program>
{
    public static readonly string TestDatabaseName =
        $"GestorArchivosDb_IntegrationTests_{Guid.NewGuid():N}";

    public string StorageRoot { get; } =
        Path.Combine(
            Path.GetTempPath(),
            "GestorArchivos.IntegrationTests",
            Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(
        IWebHostBuilder builder)
    {
        // Instancia compartida, base de datos exclusiva con nombre aleatorio.
        if (Environment.GetEnvironmentVariable("GESTOR_TEST_ALLOW_SHARED_INSTANCE") != "1")
            throw new InvalidOperationException(
                "Para ejecutar pruebas en una base independiente, establezca GESTOR_TEST_ALLOW_SHARED_INSTANCE=1.");

        if (!TestDatabaseName.StartsWith("GestorArchivosDb_IntegrationTests_", StringComparison.Ordinal)
            || TestDatabaseName.Length <= "GestorArchivosDb_IntegrationTests_".Length)
            throw new InvalidOperationException("Nombre de base de pruebas no seguro.");

        builder.UseEnvironment("Testing");

        Environment.SetEnvironmentVariable(
        "GESTOR_ADMIN_INITIAL_PASSWORD",
        "IntegrationTest123!");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Storage:RootPath"] = StorageRoot
                });
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                x => x.ServiceType ==
                     typeof(DbContextOptions<ApplicationDbContext>));

            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer(
                    $@"Server=.\SQLEXPRESS;Database={TestDatabaseName};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"));
        });

        Directory.CreateDirectory(StorageRoot);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(StorageRoot))
            Directory.Delete(StorageRoot, true);

        base.Dispose(disposing);
    }
}