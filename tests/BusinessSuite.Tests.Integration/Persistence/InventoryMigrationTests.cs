using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BusinessSuite.Tests.Integration.Persistence;

/// <summary>
/// The <c>AddInventory</c> migration on a database that already holds data: existing tables and rows are untouched, the
/// new tables and the three protecting triggers appear, and rolling back removes only what the migration added.
/// </summary>
public sealed class InventoryMigrationTests
{
    private const string InventoryMigration = "AddInventory";
    private const string PreviousMigration = "AddAuditLog";

    private static readonly string[] NewTables =
    [
        "Branches", "ProductCategories", "UnitsOfMeasure", "UnitConversions", "Products", "ProductVariants", "ProductAttributeDefinitions",
        "ProductAttributeOptions", "ProductAttributeValues", "ProductVariantAttributeValues", "Warehouses", "ProductBatches",
        "InventoryBalances", "StockMovements", "StockAdjustments", "StockAdjustmentItems", "StockTransfers", "StockTransferItems",
    ];

    private static readonly string[] Triggers = ["TR_StockMovements_AppendOnly", "TR_StockAdjustments_NoDeletePosted", "TR_StockTransfers_NoDeletePosted"];

    [SqlServerFact]
    public async Task Migrating_up_adds_the_inventory_tables_and_triggers_without_touching_existing_data_and_down_removes_them()
    {
        var connection = new SqlConnectionStringBuilder(TestSettings.SqlServerConnectionString)
        {
            InitialCatalog = SqlServerTestDatabase.DatabaseNamePrefix + Guid.NewGuid().ToString("N")[..12],
        }.ConnectionString;

        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options);
        try
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO [platform].[Organizations] ([Id],[Name],[IsActive],[CreatedAtUtc]) VALUES (NEWID(), N'Existing business', 1, SYSDATETIMEOFFSET())");
            var rolesBefore = await ScalarAsync(context, "SELECT COUNT(*) AS [Value] FROM [platform].[RoleDefinitions]");
            Assert.Equal(0, await TablesAsync(context));

            // Only this migration: later ones add tables, columns and triggers of their own (and have their own tests).
            await migrator.MigrateAsync(InventoryMigration);

            Assert.Equal(NewTables.Length, await TablesAsync(context));
            Assert.Equal(Triggers.Length, await ScalarAsync(context, "SELECT COUNT(*) AS [Value] FROM sys.triggers WHERE [name] LIKE 'TR[_]Stock%'"));
            Assert.Equal(1, await ScalarAsync(context, "SELECT COUNT(*) AS [Value] FROM [platform].[Organizations] WHERE [Name] = N'Existing business'"));
            Assert.Equal(rolesBefore, await ScalarAsync(context, "SELECT COUNT(*) AS [Value] FROM [platform].[RoleDefinitions]"));

            await migrator.MigrateAsync(PreviousMigration);

            Assert.Equal(0, await TablesAsync(context));
            Assert.Equal(1, await ScalarAsync(context, "SELECT COUNT(*) AS [Value] FROM [platform].[Organizations] WHERE [Name] = N'Existing business'"));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static Task<int> TablesAsync(AppDbContext context) =>
        context.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value] FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE (s.[name] IN ('catalog','inventory')) OR (s.[name] = 'platform' AND t.[name] = 'Branches')
            """).SingleAsync();

    private static Task<int> ScalarAsync(AppDbContext context, string sql) =>
        context.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
