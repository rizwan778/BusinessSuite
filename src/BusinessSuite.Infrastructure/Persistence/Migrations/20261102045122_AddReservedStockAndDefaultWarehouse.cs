using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReservedStockAndDefaultWarehouse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                schema: "inventory",
                table: "Warehouses",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "QuantityReserved",
                schema: "inventory",
                table: "InventoryBalances",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_OrganizationId_Default",
                schema: "inventory",
                table: "Warehouses",
                column: "OrganizationId",
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InventoryBalances_Reserved",
                schema: "inventory",
                table: "InventoryBalances",
                sql: "[QuantityReserved] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Warehouses_OrganizationId_Default",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InventoryBalances_Reserved",
                schema: "inventory",
                table: "InventoryBalances");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                schema: "inventory",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "QuantityReserved",
                schema: "inventory",
                table: "InventoryBalances");
        }
    }
}
