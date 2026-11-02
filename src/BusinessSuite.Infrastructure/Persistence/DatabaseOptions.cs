using System.ComponentModel.DataAnnotations;

namespace BusinessSuite.Infrastructure.Persistence;

/// <summary>
/// Database settings, bound from the <c>"Database"</c> section of appsettings.json.
/// </summary>
/// <remarks>
/// The connection string itself is read from <c>ConnectionStrings:DefaultConnection</c>
/// (the standard .NET location) and copied into <see cref="ConnectionString"/>.
/// It must never be committed with credentials: use <c>dotnet user-secrets</c> locally and
/// the <c>ConnectionStrings__DefaultConnection</c> environment variable (or a secret store) in production.
/// </remarks>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Database";

    /// <summary>Name of the entry under <c>ConnectionStrings</c>.</summary>
    public const string ConnectionStringName = "DefaultConnection";

    /// <summary>SQL Server connection string (filled from <c>ConnectionStrings:DefaultConnection</c>).</summary>
    [Required(ErrorMessage =
        "The connection string 'ConnectionStrings:DefaultConnection' is missing. " +
        "Development: set it with 'dotnet user-secrets' or appsettings.Development.json. " +
        "Production: set the environment variable 'ConnectionStrings__DefaultConnection'. See docs/CONFIGURATION.md.")]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Seconds before a SQL command times out.</summary>
    [Range(5, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Automatic retries for transient SQL errors (0 = off). Useful for cloud databases.
    /// When enabled, code that opens its own transaction must run inside
    /// <c>Database.CreateExecutionStrategy()</c>; see docs/DATABASE_DESIGN.md.
    /// </summary>
    [Range(0, 10)]
    public int MaxRetryCount { get; set; }

    /// <summary>
    /// Apply pending migrations when the app starts. Honoured in Development ONLY;
    /// production databases are migrated with a reviewed SQL script.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    /// <summary>Include parameter values in EF Core logs (Development only; may expose personal data).</summary>
    public bool EnableSensitiveDataLogging { get; set; }

    /// <summary>More detailed EF Core error messages (Development only).</summary>
    public bool EnableDetailedErrors { get; set; }
}
