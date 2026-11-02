using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Replaces the coarse <c>catalog.view</c>, <c>catalog.manage</c> and the single "do everything" use of
    /// <c>inventory.manage</c> by the granular inventory permissions, in every stored role (system, shared and custom).
    /// Data only: no table changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mapping (nobody loses what they could do, nobody gains more than the old permission allowed):
    /// <c>catalog.view</c> becomes <c>inventory.view</c> (which now also reads the catalogue);
    /// <c>catalog.manage</c> becomes <c>inventory.product.manage</c> + <c>inventory.attribute.manage</c>;
    /// <c>inventory.manage</c> keeps warehouses and also becomes <c>inventory.stock.adjust</c> + <c>inventory.stock.transfer</c>;
    /// everyone who could view inventory keeps the dashboard and reports (<c>inventory.reports.view</c>). The one exception is
    /// the SalesOperator system role, which loses the new reports grant again: reports show costs and values, and the role's
    /// default (<c>RolePermissions</c>) leaves them out.
    /// </para>
    /// <para>
    /// Lists are rewritten sorted (ordinal), as <c>RoleDefinition.SetPermissions</c> stores them, and <c>UpdatedAtUtc</c> is
    /// left alone like in the earlier permission migrations, so an integration test can still compare every unmodified system
    /// role with <c>RolePermissions</c>. Rolling back maps each new permission to its closest old one.
    /// </para>
    /// </remarks>
    public partial class SplitInventoryPermissions : Migration
    {
        private const string SalesOperatorRoleId = "5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b05";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH held AS (
                    SELECT r.[Id], p.[value] AS [Permission]
                    FROM [platform].[RoleDefinitions] AS r
                    CROSS APPLY OPENJSON(r.[Permissions]) AS p
                    WHERE EXISTS (
                        SELECT 1 FROM OPENJSON(r.[Permissions]) AS q
                        WHERE q.[value] IN (N'catalog.view', N'catalog.manage', N'inventory.view', N'inventory.manage'))
                ), mapped AS (
                    SELECT [Id], [Permission] FROM held WHERE [Permission] NOT IN (N'catalog.view', N'catalog.manage')
                    UNION SELECT [Id], N'inventory.view' FROM held WHERE [Permission] = N'catalog.view'
                    UNION SELECT [Id], N'inventory.product.manage' FROM held WHERE [Permission] = N'catalog.manage'
                    UNION SELECT [Id], N'inventory.attribute.manage' FROM held WHERE [Permission] = N'catalog.manage'
                    UNION SELECT [Id], N'inventory.stock.adjust' FROM held WHERE [Permission] = N'inventory.manage'
                    UNION SELECT [Id], N'inventory.stock.transfer' FROM held WHERE [Permission] = N'inventory.manage'
                ), final AS (
                    SELECT [Id], [Permission] FROM mapped
                    UNION SELECT [Id], N'inventory.reports.view' FROM mapped WHERE [Permission] = N'inventory.view'
                )
                UPDATE r
                SET [Permissions] = COALESCE(N'[' + (
                    SELECT STRING_AGG(N'"' + STRING_ESCAPE(f.[Permission], 'json') + N'"', N',')
                               WITHIN GROUP (ORDER BY f.[Permission] COLLATE Latin1_General_BIN2)
                    FROM final AS f
                    WHERE f.[Id] = r.[Id]) + N']', N'[]')
                FROM [platform].[RoleDefinitions] AS r
                WHERE r.[Id] IN (SELECT [Id] FROM held);
                """);

            // The SalesOperator default leaves the cost-revealing reports out.
            migrationBuilder.Sql($"""
                UPDATE r
                SET [Permissions] = COALESCE((
                    SELECT N'[' + STRING_AGG(N'"' + STRING_ESCAPE(p.[value], 'json') + N'"', N',')
                               WITHIN GROUP (ORDER BY p.[value] COLLATE Latin1_General_BIN2) + N']'
                    FROM OPENJSON(r.[Permissions]) AS p
                    WHERE p.[value] <> N'inventory.reports.view'), N'[]')
                FROM [platform].[RoleDefinitions] AS r
                WHERE r.[Id] = '{SalesOperatorRoleId}';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH held AS (
                    SELECT r.[Id], p.[value] AS [Permission]
                    FROM [platform].[RoleDefinitions] AS r
                    CROSS APPLY OPENJSON(r.[Permissions]) AS p
                    WHERE EXISTS (
                        SELECT 1 FROM OPENJSON(r.[Permissions]) AS q
                        WHERE q.[value] IN (N'inventory.view', N'inventory.product.manage', N'inventory.attribute.manage',
                                            N'inventory.stock.adjust', N'inventory.stock.transfer', N'inventory.reports.view'))
                ), mapped AS (
                    SELECT [Id], [Permission] FROM held
                    WHERE [Permission] NOT IN (N'inventory.product.manage', N'inventory.attribute.manage', N'inventory.stock.adjust',
                                               N'inventory.stock.transfer', N'inventory.reports.view')
                    UNION SELECT [Id], N'catalog.manage' FROM held WHERE [Permission] IN (N'inventory.product.manage', N'inventory.attribute.manage')
                    UNION SELECT [Id], N'catalog.view' FROM held WHERE [Permission] = N'inventory.view'
                    UNION SELECT [Id], N'inventory.manage' FROM held WHERE [Permission] IN (N'inventory.stock.adjust', N'inventory.stock.transfer')
                )
                UPDATE r
                SET [Permissions] = COALESCE(N'[' + (
                    SELECT STRING_AGG(N'"' + STRING_ESCAPE(m.[Permission], 'json') + N'"', N',')
                               WITHIN GROUP (ORDER BY m.[Permission] COLLATE Latin1_General_BIN2)
                    FROM mapped AS m
                    WHERE m.[Id] = r.[Id]) + N']', N'[]')
                FROM [platform].[RoleDefinitions] AS r
                WHERE r.[Id] IN (SELECT [Id] FROM held);
                """);
        }
    }
}
