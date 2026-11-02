using Microsoft.AspNetCore.Identity;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// A PLATFORM-level role stored by ASP.NET Core Identity (for example "PlatformAdministrator").
/// </summary>
/// <remarks>
/// Organization roles (Owner, Manager, Accountant, ...) are NOT Identity roles: they belong to
/// an organization membership (Phase 2), because the same person can be an Owner in one
/// business and a Read-Only user in another.
/// </remarks>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 256;

    /// <summary>Required by EF Core and Identity.</summary>
    public ApplicationRole()
    {
    }

    /// <summary>Creates a role with the given name.</summary>
    public ApplicationRole(string roleName)
        : base(roleName)
    {
    }

    /// <summary>What the role allows, for administrators reading the role list.</summary>
    public string? Description { get; set; }
}
