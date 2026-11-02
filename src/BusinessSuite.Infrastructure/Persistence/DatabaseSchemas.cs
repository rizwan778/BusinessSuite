namespace BusinessSuite.Infrastructure.Persistence;

/// <summary>
/// SQL Server schema names. Each module gets its own schema so the database is
/// self-describing (<c>identity.Users</c>, <c>inventory.StockMovements</c>, ...)
/// and permissions can be granted per module later if needed.
/// </summary>
internal static class DatabaseSchemas
{
    /// <summary>ASP.NET Core Identity tables (users, roles, claims, logins, tokens).</summary>
    public const string Identity = "identity";

    /// <summary>Organizations (tenants) and memberships; later plans and subscriptions.</summary>
    public const string Platform = "platform";

    /// <summary>The audit log of administrative changes (append-only).</summary>
    public const string Audit = "audit";

    /// <summary>Products, categories, brands and units of measure.</summary>
    public const string Catalog = "catalog";

    /// <summary>Stock locations, the stock movement ledger and stock balances.</summary>
    public const string Inventory = "inventory";

    // Planned schemas, added together with their first table:
    //   "purchasing" suppliers, purchase orders, goods receipts          (Phase 3)
    //   "sales"      customers, orders, invoices, payments, returns      (Phase 3)
    //   "finance"    expenses, payment methods                           (Phase 3)
}
