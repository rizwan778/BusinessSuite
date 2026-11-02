namespace BusinessSuite.Infrastructure.Persistence.Seeding;

/// <summary>
/// Optional development sign-in account, bound from the <c>"DevelopmentSeed"</c> section.
/// </summary>
/// <remarks>
/// Set these values with <c>dotnet user-secrets</c> (never in a committed file):
/// <code>
/// dotnet user-secrets set "DevelopmentSeed:UserEmail" "you@example.com" --project src/BusinessSuite.Web
/// dotnet user-secrets set "DevelopmentSeed:UserPassword" "a-long-unique-password" --project src/BusinessSuite.Web
/// </code>
/// There are no default credentials anywhere in the code, and seeding never runs in Production.
/// </remarks>
public sealed class DevelopmentSeedOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DevelopmentSeed";

    /// <summary>E-mail (and user name) of the development account.</summary>
    public string? UserEmail { get; set; }

    /// <summary>Password of the development account. Must satisfy the password policy.</summary>
    public string? UserPassword { get; set; }

    /// <summary>Display name of the development account.</summary>
    public string? UserDisplayName { get; set; }

    /// <summary>
    /// Organization created for the development account, which becomes its Owner (only if the account belongs
    /// to no organization yet). Empty string: create none.
    /// </summary>
    public string? OrganizationName { get; set; } = "Demo Business";

    /// <summary>Also make the development account a Super Admin (every permission everywhere).</summary>
    public bool UserIsSuperAdmin { get; set; }
}
