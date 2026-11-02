using BusinessSuite.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BusinessSuite.Infrastructure.Persistence.Configurations;

/// <summary>Maps <see cref="UserSession"/> to <c>identity.UserSessions</c>.</summary>
internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("UserSessions", DatabaseSchemas.Identity);

        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();

        // Deleting a user removes their sessions.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Login purges a user's expired sessions.
        builder.HasIndex(session => new { session.UserId, session.ExpiresAtUtc });
    }
}
