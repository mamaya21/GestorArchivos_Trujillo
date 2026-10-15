using GestorArchivos.Sync.Windows;
using GestorArchivos.Sync.Windows.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Gestor Archivos Sync";
});

builder.Services.AddGestorArchivosSync(builder.Configuration);
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();