using BusinessSuite.Domain.Organizations;

namespace BusinessSuite.Application.Common.Security;

/// <summary>
/// The DEFAULT <see cref="Permissions"/> of each system role, and the permissions of each platform role.
/// </summary>
/// <remarks>
/// <para>
/// Organization roles live in the database (<c>platform.RoleDefinitions</c>). The migration that created the system
/// roles stored these defaults; afterwards a Super Admin can change them through the API, so the database, not this
/// class, is what applies at runtime. Changing a default here does NOT change existing databases: add a migration.
/// The Owner is the exception: it always holds every organization permission, including ones added later.
/// </para>
/// <para>
/// Platform roles (<see cref="PlatformRoles"/>) are defined here only. Super Admins are not listed: they hold every
/// permission (see <see cref="UserAccessCalculator"/>).
/// </para>
/// </remarks>
public static class RolePermissions
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>(StringComparer.Ordinal);

    // The audit log shows who changed what in user administration: for administrators, not for every viewer.
    private static readonly IReadOnlyList<string> ViewOnly =
        Permissions.OrganizationScoped
            .Where(permission => permission.EndsWith(".view", StringComparison.Ordinal) && permission != Permissions.Audit.View)
            .ToArray();

    private static readonly Dictionary<string, IReadOnlySet<string>> Organization = new(StringComparer.Ordinal)
    {
        [OrganizationRoles.Owner] = Set(Permissions.OrganizationScoped),

        [OrganizationRoles.Administrator] = Set(Permissions.OrganizationScoped.Except([Permissions.Subscription.Manage])),

        [OrganizationRoles.Manager] = Set(ViewOnly.Concat(
        [
            Permissions.Inventory.ProductManage, Permissions.Inventory.AttributeManage, Permissions.Inventory.Manage,
            Permissions.Inventory.StockAdjust, Permissions.Inventory.StockTransfer, Permissions.Purchasing.Manage,
            Permissions.Sales.Manage, Permissions.Invoices.Create, Permissions.Invoices.Cancel,
            Permissions.Payments.Record, Permissions.Reports.Export,
        ])),

        [OrganizationRoles.InventoryManager] = Set(
        [
            Permissions.Organization.View, Permissions.Inventory.View, Permissions.Inventory.ProductManage,
            Permissions.Inventory.AttributeManage, Permissions.Inventory.Manage, Permissions.Inventory.StockAdjust,
            Permissions.Inventory.StockTransfer, Permissions.Inventory.ReportsView, Permissions.Purchasing.View,
            Permissions.Purchasing.Manage, Permissions.Reports.View,
        ]),

        // Sells from the stock it can see; the inventory dashboard and reports show costs and values, so they are left out.
        [OrganizationRoles.SalesOperator] = Set(
        [
            Permissions.Organization.View, Permissions.Inventory.View,
            Permissions.Sales.View, Permissions.Sales.Manage, Permissions.Invoices.View,
            Permissions.Invoices.Create, Permissions.Payments.View, Permissions.Payments.Record,
        ]),

        [OrganizationRoles.Accountant] = Set(
        [
            Permissions.Organization.View, Permissions.Sales.View, Permissions.Purchasing.View,
            Permissions.Invoices.View, Permissions.Payments.View, Permissions.Payments.Record,
            Permissions.Accounts.View, Permissions.Accounts.Manage,
            Permissions.Reports.View, Permissions.Reports.Export, Permissions.Subscription.View,
        ]),

        [OrganizationRoles.ReadOnly] = Set(ViewOnly),
    };

    private static readonly Dictionary<string, IReadOnlySet<string>> Platform = new(StringComparer.Ordinal)
    {
        [PlatformRoles.SuperAdministrator] = Set(Permissions.All),
        [PlatformRoles.Administrator] = Set(Permissions.PlatformScoped),
    };

    /// <summary>Permissions of an organization role; empty for an unknown role.</summary>
    public static IReadOnlySet<string> ForOrganizationRole(string? role) =>
        role is not null && Organization.TryGetValue(role, out var permissions) ? permissions : Empty;

    /// <summary>Permissions of a platform role; empty for an unknown role.</summary>
    public static IReadOnlySet<string> ForPlatformRole(string? role) =>
        role is not null && Platform.TryGetValue(role, out var permissions) ? permissions : Empty;

    private static HashSet<string> Set(IEnumerable<string> permissions) => new(permissions, StringComparer.Ordinal);
}
