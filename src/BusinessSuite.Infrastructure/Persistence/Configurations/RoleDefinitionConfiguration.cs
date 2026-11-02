using BusinessSuite.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="RoleDefinition"/> to <c>platform.RoleDefinitions</c>.</summary>
/// <remarks>
/// The system roles are inserted by the migration <c>AddRolesAndPermissions</c> with the fixed ids of
/// <see cref="OrganizationRoles.Ids"/> (not with <c>HasData</c>: their permissions are edited at runtime, and seed data
/// would be re-applied by later migrations).
/// </remarks>
internal sealed class RoleDefinitionConfiguration : IEntityTypeConfiguration<RoleDefinition>
{
    public void Configure(EntityTypeBuilder<RoleDefinition> builder)
    {
        builder.ToTable("RoleDefinitions", DatabaseSchemas.Platform, table =>
            table.HasCheckConstraint("CK_RoleDefinitions_SystemRolesArePlatformRoles", "[SystemKey] IS NULL OR [OrganizationId] IS NULL"));

        builder.Property(role => role.Name)
            .HasMaxLength(RoleDefinition.NameMaxLength)
            .IsRequired();

        builder.Property(role => role.Description)
            .HasMaxLength(RoleDefinition.DescriptionMaxLength);

        builder.Property(role => role.SystemKey)
            .HasMaxLength(OrganizationRoles.MaxLength);

        // Stored as a JSON array of permission names, e.g. ["inventory.view","users.manage"].
        builder.PrimitiveCollection(role => role.Permissions)
            .HasField("_permissions")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .IsRequired();

        // One name per organization, and (OrganizationId NULL counts as one value) one name among platform roles.
        // No filter: SQL Server's default would exclude the NULL rows and let platform roles share a name.
        builder.HasIndex(role => new { role.OrganizationId, role.Name })
            .IsUnique()
            .HasFilter(null);

        builder.HasIndex(role => role.SystemKey)
            .IsUnique()
            .HasFilter("[SystemKey] IS NOT NULL");

        // Organizations are suspended, never deleted; no cascade (it would also create two cascade paths to RoleAvailability).
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(role => role.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
