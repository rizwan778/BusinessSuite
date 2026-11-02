using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Infrastructure.Persistence.Interceptors;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>Reads test settings from environment variables.</summary>
public static class TestSettings
{
    /// <summary>
    /// Environment variable holding a SQL Server connection string WITHOUT a database name, e.g.
    /// <c>Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true</c>.
    /// </summary>
    public const string SqlServerVariable = "BUSINESSSUITE_TEST_SQLSERVER";

    public static string? SqlServerConnectionString =>
        Environment.GetEnvironmentVariable(SqlServerVariable) is { Length: > 0 } value ? value : null;

    public static bool IsSqlServerConfigured => SqlServerConnectionString is not null;
}

/// <summary>
/// A test that needs a real SQL Server. Skipped (not failed) when
/// <see cref="TestSettings.SqlServerVariable"/> is not set, so <c>dotnet test</c> works anywhere.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!TestSettings.IsSqlServerConfigured)
        {
            Skip = $"SQL Server tests are disabled. Set the {TestSettings.SqlServerVariable} environment variable (see tests/README.md).";
        }
    }
}

/// <summary>
/// Creates a uniquely named, throw-away database, applies all migrations, and drops it afterwards.
/// </summary>
/// <remarks>
/// Safety: only databases whose name starts with <see cref="DatabaseNamePrefix"/> are ever dropped,
/// so pointing the tests at a real server can never delete real data.
/// The EF Core in-memory provider is deliberately NOT used: it does not behave like SQL Server
/// (no transactions, constraints, or SQL translation).
/// </remarks>
public sealed class SqlServerTestDatabase : IAsyncLifetime
{
    public const string DatabaseNamePrefix = "BusinessSuite_Test_";

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!TestSettings.IsSqlServerConfigured)
        {
            return;
        }

        var builder = new SqlConnectionStringBuilder(TestSettings.SqlServerConnectionString)
        {
            InitialCatalog = DatabaseNamePrefix + Guid.NewGuid().ToString("N")[..12],
        };
        ConnectionString = builder.ConnectionString;

        await using var context = CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            return;
        }

        var databaseName = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;
        if (!databaseName.StartsWith(DatabaseNamePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to drop database '{databaseName}': it was not created by the tests.");
        }

        await using var context = CreateDbContext();
        await context.Database.EnsureDeletedAsync();
    }

    /// <summary>Creates a context on the test database with the auditing interceptor attached.</summary>
    public AppDbContext CreateDbContext(ICurrentUser? currentUser = null, TimeProvider? timeProvider = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(new AuditableEntityInterceptor(currentUser ?? new SystemUser(), timeProvider ?? TimeProvider.System))
            .Options;

        return new AppDbContext(options);
    }

    private sealed class SystemUser : ICurrentUser
    {
        public bool IsAuthenticated => false;

        public Guid? UserId => null;

        public string? UserName => null;

        public string? DisplayName => null;

        public Guid? OrganizationId => null;

        public string? OrganizationRole => null;

        public bool IsSuperAdmin => false;

        public bool HasPermission(string permission) => false;
    }
}

/// <summary>
/// A throw-away SQL Server database plus a web application connected to it.
/// Use with <c>IClassFixture&lt;SqlServerAppFixture&gt;</c>; tests must be marked <see cref="SqlServerFactAttribute"/>.
/// </summary>
public sealed class SqlServerAppFixture : IAsyncLifetime
{
    /// <summary>Lower than production so lockout can be tested quickly.</summary>
    public const int MaxFailedAccessAttempts = 3;

    public SqlServerTestDatabase Database { get; } = new();

    public BusinessSuiteWebApplicationFactory Factory { get; private set; } = new();

    public async Task InitializeAsync()
    {
        await Database.InitializeAsync();

        if (!TestSettings.IsSqlServerConfigured)
        {
            return;
        }

        await Factory.DisposeAsync();
        Factory = new BusinessSuiteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = Database.ConnectionString,
            ["Identity:Lockout:MaxFailedAccessAttempts"] = MaxFailedAccessAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RateLimiting:LoginPermitLimit"] = "1000",
            ["RateLimiting:RegistrationPermitLimit"] = "1000",
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }
}
