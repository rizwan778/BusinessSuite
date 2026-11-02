using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BusinessSuite.Tests.Integration.Persistence;

/// <summary>
/// The <c>SplitInventoryPermissions</c> migration with data in the database: the seven system roles end up with exactly the
/// new defaults of <see cref="RolePermissions"/>, custom roles are mapped without gaining more than the old permission allowed,
/// and rolling back maps each new permission to its closest old one.
/// </summary>
public sealed class InventoryPermissionsMigrationTests
{
    private const string PreviousMigration = "AddReservedStockAndDefaultWarehouse";

    [SqlServerFact]
    public async Task System_roles_end_up_with_the_new_defaults_and_custom_roles_are_mapped_both_ways()
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

            // Roles as the old model could hold them (shared roles: no organization, so no organization needs to exist).
            var catalogManager = await AddRoleAsync(context, "Catalog Manager", """["catalog.manage","catalog.view","sales.view"]""");
            var catalogViewer = await AddRoleAsync(context, "Catalog Viewer", """["catalog.view"]""");
            var warehouseHand = await AddRoleAsync(context, "Warehouse Hand", """["inventory.manage"]""");
            var salesViewer = await AddRoleAsync(context, "Sales Viewer", """["sales.view"]""");
            var empty = await AddRoleAsync(context, "Nothing", "[]");

            await migrator.MigrateAsync();

            // 1. The migration reproduces the code defaults exactly: this is what keeps RolePermissions and the database honest.
            foreach (var role in OrganizationRoles.All.Where(role => role != OrganizationRoles.Owner))
            {
                Assert.Equal(Json(RolePermissions.ForOrganizationRole(role)), await PermissionsOfAsync(context, OrganizationRoles.IdOf(role)));
            }

            Assert.Equal("[]", await PermissionsOfAsync(context, OrganizationRoles.OwnerId)); // the Owner always holds everything: nothing is stored

            // 2. Custom roles: each old permission becomes its new counterparts, sorted; unrelated roles are untouched.
            Assert.Equal(
                Json([Permissions.Inventory.AttributeManage, Permissions.Inventory.ProductManage, Permissions.Inventory.ReportsView, Permissions.Inventory.View, Permissions.Sales.View]),
                await PermissionsOfAsync(context, catalogManager));
            Assert.Equal(Json([Permissions.Inventory.ReportsView, Permissions.Inventory.View]), await PermissionsOfAsync(context, catalogViewer));
            Assert.Equal(
                Json([Permissions.Inventory.Manage, Permissions.Inventory.StockAdjust, Permissions.Inventory.StockTransfer]),
                await PermissionsOfAsync(context, warehouseHand)); // no inventory.view before, so no reports now
            Assert.Equal(Json([Permissions.Sales.View]), await PermissionsOfAsync(context, salesViewer));
            Assert.Equal("[]", await PermissionsOfAsync(context, empty));

            // 3. The old permissions are gone everywhere.
            Assert.Equal(0, await context.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM [platform].[RoleDefinitions] WHERE [Permissions] LIKE N'%catalog.%'").SingleAsync());

            // 4. Rolling back: product/attribute management is catalog.manage again, stock operations are inventory.manage,
            //    and the new reports permission disappears.
            await migrator.MigrateAsync(PreviousMigration);

            Assert.Equal(Json(["catalog.manage", "catalog.view", "inventory.view", "sales.view"]), await PermissionsOfAsync(context, catalogManager));
            Assert.Equal(Json(["catalog.view", "inventory.view"]), await PermissionsOfAsync(context, catalogViewer));
            Assert.Equal(Json(["inventory.manage"]), await PermissionsOfAsync(context, warehouseHand));
            Assert.Equal(Json(["sales.view"]), await PermissionsOfAsync(context, salesViewer));
            Assert.Equal(0, await context.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS [Value] FROM [platform].[RoleDefinitions] WHERE [Permissions] LIKE N'%inventory.stock.%' OR [Permissions] LIKE N'%inventory.reports.%' OR [Permissions] LIKE N'%inventory.product.%' OR [Permissions] LIKE N'%inventory.attribute.%'").SingleAsync());
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static string Json(IEnumerable<string> permissions) =>
        "[" + string.Join(",", permissions.Order(StringComparer.Ordinal).Select(permission => $"\"{permission}\"")) + "]";

    private static async Task<Guid> AddRoleAsync(AppDbContext context, string name, string permissionsJson)
    {
        var id = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [platform].[RoleDefinitions] ([Id], [OrganizationId], [SystemKey], [Name], [Description], [AvailableToAllOrganizations], [Permissions], [CreatedAtUtc])
            VALUES ({id}, NULL, NULL, {name}, NULL, 0, {permissionsJson}, SYSUTCDATETIME())
            """);
        return id;
    }

    private static Task<string> PermissionsOfAsync(AppDbContext context, Guid roleId) =>
        context.Database.SqlQuery<string>($"SELECT [Permissions] AS [Value] FROM [platform].[RoleDefinitions] WHERE [Id] = {roleId}").SingleAsync();
}
