using BusinessSuite.Application.Common.Security;
using BusinessSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>
/// Moves the remaining ASP.NET Core Identity tables into the <c>identity</c> schema with
/// short names (<c>identity.Roles</c>, <c>identity.UserRoles</c>, ...).
/// </summary>
/// <remarks>
/// One class implementing several <see cref="IEntityTypeConfiguration{TEntity}"/> interfaces
/// keeps all small Identity mappings in one place. Business entities get one class each.
/// </remarks>
internal sealed class IdentityTablesConfiguration :
    IEntityTypeConfiguration<ApplicationRole>,
    IEntityTypeConfiguration<IdentityUserRole<Guid>>,
    IEntityTypeConfiguration<IdentityUserClaim<Guid>>,
    IEntityTypeConfiguration<IdentityUserLogin<Guid>>,
    IEntityTypeConfiguration<IdentityUserToken<Guid>>,
    IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    /// <summary>Fixed ids of the seeded platform roles (never change them: migrations depend on them).</summary>
    internal static readonly Guid SuperAdministratorRoleId = Guid.Parse("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4a01");

    internal static readonly Guid PlatformAdministratorRoleId = Guid.Parse("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4a02");

    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.ToTable("Roles", DatabaseSchemas.Identity);
        builder.Property(role => role.Description).HasMaxLength(ApplicationRole.DescriptionMaxLength);

        // Platform roles exist in every environment (created by migration), so granting one is a single insert
        // into identity.UserRoles. Static concurrency stamps keep the migration deterministic.
        builder.HasData(
            new ApplicationRole
            {
                Id = SuperAdministratorRoleId,
                Name = PlatformRoles.SuperAdministrator,
                NormalizedName = PlatformRoles.SuperAdministrator.ToUpperInvariant(),
                ConcurrencyStamp = "2c7b0f1e-super-admin",
                Description = "Super Admin: every permission in every organization and on the platform.",
            },
            new ApplicationRole
            {
                Id = PlatformAdministratorRoleId,
                Name = PlatformRoles.Administrator,
                NormalizedName = PlatformRoles.Administrator.ToUpperInvariant(),
                ConcurrencyStamp = "2c7b0f1e-platform-admin",
                Description = "Platform administration: organizations, users and subscriptions; no access inside organizations.",
            });
    }

    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder) =>
        builder.ToTable("UserRoles", DatabaseSchemas.Identity);

    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder) =>
        builder.ToTable("UserClaims", DatabaseSchemas.Identity);

    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder) =>
        builder.ToTable("UserLogins", DatabaseSchemas.Identity);

    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder) =>
        builder.ToTable("UserTokens", DatabaseSchemas.Identity);

    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder) =>
        builder.ToTable("RoleClaims", DatabaseSchemas.Identity);
}
