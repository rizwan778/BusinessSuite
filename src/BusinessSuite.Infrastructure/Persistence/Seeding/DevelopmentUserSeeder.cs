using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates the optional development account described by <see cref="DevelopmentSeedOptions"/>, a demo
/// organization it owns, and (if asked) its Super Admin role.
/// </summary>
/// <remarks>
/// Idempotent: each step is skipped when already done or not configured.
/// Refuses to run outside the Development environment (defence in depth).
/// </remarks>
internal sealed class DevelopmentUserSeeder(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    IOptions<DevelopmentSeedOptions> options,
    IHostEnvironment environment,
    ILogger<DevelopmentUserSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogWarning("Development user seeding skipped: environment is {Environment}.", environment.EnvironmentName);
            return;
        }

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.UserEmail) || string.IsNullOrWhiteSpace(settings.UserPassword))
        {
            logger.LogInformation(
                "No development user configured. To create one, set DevelopmentSeed:UserEmail and " +
                "DevelopmentSeed:UserPassword with dotnet user-secrets (see docs/CONFIGURATION.md).");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var user = await userManager.FindByEmailAsync(settings.UserEmail) ?? await CreateUserAsync(settings);

        if (settings.UserIsSuperAdmin && !await userManager.IsInRoleAsync(user, PlatformRoles.SuperAdministrator))
        {
            ThrowIfFailed(await userManager.AddToRoleAsync(user, PlatformRoles.SuperAdministrator), "made Super Admin");
            logger.LogWarning("Development user {UserId} is now a Super Admin (DevelopmentSeed:UserIsSuperAdmin).", user.Id);
        }

        if (!string.IsNullOrWhiteSpace(settings.OrganizationName)
            && !await db.OrganizationMemberships.AnyAsync(membership => membership.UserId == user.Id, cancellationToken))
        {
            var organization = Organization.Create(settings.OrganizationName);
            db.Organizations.Add(organization);
            await db.SaveChangesAsync(cancellationToken);

            db.OrganizationMemberships.Add(OrganizationMembership.Create(organization.Id, user.Id, OrganizationRoles.Owner));
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Development organization {OrganizationId} created; user {UserId} is its Owner.", organization.Id, user.Id);
        }
    }

    private async Task<ApplicationUser> CreateUserAsync(DevelopmentSeedOptions settings)
    {
        var user = new ApplicationUser
        {
            UserName = settings.UserEmail,
            Email = settings.UserEmail,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(settings.UserDisplayName) ? "Developer" : settings.UserDisplayName.Trim(),
        };

        ThrowIfFailed(await userManager.CreateAsync(user, settings.UserPassword!), "created");
        logger.LogInformation("Development user created (id {UserId}).", user.Id);
        return user;
    }

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            var reasons = string.Join(" ", result.Errors.Select(error => error.Description));
            throw new InvalidOperationException($"The development user could not be {action}: {reasons}");
        }
    }
}
