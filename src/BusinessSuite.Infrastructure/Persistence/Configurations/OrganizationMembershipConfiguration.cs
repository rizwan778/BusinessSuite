using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="OrganizationMembership"/> to <c>platform.OrganizationMemberships</c>.</summary>
internal sealed class OrganizationMembershipConfiguration : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> builder)
    {
        builder.ToTable("OrganizationMemberships", DatabaseSchemas.Platform);

        builder.Property(membership => membership.Department)
            .HasMaxLength(OrganizationMembership.DepartmentMaxLength);

        builder.Property(membership => membership.IsActive)
            .IsRequired();

        // One membership per person per organization.
        builder.HasIndex(membership => new { membership.OrganizationId, membership.UserId }).IsUnique();

        // "Which organizations does this user belong to?" runs on every authenticated request.
        builder.HasIndex(membership => membership.UserId);

        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A role that is held cannot be deleted (the service checks first; this is the guarantee).
        builder.HasOne<RoleDefinition>()
            .WithMany()
            .HasForeignKey(membership => membership.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
