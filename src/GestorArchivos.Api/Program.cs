
using GestorArchivos.Infrastructure;
using GestorArchivos.Api.Services;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Infrastructure.Identity.Seed;
using GestorArchivos.Api.Security;
using Microsoft.AspNetCore.Authentication;
using GestorArchivos.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, SyncDeviceAuthenticationHandler>(
        SyncDeviceAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Servicio de inspección de archivos huérfanos.
// No realiza eliminaciones físicas.
builder.Services.AddSingleton<OrphanFileCleanupService>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<OrphanFileCleanupService>());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Aplica migraciones pendientes antes de atender operaciones que usan nuevas tablas.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

await IdentitySeeder.SeedAsync(app.Services);

app.Run();

public partial class Program { }
