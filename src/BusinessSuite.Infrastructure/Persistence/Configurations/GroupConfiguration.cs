using BusinessSuite.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="Group"/> to <c>platform.Groups</c>.</summary>
internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> builder)
    {
        builder.ToTable("Groups", DatabaseSchemas.Platform);

        builder.Property(group => group.Name)
            .HasMaxLength(Group.NameMaxLength)
            .IsRequired();

        builder.Property(group => group.Description)
            .HasMaxLength(Group.DescriptionMaxLength);

        // One name per organization (the guarantee behind the service's duplicate check and its concurrent-create race).
        builder.HasIndex(group => new { group.OrganizationId, group.Name }).IsUnique();

        // Organizations are suspended, never deleted.
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(group => group.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
