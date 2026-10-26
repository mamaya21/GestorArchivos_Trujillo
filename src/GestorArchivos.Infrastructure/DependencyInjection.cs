using GestorArchivos.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using GestorArchivos.Application.Common.Storage;
using GestorArchivos.Infrastructure.Storage;
using GestorArchivos.Application.Common.Security;
using GestorArchivos.Infrastructure.Security;
using GestorArchivos.Application.Common.Interfaces;
using GestorArchivos.Infrastructure.Services;

namespace GestorArchivos.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool allowInsecureDevelopmentCookies = false)
    {
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection")));

        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;

            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan =
                TimeSpan.FromMinutes(15);
            options.Lockout.AllowedForNewUsers = true;

            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddRoles<IdentityRole<Guid>>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            options.ValidationInterval = TimeSpan.Zero;
        });

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = "GestorArchivos.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = allowInsecureDevelopmentCookies
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;

                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode =
                        StatusCodes.Status401Unauthorized;

                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode =
                        StatusCodes.Status403Forbidden;

                    return Task.CompletedTask;
                };
            });

        services.Configure<StorageOptions>(
            configuration.GetSection(StorageOptions.SectionName));

        services.AddSingleton<IFileStorage, LocalFileStorage>();

        services.AddSingleton<UploadLockManager>();

        services.AddSingleton<IChunkUploadService, ChunkUploadService>();

        services.AddHostedService<AbandonedUploadCleanupService>();

        services.AddScoped<IFolderPermissionService, FolderPermissionService>();

        services.AddScoped<ISyncChangeService, SyncChangeService>();

        return services;
    }
}