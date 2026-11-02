namespace BusinessSuite.Application.Common.Security;

/// <summary>A permission as shown to people configuring roles.</summary>
/// <param name="Key">The value checked by endpoints, e.g. <c>users.manage</c>.</param>
/// <param name="Name">Short label.</param>
/// <param name="Description">What holding it allows.</param>
public sealed record PermissionInfo(string Key, string Name, string Description);

/// <summary>A group of related permissions (one area of the product).</summary>
/// <param name="Key">Stable group id, e.g. <c>user-management</c>.</param>
/// <param name="Name">Display name, e.g. "User Management".</param>
/// <param name="IsPlatform">Platform permissions: never granted by organization roles.</param>
/// <param name="Permissions">The group's permissions.</param>
public sealed record PermissionGroup(string Key, string Name, bool IsPlatform, IReadOnlyList<PermissionInfo> Permissions);

/// <summary>
/// The default permission catalogue: every <see cref="Permissions"/> value with a label and description, grouped by
/// area. Served by <c>GET /api/v1/permissions</c> so clients can build role editors.
/// </summary>
/// <remarks>
/// Permissions are code, not data: an endpoint enforces a permission, so a new one needs a release. A unit test fails
/// when a permission is missing here or listed twice. Group and permission keys are part of the API contract.
/// </remarks>
public static class PermissionCatalogue
{
    /// <summary>Every group, in display order; the platform group last.</summary>
    public static IReadOnlyList<PermissionGroup> Groups { get; } =
    [
        new("user-management", "User Management", false,
        [
            new(Permissions.Users.View, "View users", "See the organization's users, their roles, departments and status."),
            new(Permissions.Users.Manage, "Manage users", "Create users, change their profile, role, department and status, and start password resets."),
            new(Permissions.Roles.View, "View roles", "See the roles available to the organization and their permissions."),
            new(Permissions.Roles.Manage, "Manage roles", "Create, change and delete the organization's custom roles and their permissions."),
            new(Permissions.Audit.View, "View audit log", "See who created users and changed roles, groups, permissions, status and the organization, and when."),
        ]),
        new("product-management", "Product Management", false,
        [
            new(Permissions.Inventory.ProductManage, "Manage products", "Create and change products, variants, categories, units of measure and prices."),
            new(Permissions.Inventory.AttributeManage, "Manage product attributes", "Define the attributes products are described with (brand, size, colour...) and their options."),
        ]),
        new("inventory", "Inventory", false,
        [
            new(Permissions.Inventory.View, "View inventory", "See products, categories, units, attributes, warehouses, batches, stock levels, stock documents and the movement history."),
            new(Permissions.Inventory.Manage, "Manage warehouses", "Create, change, retire and delete warehouses, and choose the default warehouse."),
            new(Permissions.Inventory.StockAdjust, "Adjust stock", "Create, post and cancel stock adjustments (counts, write-offs, opening stock) and register batches."),
            new(Permissions.Inventory.StockTransfer, "Transfer stock", "Create, post and cancel stock transfers between warehouses."),
        ]),
        new("sales", "Sales", false,
        [
            new(Permissions.Sales.View, "View sales", "See customers, sales orders and returns."),
            new(Permissions.Sales.Manage, "Manage sales", "Create and change customers, sales orders and returns."),
            new(Permissions.Invoices.View, "View invoices", "See invoices."),
            new(Permissions.Invoices.Create, "Create invoices", "Create and confirm invoices."),
            new(Permissions.Invoices.Cancel, "Cancel invoices", "Cancel confirmed invoices."),
        ]),
        new("purchases", "Purchases", false,
        [
            new(Permissions.Purchasing.View, "View purchases", "See suppliers, purchase orders, goods receipts and supplier invoices."),
            new(Permissions.Purchasing.Manage, "Manage purchases", "Create and change suppliers, purchase orders, goods receipts and supplier invoices."),
        ]),
        new("accounts", "Accounts", false,
        [
            new(Permissions.Accounts.View, "View accounts", "See expenses, payment methods, receivables and payables."),
            new(Permissions.Accounts.Manage, "Manage accounts", "Record expenses and manage payment methods."),
            new(Permissions.Payments.View, "View payments", "See customer and supplier payments."),
            new(Permissions.Payments.Record, "Record payments", "Record customer and supplier payments."),
        ]),
        new("reports", "Reports", false,
        [
            new(Permissions.Reports.View, "View reports", "Open sales, purchase, stock and financial reports."),
            new(Permissions.Reports.Export, "Export reports", "Download reports as CSV, Excel or PDF."),
            new(Permissions.Inventory.ReportsView, "View inventory reports", "Open the inventory dashboard and the valuation, warehouse and product stock reports (they show costs and values)."),
        ]),
        new("organization-management", "Organization Management", false,
        [
            new(Permissions.Organization.View, "View organization", "See the organization's profile and settings."),
            new(Permissions.Organization.Manage, "Manage organization", "Change the organization's profile, tax details and settings."),
            new(Permissions.Subscription.View, "View subscription", "See the plan, limits and billing history."),
            new(Permissions.Subscription.Manage, "Manage subscription", "Change the plan and billing details."),
        ]),
        new("platform", "Platform Administration", true,
        [
            new(Permissions.Platform.OrganizationsManage, "Manage organizations", "Create, suspend and reactivate organizations (platform staff)."),
            new(Permissions.Platform.UsersManage, "Manage platform users", "Manage users across organizations (platform staff)."),
            new(Permissions.Platform.SubscriptionsManage, "Manage subscriptions", "Manage plans and organization subscriptions (platform staff)."),
            new(Permissions.Platform.AuditView, "View all audit logs", "See the audit log of every organization and platform-wide changes (platform staff)."),
        ]),
    ];

    /// <summary>The groups that organization roles can grant (everything but the platform group).</summary>
    public static IReadOnlyList<PermissionGroup> OrganizationGroups { get; } = Groups.Where(group => !group.IsPlatform).ToArray();
}
