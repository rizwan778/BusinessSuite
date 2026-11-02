using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Groups",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Groups_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalSchema: "platform",
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Groups_OrganizationId_Name",
                schema: "platform",
                table: "Groups",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            // Departments typed on memberships before groups existed become groups, so administrators can manage them.
            // Hand-written: GROUP BY uses the database collation (the same one the unique index uses), so spellings that
            // differ only in letter case give one group; members keep their Department text unchanged.
            migrationBuilder.Sql("""
                INSERT INTO [platform].[Groups] ([Id], [OrganizationId], [Name], [CreatedAtUtc])
                SELECT NEWID(), [OrganizationId], MIN([Department]), SYSUTCDATETIME()
                FROM [platform].[OrganizationMemberships]
                WHERE [Department] IS NOT NULL
                GROUP BY [OrganizationId], [Department]
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Memberships keep their Department text, so dropping the table loses only the groups' descriptions.
            migrationBuilder.DropTable(
                name: "Groups",
                schema: "platform");
        }
    }
}
