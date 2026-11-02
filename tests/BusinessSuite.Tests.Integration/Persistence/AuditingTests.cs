using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Tests.Integration.Persistence;

/// <summary>
/// The auditing interceptor stamps created/updated values on real SQL Server rows.
/// </summary>
public sealed class AuditingTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 3, 2, 9, 30, 0, TimeSpan.Zero);

    [SqlServerFact]
    public async Task Created_and_updated_stamps_are_set_automatically()
    {
        var creator = new StubUser(Guid.NewGuid());
        var editor = new StubUser(Guid.NewGuid());
        var user = NewUser();

        await using (var context = database.CreateDbContext(creator, new StubClock(CreatedAt)))
        {
            context.Users.Add(user);
            await context.SaveChangesAsync();
        }

        await using (var context = database.CreateDbContext(editor, new StubClock(UpdatedAt)))
        {
            var stored = await context.Users.SingleAsync(candidate => candidate.Id == user.Id);
            stored.DisplayName = "Renamed";
            await context.SaveChangesAsync();
        }

        await using (var context = database.CreateDbContext())
        {
            var stored = await context.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == user.Id);

            Assert.Equal(CreatedAt, stored.CreatedAtUtc);
            Assert.Equal(creator.UserId, stored.CreatedByUserId);
            Assert.Equal(UpdatedAt, stored.UpdatedAtUtc);
            Assert.Equal(editor.UserId, stored.UpdatedByUserId);
        }
    }

    private static ApplicationUser NewUser()
    {
        var email = $"audit-{Guid.NewGuid():N}@example.com";
        return new ApplicationUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            DisplayName = "Audit Test",
        };
    }

    private sealed class StubUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => id;

        public string? UserName => "stub@example.com";

        public string? DisplayName => null;

        public Guid? OrganizationId => null;

        public string? OrganizationRole => null;

        public bool IsSuperAdmin => false;

        public bool HasPermission(string permission) => false;
    }

    private sealed class StubClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
