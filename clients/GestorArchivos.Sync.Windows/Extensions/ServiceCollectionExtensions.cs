
using GestorArchivos.Sync.Windows.Api;
using GestorArchivos.Sync.Windows.Configuration;
using GestorArchivos.Sync.Windows.Infrastructure;
using GestorArchivos.Sync.Windows.Security;
using GestorArchivos.Sync.Windows.State;
using GestorArchivos.Sync.Windows.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using GestorArchivos.Sync.Windows.Monitoring;
using GestorArchivos.Sync.Windows.Services;
using GestorArchivos.Sync.Windows.Sync;
using GestorArchivos.Sync.Windows.Health;
using GestorArchivos.Sync.Windows.Logging;
using GestorArchivos.Sync.Windows.Activation;

namespace GestorArchivos.Sync.Windows.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGestorArchivosSync(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<SyncOptions>()
            .Bind(configuration.GetSection(SyncOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<SyncOptions>, SyncOptionsValidator>();

        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<ILocalStateStore, LocalStateStore>();
        services.AddSingleton<ISyncBaselineStore, SyncBaselineStore>();
        services.AddSingleton<ILocalFileStore, LocalFileStore>();
        services.AddSingleton<SyncRuntimeState>();
        services.AddSingleton<SyncCancellationCoordinator>();
        services.AddSingleton<SyncBaselineService>();
        services.AddSingleton<SelectiveSyncMappingService>();
        services.AddSingleton<SelectiveSyncService>();
        services.AddSingleton<RetryPolicy>();
        services.AddSingleton<FileSystemEventBuffer>();
        services.AddSingleton<ILocalChangeMonitor, LocalChangeMonitor>();
        services.AddSingleton<IConflictService, ConflictService>();
        services.AddSingleton<IDeviceRegistrationService, DeviceRegistrationService>();
        services.AddSingleton<SyncHealthService>();
        services.AddSingleton<SyncLog>();
        services.AddSingleton<ChangePullService>();
        services.AddSingleton<InitialSyncService>();
        services.AddSingleton<ReconciliationService>();
        services.AddSingleton<ISyncEngine, SyncEngine>();
        services.AddSingleton<IActivationService, ActivationService>();

        services.AddTransient<SyncAuthenticationHandler>();

        services.AddHttpClient<ISyncApiClient, SyncApiClient>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<SyncOptions>>()
                .Value;

            client.BaseAddress = new Uri(
                options.ApiBaseUrl.TrimEnd('/') + "/");

            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SyncAuthenticationHandler>();

        services.AddHttpClient("SyncConnectivity", (serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<SyncOptions>>()
                .Value;

            client.BaseAddress = new Uri(
                options.ApiBaseUrl.TrimEnd('/') + "/");

            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient<IDownloadService, DownloadService>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<SyncOptions>>()
                .Value;

            client.BaseAddress = new Uri(
                options.ApiBaseUrl.TrimEnd('/') + "/");

            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddHttpMessageHandler<SyncAuthenticationHandler>();

        services.AddHttpClient<IUploadService, UploadService>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<SyncOptions>>()
                .Value;

            client.BaseAddress = new Uri(
                options.ApiBaseUrl.TrimEnd('/') + "/");

            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddHttpMessageHandler<SyncAuthenticationHandler>();

        services.AddHttpClient<FileLockService>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<SyncOptions>>()
                .Value;

            client.BaseAddress = new Uri(
                options.ApiBaseUrl.TrimEnd('/') + "/");

            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SyncAuthenticationHandler>();

        services.AddHttpClient<ChangePushService>((serviceProvider, client) =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<SyncOptions>>()
                .Value;

            client.BaseAddress = new Uri(
                options.ApiBaseUrl.TrimEnd('/') + "/");

            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddHttpMessageHandler<SyncAuthenticationHandler>();

        services.AddSingleton<ConnectivityService>();

        services.AddHostedService<ActivationListenerService>();

        return services;
    }
}
