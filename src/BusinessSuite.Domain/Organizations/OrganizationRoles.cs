using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Organizations;

/// <summary>
/// The SYSTEM roles a person can hold inside one organization (customer business): built in, present in every
/// environment (created by migration as <see cref="RoleDefinition"/> rows with the fixed ids below), never deleted
/// or renamed. Organizations can also have custom roles (<see cref="RoleDefinition"/>).
/// </summary>
/// <remarks>
/// <para>
/// These are not platform roles: an organization Administrator never gets platform access.
/// The names and ids are part of the database contract: do not change them.
/// </para>
/// <para>
/// The names are RESERVED: no custom role may be called "Owner" (any case), so a role name in the per-request
/// claims identifies a system role unambiguously.
/// </para>
/// Default permissions: <c>BusinessSuite.Application.Common.Security.RolePermissions</c>. The Owner always holds every
/// organization permission; the Super Admin can change the others.
/// </remarks>
public static class OrganizationRoles
{
    /// <summary>Owns the business: everything, including the subscription.</summary>
    public const string Owner = "Owner";

    /// <summary>Runs the business day to day: everything except the subscription.</summary>
    public const string Administrator = "Administrator";

    /// <summary>Manages operations (catalogue, stock, purchasing, sales, invoices); cannot manage users or settings.</summary>
    public const string Manager = "Manager";

    /// <summary>Catalogue, stock and purchasing.</summary>
    public const string InventoryManager = "InventoryManager";

    /// <summary>Sells: customers, orders, invoices and payments.</summary>
    public const string SalesOperator = "SalesOperator";

    /// <summary>Invoices, payments and reports.</summary>
    public const string Accountant = "Accountant";

    /// <summary>Can look at everything, change nothing.</summary>
    public const string ReadOnly = "ReadOnly";

    /// <summary>Maximum stored length of a role name.</summary>
    public const int MaxLength = 50;

    /// <summary>Every organization role, most powerful first.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Owner, Administrator, Manager, InventoryManager, SalesOperator, Accountant, ReadOnly];

    /// <summary><see langword="true"/> if <paramref name="role"/> is one of <see cref="All"/> (exact, case-sensitive).</summary>
    public static bool IsValid(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);

    /// <summary><see langword="true"/> if <paramref name="name"/> is a system role name in any letter case (not allowed for custom roles).</summary>
    public static bool IsReservedName(string? name) =>
        name is not null && All.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Fixed ids of the system roles (<c>platform.RoleDefinitions</c>), keyed by name.</summary>
    public static IReadOnlyDictionary<string, Guid> Ids { get; } = new Dictionary<string, Guid>(StringComparer.Ordinal)
    {
        [Owner] = new("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b01"),
        [Administrator] = new("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b02"),
        [Manager] = new("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b03"),
        [InventoryManager] = new("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b04"),
        [SalesOperator] = new("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b05"),
        [Accountant] = new("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b06"),
        [ReadOnly] = new("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b07"),
    };

    /// <summary>Id of the Owner system role.</summary>
    public static Guid OwnerId => Ids[Owner];

    /// <summary>The fixed id of a system role.</summary>
    /// <exception cref="DomainException">Not a system role name (exact, case-sensitive).</exception>
    public static Guid IdOf(string role) =>
        role is not null && Ids.TryGetValue(role, out var id)
            ? id
            : throw new DomainException($"'{role}' is not a system role. Valid roles: {string.Join(", ", All)}.");
}
