using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationsAndAccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AbsoluteExpiresAtUtc",
                schema: "identity",
                table: "UserSessions",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Existing sessions: their absolute limit is their current expiry (instead of the year-0001 default).
            migrationBuilder.Sql("UPDATE [identity].[UserSessions] SET [AbsoluteExpiresAtUtc] = [ExpiresAtUtc];");

            migrationBuilder.CreateTable(
                name: "Organizations",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationMemberships",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMemberships", x => x.Id);
                    table.CheckConstraint("CK_OrganizationMemberships_Role", "[Role] IN (N'Owner', N'Administrator', N'Manager', N'InventoryManager', N'SalesOperator', N'Accountant', N'ReadOnly')");
                    table.ForeignKey(
                        name: "FK_OrganizationMemberships_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrganizationMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "Roles",
                columns: new[] { "Id", "ConcurrencyStamp", "Description", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4a01"), "2c7b0f1e-super-admin", "Super Admin: every permission in every organization and on the platform.", "SuperAdmin", "SUPERADMIN" },
                    { new Guid("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4a02"), "2c7b0f1e-platform-admin", "Platform administration: organizations, users and subscriptions; no access inside organizations.", "PlatformAdministrator", "PLATFORMADMINISTRATOR" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_OrganizationId_UserId",
                schema: "platform",
                table: "OrganizationMemberships",
                columns: new[] { "OrganizationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_UserId",
                schema: "platform",
                table: "OrganizationMemberships",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationMemberships",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "Organizations",
                schema: "platform");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4a01"));

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "Roles",
                keyColumn: "Id",
                keyValue: new Guid("5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4a02"));

            migrationBuilder.DropColumn(
                name: "AbsoluteExpiresAtUtc",
                schema: "identity",
                table: "UserSessions");
        }
    }
}
