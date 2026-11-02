using BusinessSuite.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="RoleAvailability"/> to <c>platform.RoleAvailability</c>.</summary>
internal sealed class RoleAvailabilityConfiguration : IEntityTypeConfiguration<RoleAvailability>
{
    public void Configure(EntityTypeBuilder<RoleAvailability> builder)
    {
        builder.ToTable("RoleAvailability", DatabaseSchemas.Platform);

        builder.HasKey(availability => new { availability.RoleId, availability.OrganizationId });

        // "Which platform roles may this organization use?" runs when roles are listed or assigned.
        builder.HasIndex(availability => availability.OrganizationId);

        builder.HasOne<RoleDefinition>()
            .WithMany()
            .HasForeignKey(availability => availability.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(availability => availability.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
