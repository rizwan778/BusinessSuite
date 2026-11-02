using BusinessSuite.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="ApplicationUser"/> to <c>identity.Users</c>.
/// Identity already configures its standard columns; only BusinessSuite additions are set here.
/// </summary>
internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("Users", DatabaseSchemas.Identity);

        builder.Property(user => user.DisplayName)
            .HasMaxLength(ApplicationUser.DisplayNameMaxLength);

        builder.Property(user => user.IsActive)
            .IsRequired();

        builder.Property(user => user.CreatedAtUtc)
            .IsRequired();
    }
}
