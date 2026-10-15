using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace GestorArchivos.Infrastructure.Identity.Seed;

public static class IdentitySeeder
{
    public const string AdministratorRole = "Administrador";
    public const string UserRole = "Usuario";

    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();

        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();

        var roleManager = scope.ServiceProvider
            .GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var roleName in new[]
                 {
                     AdministratorRole,
                     UserRole
                 })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var roleResult = await roleManager.CreateAsync(
                    new IdentityRole<Guid>(roleName));

                if (!roleResult.Succeeded)
                    throw BuildException("crear rol", roleResult.Errors);
            }
        }

        const string userName = "admin";

        var admin = await userManager.FindByNameAsync(userName);

        if (admin is null)
        {
            admin = new ApplicationUser
            {
                UserName = userName,
                Email = "admin@gestorarchivos.local",
                Nombre = "Administrador",
                Apellido = "Sistema",
                Activo = true,
                EmailConfirmed = true
            };

            var password = Environment.GetEnvironmentVariable(
                "GESTOR_ADMIN_INITIAL_PASSWORD");

            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "No se ha definido GESTOR_ADMIN_INITIAL_PASSWORD.");
            }

            var createResult = await userManager.CreateAsync(
                admin,
                password);

            if (!createResult.Succeeded)
                throw BuildException(
                    "crear administrador inicial",
                    createResult.Errors);
        }

        if (!await userManager.IsInRoleAsync(
                admin,
                AdministratorRole))
        {
            var roleResult = await userManager.AddToRoleAsync(
                admin,
                AdministratorRole);

            if (!roleResult.Succeeded)
            {
                throw BuildException(
                    "asignar rol Administrador",
                    roleResult.Errors);
            }
        }
    }

    private static InvalidOperationException BuildException(
        string operation,
        IEnumerable<IdentityError> errors)
    {
        var detail = string.Join(
            "; ",
            errors.Select(x => $"{x.Code}: {x.Description}"));

        return new InvalidOperationException(
            $"No se pudo {operation}: {detail}");
    }
}