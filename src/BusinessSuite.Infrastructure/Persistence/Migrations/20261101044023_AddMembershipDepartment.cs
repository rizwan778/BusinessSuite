using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipDepartment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Department",
                schema: "platform",
                table: "OrganizationMemberships",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Department",
                schema: "platform",
                table: "OrganizationMemberships");
        }
    }
}
