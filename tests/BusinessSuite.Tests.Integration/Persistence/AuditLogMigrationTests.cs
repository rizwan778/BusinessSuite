using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BusinessSuite.Tests.Integration.Persistence;

/// <summary>
/// The <c>AddAuditLog</c> migration with data in the database: the Administrator gains <c>audit.view</c> and keeps a
/// Super Admin's customization, other roles are untouched, and rolling back removes the permission everywhere.
/// </summary>
public sealed class AuditLogMigrationTests
{
    private const string PreviousMigration = "AddGroups";
    private const string AuditMigration = "AddAuditLog";

    [SqlServerFact]
    public async Task Administrator_gains_audit_view_keeping_its_customization_and_rolling_back_removes_it_everywhere()
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

            // A Super Admin narrowed the Administrator to two permissions before the upgrade.
            var administrator = OrganizationRoles.IdOf(OrganizationRoles.Administrator);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [platform].[RoleDefinitions] SET [Permissions] = N'[\"users.view\",\"accounts.view\"]' WHERE [Id] = {administrator}");
            var managerBefore = await PermissionsOfAsync(context, OrganizationRoles.IdOf(OrganizationRoles.Manager));

            // Only this migration: later ones change roles too (they have their own tests).
            await migrator.MigrateAsync(AuditMigration);

            // Sorted (ordinal), the narrowing kept, the new permission added; nobody else changed.
            Assert.Equal("[\"accounts.view\",\"audit.view\",\"users.view\"]", await PermissionsOfAsync(context, administrator));
            Assert.Equal(managerBefore, await PermissionsOfAsync(context, OrganizationRoles.IdOf(OrganizationRoles.Manager)));
            Assert.Equal(1, await CountTablesAsync(context, "AuditLogs"));

            // A custom role granted audit.view after the upgrade loses it on rollback too.
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [platform].[RoleDefinitions] SET [Permissions] = N'[\"audit.view\"]' WHERE [Id] = {OrganizationRoles.IdOf(OrganizationRoles.ReadOnly)}");

            await migrator.MigrateAsync(PreviousMigration);

            Assert.Equal("[\"accounts.view\",\"users.view\"]", await PermissionsOfAsync(context, administrator));
            Assert.Equal("[]", await PermissionsOfAsync(context, OrganizationRoles.IdOf(OrganizationRoles.ReadOnly)));
            Assert.Equal(0, await CountTablesAsync(context, "AuditLogs"));
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static Task<string> PermissionsOfAsync(AppDbContext context, Guid roleId) =>
        context.Database.SqlQuery<string>($"SELECT [Permissions] AS [Value] FROM [platform].[RoleDefinitions] WHERE [Id] = {roleId}").SingleAsync();

    private static Task<int> CountTablesAsync(AppDbContext context, string name) =>
        context.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.tables WHERE [name] = {name}").SingleAsync();
}
