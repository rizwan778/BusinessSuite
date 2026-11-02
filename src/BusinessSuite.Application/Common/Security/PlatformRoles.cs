namespace BusinessSuite.Application.Common.Security;

/// <summary>
/// Roles for people who operate the PLATFORM (the SaaS provider), not for customer businesses.
/// Stored as ASP.NET Core Identity roles (<c>identity.Roles</c>, seeded by migration).
/// </summary>
/// <remarks>
/// Customer (organization) roles such as Owner, Administrator, Manager, Inventory Manager,
/// Sales Operator, Accountant and Read-Only are a separate concept, stored per organization
/// membership (<c>BusinessSuite.Domain.Organizations.OrganizationRoles</c>).
/// An organization Administrator must NEVER receive platform access.
/// </remarks>
public static class PlatformRoles
{
    /// <summary>
    /// Super Admin: every permission in every organization, plus all platform permissions.
    /// Grant to as few people as possible; every grant should be deliberate and reviewed.
    /// </summary>
    public const string SuperAdministrator = "SuperAdmin";

    /// <summary>Platform administration (organizations, users, subscriptions); no access inside organizations.</summary>
    public const string Administrator = "PlatformAdministrator";

    /// <summary>Every platform role, most powerful first.</summary>
    public static IReadOnlyList<string> All { get; } = [SuperAdministrator, Administrator];
}
