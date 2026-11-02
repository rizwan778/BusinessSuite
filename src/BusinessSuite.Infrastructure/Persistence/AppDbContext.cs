using BusinessSuite.Domain.Auditing;
using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Infrastructure.Persistence;

/// <summary>
/// The single EF Core database context for BusinessSuite (SQL Server).
/// </summary>
/// <remarks>
/// <para>
/// Inherits Identity's tables (users, roles, claims, logins, tokens). Business tables are
/// added module by module as <c>DbSet</c> properties, for example:
/// <code>public DbSet&lt;Product&gt; Products =&gt; Set&lt;Product&gt;();</code>
/// </para>
/// <para>
/// Table mappings live in <c>Persistence/Configurations</c>, one
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> class per entity. They are discovered
/// automatically, so you never edit <see cref="OnModelCreating"/> to add a table.
/// </para>
/// <para>
/// Phase 2 adds tenant (organization) query filters here. Filters are a safety net only:
/// application services must still check tenant ownership explicitly.
/// </para>
/// </remarks>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    /// <summary>API sign-ins; revoked by logout (<c>identity.UserSessions</c>).</summary>
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>Customer businesses (tenants) (<c>platform.Organizations</c>).</summary>
    public DbSet<Organization> Organizations => Set<Organization>();

    /// <summary>Who works in which organization, with which role (<c>platform.OrganizationMemberships</c>).</summary>
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();

    /// <summary>Departments or groups of each organization (<c>platform.Groups</c>).</summary>
    public DbSet<Group> Groups => Set<Group>();

    /// <summary>Organization roles: system, platform and custom (<c>platform.RoleDefinitions</c>).</summary>
    public DbSet<RoleDefinition> RoleDefinitions => Set<RoleDefinition>();

    /// <summary>Which platform roles each organization may use (<c>platform.RoleAvailability</c>).</summary>
    public DbSet<RoleAvailability> RoleAvailability => Set<RoleAvailability>();

    /// <summary>Append-only audit log of administrative changes (<c>audit.AuditLogs</c>).</summary>
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    /// <summary>Shops and offices of each organization (<c>platform.Branches</c>).</summary>
    public DbSet<Branch> Branches => Set<Branch>();

    /// <summary>The category tree (<c>catalog.ProductCategories</c>).</summary>
    public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();

    /// <summary>Units of measure (<c>catalog.UnitsOfMeasure</c>).</summary>
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    /// <summary>Unit conversions (<c>catalog.UnitConversions</c>).</summary>
    public DbSet<UnitConversion> UnitConversions => Set<UnitConversion>();

    /// <summary>Products (<c>catalog.Products</c>).</summary>
    public DbSet<Product> Products => Set<Product>();

    /// <summary>Product variants (<c>catalog.ProductVariants</c>).</summary>
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    /// <summary>Attributes organizations defined (<c>catalog.ProductAttributeDefinitions</c>).</summary>
    public DbSet<ProductAttributeDefinition> ProductAttributeDefinitions => Set<ProductAttributeDefinition>();

    /// <summary>Choices of dropdown and multi-select attributes (<c>catalog.ProductAttributeOptions</c>).</summary>
    public DbSet<ProductAttributeOption> ProductAttributeOptions => Set<ProductAttributeOption>();

    /// <summary>Product attribute values (<c>catalog.ProductAttributeValues</c>).</summary>
    public DbSet<ProductAttributeValue> ProductAttributeValues => Set<ProductAttributeValue>();

    /// <summary>Variant attribute values (<c>catalog.ProductVariantAttributeValues</c>).</summary>
    public DbSet<ProductVariantAttributeValue> ProductVariantAttributeValues => Set<ProductVariantAttributeValue>();

    /// <summary>Warehouses (<c>inventory.Warehouses</c>).</summary>
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();

    /// <summary>Batches (lots) with expiry (<c>inventory.ProductBatches</c>).</summary>
    public DbSet<ProductBatch> ProductBatches => Set<ProductBatch>();

    /// <summary>Stock on hand (<c>inventory.InventoryBalances</c>).</summary>
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();

    /// <summary>The append-only stock ledger (<c>inventory.StockMovements</c>).</summary>
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    /// <summary>Stock adjustments (<c>inventory.StockAdjustments</c>).</summary>
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();

    /// <summary>Lines of stock adjustments (<c>inventory.StockAdjustmentItems</c>).</summary>
    public DbSet<StockAdjustmentItem> StockAdjustmentItems => Set<StockAdjustmentItem>();

    /// <summary>Stock transfers (<c>inventory.StockTransfers</c>).</summary>
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();

    /// <summary>Lines of stock transfers (<c>inventory.StockTransferItems</c>).</summary>
    public DbSet<StockTransferItem> StockTransferItems => Set<StockTransferItem>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // 1. Identity configures its own tables first.
        base.OnModelCreating(builder);

        // 2. Apply every IEntityTypeConfiguration<T> in this assembly (Persistence/Configurations).
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Money and quantities: decimal(18,4) by default. Never float/double for money.
        // A configuration class can still override precision for a specific column.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);

        base.ConfigureConventions(configurationBuilder);
    }
}
