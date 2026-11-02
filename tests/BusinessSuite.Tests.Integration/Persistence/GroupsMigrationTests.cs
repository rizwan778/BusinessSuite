using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BusinessSuite.Tests.Integration.Persistence;

/// <summary>
/// The <c>AddGroups</c> migration with data in the database: departments typed before groups existed become groups,
/// members keep their department, and rolling back loses nothing but the groups.
/// </summary>
public sealed class GroupsMigrationTests
{
    private const string PreviousMigration = "AddRolesAndPermissions";

    [SqlServerFact]
    public async Task Existing_departments_become_groups_and_rolling_back_keeps_every_membership()
    {
        var connection = new SqlConnectionStringBuilder(TestSettings.SqlServerConnectionString)
        {
            InitialCatalog = SqlServerTestDatabase.DatabaseNamePrefix + Guid.NewGuid().ToString("N")[..12],
        }.ConnectionString;

        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        try
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            // Old schema, old data: two organizations; "Sales" typed in two letter cases, "Warehouse", and no department.
            var shop = Guid.NewGuid();
            var other = Guid.NewGuid();
            await InsertOrganizationAsync(context, shop);
            await InsertOrganizationAsync(context, other);
            var first = await InsertMemberAsync(context, shop, "Sales");
            await InsertMemberAsync(context, shop, "sales");
            await InsertMemberAsync(context, shop, "Warehouse");
            await InsertMemberAsync(context, shop, null);
            await InsertMemberAsync(context, other, "Sales");

            await migrator.MigrateAsync();

            var groups = await context.Database
                .SqlQuery<GroupRow>($"SELECT [OrganizationId], [Name] FROM [platform].[Groups]")
                .ToListAsync();
            Assert.Equal(3, groups.Count);
            Assert.Equal(
                new[] { "sales", "warehouse" },
                groups.Where(group => group.OrganizationId == shop).Select(group => group.Name.ToLowerInvariant()).Order());
            Assert.Equal(new[] { "sales" }, groups.Where(group => group.OrganizationId == other).Select(group => group.Name.ToLowerInvariant()));
            Assert.Equal(5, await CountMembershipsAsync(context));

            // Members still match their group by department text (letter case does not matter to the database).
            var salesMembers = await context.Database
                .SqlQuery<int>($"""
                    SELECT COUNT(*) AS [Value] FROM [platform].[OrganizationMemberships] AS m
                    INNER JOIN [platform].[Groups] AS g ON g.[OrganizationId] = m.[OrganizationId] AND g.[Name] = m.[Department]
                    WHERE m.[OrganizationId] = {shop} AND g.[Name] = N'Sales'
                    """)
                .SingleAsync();
            Assert.Equal(2, salesMembers);
            Assert.NotEqual(Guid.Empty, first);

            // Roll back: the groups go, every membership and department stays.
            await migrator.MigrateAsync(PreviousMigration);

            Assert.Equal(0, await context.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE [name] = N'Groups'")
                .SingleAsync());
            Assert.Equal(5, await CountMembershipsAsync(context));
            Assert.Equal(
                new[] { "Sales", "Sales", "Warehouse", "sales" }.Order(StringComparer.Ordinal),
                (await context.Database
                    .SqlQuery<string>($"SELECT [Department] AS [Value] FROM [platform].[OrganizationMemberships] WHERE [Department] IS NOT NULL")
                    .ToListAsync()).Order(StringComparer.Ordinal));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static Task<int> InsertOrganizationAsync(AppDbContext context, Guid id) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [platform].[Organizations] ([Id], [Name], [IsActive], [CreatedAtUtc])
            VALUES ({id}, {$"Migration Test {id:N}"}, 1, SYSUTCDATETIME())
            """);

    /// <summary>A user (the minimum Identity needs) with a membership in <paramref name="organizationId"/>; returns the user's id.</summary>
    private static async Task<Guid> InsertMemberAsync(AppDbContext context, Guid organizationId, string? department)
    {
        var userId = Guid.NewGuid();
        var name = $"member-{userId:N}@example.com";
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [identity].[Users] ([Id], [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [IsActive], [CreatedAtUtc],
                [EmailConfirmed], [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
            VALUES ({userId}, {name}, {name.ToUpperInvariant()}, {name}, {name.ToUpperInvariant()}, 1, SYSUTCDATETIME(), 1, 0, 0, 1, 0)
            """);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [platform].[OrganizationMemberships] ([Id], [OrganizationId], [UserId], [RoleId], [Department], [IsActive], [CreatedAtUtc])
            VALUES ({Guid.NewGuid()}, {organizationId}, {userId}, {OrganizationRoles.OwnerId}, {department}, 1, SYSUTCDATETIME())
            """);
        return userId;
    }

    private static Task<int> CountMembershipsAsync(AppDbContext context) =>
        context.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [platform].[OrganizationMemberships]").SingleAsync();

    private sealed record GroupRow(Guid OrganizationId, string Name);
}
