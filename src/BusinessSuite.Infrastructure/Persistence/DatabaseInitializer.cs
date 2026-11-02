using BusinessSuite.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Infrastructure.Persistence;

/// <summary>
/// Prepares the DEVELOPMENT database at start-up: applies pending migrations and creates the
/// optional development user. Never runs in Production (see <c>Program.cs</c>).
/// </summary>
internal sealed class DatabaseInitializer(
    AppDbContext dbContext,
    DevelopmentUserSeeder developmentUserSeeder,
    IOptions<DatabaseOptions> options,
    IHostEnvironment environment,
    ILogger<DatabaseInitializer> logger)
{
    internal const string MissingMigrationsMessage =
        "No EF Core migrations exist yet, so the database schema cannot be created. " +
        "Create the initial migration once, from the repository root: " +
        "dotnet tool restore && dotnet ef migrations add InitialCreate --project src/BusinessSuite.Infrastructure " +
        "--startup-project src/BusinessSuite.Web --output-dir Persistence/Migrations " +
        "(or: ./scripts/db.ps1 add InitialCreate). Then commit the generated files. See docs/DATABASE_DESIGN.md.";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogWarning("Database initialization skipped: it only runs in Development (current: {Environment}).", environment.EnvironmentName);
            return;
        }

        if (!options.Value.ApplyMigrationsOnStartup)
        {
            logger.LogInformation("Automatic migrations are disabled (Database:ApplyMigrationsOnStartup = false).");
            return;
        }

        if (!dbContext.Database.GetMigrations().Any())
        {
            throw new InvalidOperationException(MissingMigrationsMessage);
        }

        var pending = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} pending migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            logger.LogInformation("Database schema is up to date.");
        }

        await developmentUserSeeder.SeedAsync(cancellationToken);
    }
}
