using BusinessSuite.Web.Framework.Security;

namespace BusinessSuite.Web.Framework.Navigation;

/// <summary>Whether a module can be opened yet.</summary>
public enum ModuleStatus
{
    /// <summary>Implemented: the menu item is a link.</summary>
    Available,

    /// <summary>On the roadmap: shown greyed out with its phase, not clickable.</summary>
    Planned,
}

/// <summary>One entry in the sidebar.</summary>
/// <param name="Title">Text shown in the menu.</param>
/// <param name="Icon">Bootstrap Icons name without the <c>bi-</c> prefix (see https://icons.getbootstrap.com).</param>
/// <param name="Controller">MVC controller name without the "Controller" suffix.</param>
/// <param name="Action">MVC action name.</param>
/// <param name="Area">MVC area for module controllers, or <see langword="null"/>.</param>
/// <param name="Policy">
/// Authorization policy required to SEE the item. The controller must still enforce the same
/// policy with <c>[Authorize(Policy = ...)]</c>: hiding a link is not security.
/// </param>
/// <param name="Status">Available or planned.</param>
/// <param name="PlannedPhase">Roadmap phase shown for planned items.</param>
/// <param name="Description">One-line description used on the dashboard.</param>
public sealed record NavigationItem(
    string Title,
    string Icon,
    string? Controller = null,
    string Action = "Index",
    string? Area = null,
    string? Policy = null,
    ModuleStatus Status = ModuleStatus.Available,
    string? PlannedPhase = null,
    string? Description = null);

/// <summary>A titled group of sidebar items.</summary>
public sealed record NavigationSection(string Title, IReadOnlyList<NavigationItem> Items);

/// <summary>
/// The single source of truth for the sidebar and the dashboard's module overview.
/// </summary>
/// <remarks>
/// When a module is implemented: set its <c>Controller</c> (and <c>Area</c>), remove
/// <c>Status: ModuleStatus.Planned</c>, and add the <c>Policy</c> that protects it.
/// </remarks>
public static class NavigationMenu
{
    private const string Phase2 = "Phase 2";
    private const string Phase3 = "Phase 3";
    private const string Phase4 = "Phase 4";

    public static IReadOnlyList<NavigationSection> Sections { get; } =
    [
        new("Overview",
        [
            new("Dashboard", "speedometer2", Controller: "Dashboard",
                Description: "Today's figures and system status at a glance."),
        ]),
        new("Sales",
        [
            new("Sales Orders", "cart3", Status: ModuleStatus.Planned, PlannedPhase: Phase3,
                Description: "Quotations, sales orders and fulfilment."),
            new("Invoices", "receipt", Status: ModuleStatus.Planned, PlannedPhase: Phase3,
                Description: "Invoicing, payments, returns and credit notes."),
            new("Customers", "people", Status: ModuleStatus.Planned, PlannedPhase: Phase3,
                Description: "Customer profiles, balances and statements."),
        ]),
        new("Inventory",
        [
            new("Products", "box-seam", Controller: "Products",
                Description: "Catalogue, variants, prices and dynamic attributes."),
            new("Inventory", "boxes", Controller: "Inventory",
                Description: "Stock ledger, adjustments, transfers and batches."),
            new("Purchases", "bag-check", Controller: "Purchases",
                Description: "Purchase orders and goods receipts."),
            new("Suppliers", "truck", Controller: "Suppliers",
                Description: "Supplier profiles and payable balances."),
        ]),
        new("Finance",
        [
            new("Expenses", "wallet2", Status: ModuleStatus.Planned, PlannedPhase: Phase3,
                Description: "Expense categories, records and payment methods."),
            new("Reports", "bar-chart-line", Status: ModuleStatus.Planned, PlannedPhase: Phase4,
                Description: "Sales, stock, receivables and payables reports."),
        ]),
        new("Administration",
        [
            new("Users & Roles", "person-gear", Status: ModuleStatus.Planned, PlannedPhase: Phase2,
                Description: "Invite users, assign roles and branches."),
            new("Organization", "building-gear", Status: ModuleStatus.Planned, PlannedPhase: Phase2,
                Description: "Business profile, branches, currency and tax settings."),
            new("Subscription", "credit-card-2-front", Status: ModuleStatus.Planned, PlannedPhase: Phase2,
                Description: "Plan, usage limits and billing history."),
        ]),
        new("Platform",
        [
            new("Platform Admin", "shield-lock", Policy: AuthorizationPolicies.PlatformAdministrator,
                Status: ModuleStatus.Planned, PlannedPhase: Phase2,
                Description: "Organizations, plans and subscriptions across the platform."),
        ]),
    ];
}
