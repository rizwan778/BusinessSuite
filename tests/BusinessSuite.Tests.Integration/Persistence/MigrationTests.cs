using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Tests.Integration.Persistence;

/// <summary>
/// Keeps the EF Core model and the committed migrations in sync.
/// </summary>
public sealed class MigrationTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    /// <summary>
    /// Fails when an entity or configuration changed but no migration was added.
    /// Needs no database: EF Core compares the model with the migration snapshot.
    /// </summary>
    [Fact]
    public void Model_has_no_pending_changes()
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(BusinessSuiteWebApplicationFactory.UnusedConnectionString)
            .Options);

        Assert.True(context.Database.GetMigrations().Any(), DatabaseInitializer.MissingMigrationsMessage);
        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The EF Core model changed but no migration was added. Run: ./scripts/db.ps1 add <DescriptiveName> " +
            "(or ./scripts/db.sh add <DescriptiveName>), review the generated file and commit it.");
    }

    [SqlServerFact]
    public async Task All_migrations_apply_to_an_empty_database()
    {
        await using var context = database.CreateDbContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        var pending = await context.Database.GetPendingMigrationsAsync();

        Assert.Empty(pending);
        Assert.Equal(context.Database.GetMigrations(), applied);
        Assert.Equal(0, await context.Users.CountAsync());
    }
}
