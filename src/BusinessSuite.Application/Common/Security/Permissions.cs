using System.Reflection;

namespace BusinessSuite.Application.Common.Security;

/// <summary>
/// Every permission in BusinessSuite. Endpoints require permissions, never roles directly:
/// <c>[RequirePermission(Permissions.Invoices.Create)]</c>. Roles are just named bundles of permissions
/// (<see cref="RolePermissions"/>), so a new role never needs a controller change.
/// </summary>
/// <remarks>
/// Values are "<c>area.action</c>" strings. They are returned to API clients by
/// <c>GET /api/v1/auth/me/permissions</c> and used to show or hide UI, so do not rename released ones.
/// </remarks>
public static class Permissions
{
    /// <summary>Prefix of permissions that apply to the whole platform rather than one organization.</summary>
    public const string PlatformPrefix = "platform.";

    public static class Organization
    {
        public const string View = "organization.view";
        public const string Manage = "organization.manage";
    }

    public static class Users
    {
        public const string View = "users.view";
        public const string Manage = "users.manage";
    }

    public static class Roles
    {
        public const string View = "roles.view";
        public const string Manage = "roles.manage";
    }

    public static class Audit
    {
        public const string View = "audit.view";
    }

    /// <summary>
    /// The inventory module (products, attributes, warehouses, stock). Flat names, not nested classes: <see cref="All"/>
    /// collects the constants of the classes directly inside <see cref="Permissions"/>.
    /// </summary>
    public static class Inventory
    {
        /// <summary>Read everything in the module: products, categories, units, attributes, warehouses, batches, stock, documents and the movement history.</summary>
        public const string View = "inventory.view";

        /// <summary>Warehouses: create, change, retire, delete, choose the default.</summary>
        public const string Manage = "inventory.manage";

        /// <summary>Products, variants, categories and units of measure.</summary>
        public const string ProductManage = "inventory.product.manage";

        /// <summary>The attributes organizations define for their products, and their options.</summary>
        public const string AttributeManage = "inventory.attribute.manage";

        /// <summary>Stock adjustments (counts, write-offs, opening stock) and batches.</summary>
        public const string StockAdjust = "inventory.stock.adjust";

        /// <summary>Stock transfers between warehouses.</summary>
        public const string StockTransfer = "inventory.stock.transfer";

        /// <summary>The inventory dashboard and reports, which show costs and values.</summary>
        public const string ReportsView = "inventory.reports.view";
    }

    public static class Purchasing
    {
        public const string View = "purchasing.view";
        public const string Manage = "purchasing.manage";
    }

    public static class Sales
    {
        public const string View = "sales.view";
        public const string Manage = "sales.manage";
    }

    public static class Invoices
    {
        public const string View = "invoices.view";
        public const string Create = "invoices.create";
        public const string Cancel = "invoices.cancel";
    }

    public static class Payments
    {
        public const string View = "payments.view";
        public const string Record = "payments.record";
    }

    public static class Accounts
    {
        public const string View = "accounts.view";
        public const string Manage = "accounts.manage";
    }

    public static class Reports
    {
        public const string View = "reports.view";
        public const string Export = "reports.export";
    }

    public static class Subscription
    {
        public const string View = "subscription.view";
        public const string Manage = "subscription.manage";
    }

    /// <summary>Platform operations (the SaaS provider), granted by platform roles only.</summary>
    public static class Platform
    {
        public const string OrganizationsManage = "platform.organizations.manage";
        public const string UsersManage = "platform.users.manage";
        public const string SubscriptionsManage = "platform.subscriptions.manage";
        public const string AuditView = "platform.audit.view";
    }

    /// <summary>Every permission, sorted.</summary>
    public static IReadOnlyList<string> All { get; } = typeof(Permissions)
        .GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
        .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .Order(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Permissions that apply inside one organization (everything except <c>platform.*</c>).</summary>
    public static IReadOnlyList<string> OrganizationScoped { get; } =
        All.Where(permission => !permission.StartsWith(PlatformPrefix, StringComparison.Ordinal)).ToArray();

    /// <summary>Permissions that apply to the whole platform (<c>platform.*</c>).</summary>
    public static IReadOnlyList<string> PlatformScoped { get; } =
        All.Where(permission => permission.StartsWith(PlatformPrefix, StringComparison.Ordinal)).ToArray();

    /// <summary><see langword="true"/> for a permission defined in this class.</summary>
    public static bool IsDefined(string? permission) =>
        permission is not null && All.Contains(permission, StringComparer.Ordinal);

    /// <summary><see langword="true"/> for a permission a role may grant inside an organization (defined, not <c>platform.*</c>).</summary>
    public static bool IsOrganizationScoped(string? permission) =>
        permission is not null && OrganizationScoped.Contains(permission, StringComparer.Ordinal);
}
