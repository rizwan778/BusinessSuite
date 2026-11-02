using BusinessSuite.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Organization"/> to <c>platform.Organizations</c>.</summary>
internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations", DatabaseSchemas.Platform);

        builder.Property(organization => organization.Name)
            .HasMaxLength(Organization.NameMaxLength)
            .IsRequired();

        builder.Property(organization => organization.IsActive)
            .IsRequired();
    }
}
