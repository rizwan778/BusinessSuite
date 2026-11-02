using BusinessSuite.Domain.Common;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BusinessSuite.Tests.Unit.Architecture;

/// <summary>
/// Conventions of the EF Core model that keep organizations apart and the schema as designed in docs/INVENTORY_DATABASE.md.
/// They read the model only (no database), so they run everywhere.
/// </summary>
public sealed class InventoryModelTests
{
    private static readonly IModel Model = BuildModel();

    private static readonly string[] ExpectedTables =
    [
        "platform.Branches",
        "catalog.ProductCategories", "catalog.UnitsOfMeasure", "catalog.UnitConversions", "catalog.Products", "catalog.ProductVariants",
        "catalog.ProductAttributeDefinitions", "catalog.ProductAttributeOptions", "catalog.ProductAttributeValues", "catalog.ProductVariantAttributeValues",
        "inventory.Warehouses", "inventory.ProductBatches", "inventory.InventoryBalances", "inventory.StockMovements",
        "inventory.StockAdjustments", "inventory.StockAdjustmentItems", "inventory.StockTransfers", "inventory.StockTransferItems",
    ];

    private static IEnumerable<IEntityType> TenantTypes =>
        Model.GetEntityTypes().Where(type => typeof(ITenantOwned).IsAssignableFrom(type.ClrType));

    [Fact]
    public void Every_table_of_the_design_exists_in_its_schema()
    {
        var tables = Model.GetEntityTypes().Select(type => $"{type.GetSchema()}.{type.GetTableName()}").ToHashSet();

        Assert.All(ExpectedTables, table => Assert.Contains(table, tables));
    }

    [Fact]
    public void Every_tenant_owned_table_has_a_required_organization_and_an_index_that_starts_with_it()
    {
        Assert.Equal(ExpectedTables.Length, TenantTypes.Count());

        foreach (var type in TenantTypes)
        {
            var organization = type.FindProperty(nameof(ITenantOwned.OrganizationId));
            Assert.True(organization is { IsNullable: false }, $"{type.DisplayName()} needs a non-null OrganizationId.");

            var indexed = type.GetIndexes().Any(index => index.Properties[0] == organization)
                || type.GetKeys().Any(key => key.Properties.Count > 1 && key.Properties[0] == organization);
            Assert.True(indexed, $"{type.DisplayName()} needs an index starting with OrganizationId.");
        }
    }

    [Fact]
    public void Every_reference_between_tenant_tables_includes_the_organization_so_the_database_refuses_cross_tenant_links()
    {
        foreach (var type in TenantTypes)
        {
            foreach (var foreignKey in type.GetForeignKeys().Where(key => typeof(ITenantOwned).IsAssignableFrom(key.PrincipalEntityType.ClrType)))
            {
                Assert.True(foreignKey.Properties.Any(property => property.Name == nameof(ITenantOwned.OrganizationId)),
                    $"{type.DisplayName()} -> {foreignKey.PrincipalEntityType.DisplayName()} must be a composite (OrganizationId, ...) foreign key.");
                Assert.True(foreignKey.PrincipalKey.Properties.Any(property => property.Name == nameof(ITenantOwned.OrganizationId)),
                    $"{type.DisplayName()} -> {foreignKey.PrincipalEntityType.DisplayName()} must point at a key that includes OrganizationId.");
            }
        }
    }

    [Fact]
    public void Foreign_keys_never_cascade_except_document_lines()
    {
        var cascading = Model.GetEntityTypes()
            .SelectMany(type => type.GetForeignKeys())
            .Where(key => key.DeleteBehavior == DeleteBehavior.Cascade && key.DeclaringEntityType.GetSchema() is "catalog" or "inventory")
            .Select(key => key.DeclaringEntityType.ClrType.Name)
            .Order()
            .ToArray();

        Assert.Equal(["StockAdjustmentItem", "StockTransferItem"], cascading);
    }

    [Fact]
    public void Stock_tables_that_change_concurrently_have_a_row_version()
    {
        foreach (var clr in new[] { typeof(BusinessSuite.Domain.Inventory.InventoryBalance), typeof(BusinessSuite.Domain.Inventory.StockAdjustment), typeof(BusinessSuite.Domain.Inventory.StockTransfer) })
        {
            Assert.True(Model.FindEntityType(clr)!.FindProperty("RowVersion")!.IsConcurrencyToken, $"{clr.Name} needs a concurrency token.");
        }
    }

    [Fact]
    public void Quantities_costs_and_prices_are_decimals_with_enough_precision()
    {
        var decimals = Model.GetEntityTypes()
            .Where(type => type.GetSchema() is "catalog" or "inventory")
            .SelectMany(type => type.GetProperties().Select(property => (type, property)))
            .Where(pair => pair.property.ClrType == typeof(decimal) || pair.property.ClrType == typeof(decimal?))
            .ToList();

        Assert.NotEmpty(decimals);
        Assert.All(decimals, pair => Assert.True(pair.property.GetPrecision() >= 18, $"{pair.type.DisplayName()}.{pair.property.Name} needs decimal(18,x) or more."));
    }

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=.;Database=ModelOnly;Integrated Security=true").Options;
        using var context = new AppDbContext(options);
        return context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
    }
}
