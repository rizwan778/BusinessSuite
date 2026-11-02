using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Moves organization roles into the database: <c>platform.RoleDefinitions</c> (with the seven system roles and
    /// their default permissions), <c>platform.RoleAvailability</c>, and memberships referencing a role by id.
    /// </summary>
    /// <remarks>
    /// Hand-edited after generation so existing data survives: the system roles are inserted first, every membership's
    /// role NAME is converted to the matching role ID, and only then is the old column dropped. The permission lists
    /// below are the defaults of <c>RolePermissions</c> at the time of writing, deliberately copied as literals so this
    /// migration never changes; an integration test checks they match. The Owner's list is empty because the Owner
    /// always holds every organization permission (computed in code).
    /// </remarks>
    public partial class AddRolesAndPermissions : Migration
    {
        private const string ViewPermissions =
            "\"accounts.view\",\"catalog.view\",\"inventory.view\",\"invoices.view\",\"organization.view\",\"payments.view\"," +
            "\"purchasing.view\",\"reports.view\",\"roles.view\",\"sales.view\",\"subscription.view\",\"users.view\"";

        private static readonly (string Id, string Key, string Description, string Permissions)[] SystemRoles =
        [
            ("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b01", "Owner",
                "Owns the business: every organization permission, including the subscription.",
                "[]"),
            ("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b02", "Administrator",
                "Runs the business day to day: everything except the subscription.",
                "[\"accounts.manage\",\"accounts.view\",\"catalog.manage\",\"catalog.view\",\"inventory.manage\",\"inventory.view\"," +
                "\"invoices.cancel\",\"invoices.create\",\"invoices.view\",\"organization.manage\",\"organization.view\",\"payments.record\"," +
                "\"payments.view\",\"purchasing.manage\",\"purchasing.view\",\"reports.export\",\"reports.view\",\"roles.manage\",\"roles.view\"," +
                "\"sales.manage\",\"sales.view\",\"subscription.view\",\"users.manage\",\"users.view\"]"),
            ("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b03", "Manager",
                "Manages operations: catalogue, stock, purchasing, sales and invoices; cannot manage users or settings.",
                "[\"accounts.view\",\"catalog.manage\",\"catalog.view\",\"inventory.manage\",\"inventory.view\",\"invoices.cancel\"," +
                "\"invoices.create\",\"invoices.view\",\"organization.view\",\"payments.record\",\"payments.view\",\"purchasing.manage\"," +
                "\"purchasing.view\",\"reports.export\",\"reports.view\",\"roles.view\",\"sales.manage\",\"sales.view\",\"subscription.view\",\"users.view\"]"),
            ("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b04", "InventoryManager",
                "Catalogue, stock and purchasing.",
                "[\"catalog.manage\",\"catalog.view\",\"inventory.manage\",\"inventory.view\",\"organization.view\",\"purchasing.manage\"," +
                "\"purchasing.view\",\"reports.view\"]"),
            ("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b05", "SalesOperator",
                "Sells: customers, orders, invoices and payments.",
                "[\"catalog.view\",\"inventory.view\",\"invoices.create\",\"invoices.view\",\"organization.view\",\"payments.record\"," +
                "\"payments.view\",\"sales.manage\",\"sales.view\"]"),
            ("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b06", "Accountant",
                "Accounts, invoices, payments and reports.",
                "[\"accounts.manage\",\"accounts.view\",\"invoices.view\",\"organization.view\",\"payments.record\",\"payments.view\"," +
                "\"purchasing.view\",\"reports.export\",\"reports.view\",\"sales.view\",\"subscription.view\"]"),
            ("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b07", "ReadOnly",
                "Can look at everything, change nothing.",
                "[" + ViewPermissions + "]"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Roles and the system roles.
            migrationBuilder.CreateTable(
                name: "RoleDefinitions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SystemKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    AvailableToAllOrganizations = table.Column<bool>(type: "bit", nullable: false),
                    Permissions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleDefinitions", x => x.Id);
                    table.CheckConstraint("CK_RoleDefinitions_SystemRolesArePlatformRoles", "[SystemKey] IS NULL OR [OrganizationId] IS NULL");
                    table.ForeignKey(
                        name: "FK_RoleDefinitions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleDefinitions_OrganizationId_Name",
                schema: "platform",
                table: "RoleDefinitions",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RoleDefinitions_SystemKey",
                schema: "platform",
                table: "RoleDefinitions",
                column: "SystemKey",
                unique: true,
                filter: "[SystemKey] IS NOT NULL");

            foreach (var (id, key, description, permissions) in SystemRoles)
            {
                migrationBuilder.Sql(
                    "INSERT INTO [platform].[RoleDefinitions] ([Id], [OrganizationId], [SystemKey], [Name], [Description], [AvailableToAllOrganizations], [Permissions], [CreatedAtUtc]) " +
                    $"VALUES ('{id}', NULL, N'{key}', N'{key}', N'{description}', 1, N'{permissions}', SYSUTCDATETIME());");
            }

            // 2. Which platform roles each organization may use.
            migrationBuilder.CreateTable(
                name: "RoleAvailability",
                schema: "platform",
                columns: table => new
                {
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleAvailability", x => new { x.RoleId, x.OrganizationId });
                    table.ForeignKey(
                        name: "FK_RoleAvailability_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleAvailability_RoleDefinitions_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "platform",
                        principalTable: "RoleDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleAvailability_OrganizationId",
                schema: "platform",
                table: "RoleAvailability",
                column: "OrganizationId");

            // 3. Memberships: role name -> role id (every stored name is a system role: the old CHECK constraint guaranteed it).
            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                schema: "platform",
                table: "OrganizationMemberships",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE m SET m.[RoleId] = r.[Id] " +
                "FROM [platform].[OrganizationMemberships] AS m " +
                "INNER JOIN [platform].[RoleDefinitions] AS r ON r.[SystemKey] = m.[Role];");

            migrationBuilder.AlterColumn<Guid>(
                name: "RoleId",
                schema: "platform",
                table: "OrganizationMemberships",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.DropCheckConstraint(
                name: "CK_OrganizationMemberships_Role",
                schema: "platform",
                table: "OrganizationMemberships");

            migrationBuilder.DropColumn(
                name: "Role",
                schema: "platform",
                table: "OrganizationMemberships");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_RoleId",
                schema: "platform",
                table: "OrganizationMemberships",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_OrganizationMemberships_RoleDefinitions_RoleId",
                schema: "platform",
                table: "OrganizationMemberships",
                column: "RoleId",
                principalSchema: "platform",
                principalTable: "RoleDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to role names. Custom roles do not exist in the old model: their holders become ReadOnly
            // (least privilege) and the custom roles are lost.
            migrationBuilder.AddColumn<string>(
                name: "Role",
                schema: "platform",
                table: "OrganizationMemberships",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE m SET m.[Role] = COALESCE(r.[SystemKey], N'ReadOnly') " +
                "FROM [platform].[OrganizationMemberships] AS m " +
                "INNER JOIN [platform].[RoleDefinitions] AS r ON r.[Id] = m.[RoleId];");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                schema: "platform",
                table: "OrganizationMemberships",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OrganizationMemberships_Role",
                schema: "platform",
                table: "OrganizationMemberships",
                sql: "[Role] IN (N'Owner', N'Administrator', N'Manager', N'InventoryManager', N'SalesOperator', N'Accountant', N'ReadOnly')");

            migrationBuilder.DropForeignKey(
                name: "FK_OrganizationMemberships_RoleDefinitions_RoleId",
                schema: "platform",
                table: "OrganizationMemberships");

            migrationBuilder.DropIndex(
                name: "IX_OrganizationMemberships_RoleId",
                schema: "platform",
                table: "OrganizationMemberships");

            migrationBuilder.DropColumn(
                name: "RoleId",
                schema: "platform",
                table: "OrganizationMemberships");

            migrationBuilder.DropTable(
                name: "RoleAvailability",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "RoleDefinitions",
                schema: "platform");
        }
    }
}
