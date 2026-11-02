using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds the append-only audit log (<c>audit.AuditLogs</c>) and grants the new <c>audit.view</c> permission to the
    /// Administrator system role (the Owner holds every organization permission automatically).
    /// </summary>
    /// <remarks>
    /// Written by hand (the EF tools could not load the build on the development machine), then checked against the model
    /// by the integration tests (no pending model changes, system roles match <c>RolePermissions</c>). Existing data is
    /// untouched apart from the Administrator's permission list, which keeps every permission it had: a Super Admin who
    /// narrowed it keeps the narrowing and only gains the new permission. Rolling back removes <c>audit.view</c> from every
    /// role, since the permission no longer exists then.
    /// </remarks>
    public partial class AddAuditLog : Migration
    {
        private const string AdministratorRoleId = "5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b02";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                schema: "audit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ActorUserId",
                schema: "audit",
                table: "AuditLogs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_EntityType_EntityId",
                schema: "audit",
                table: "AuditLogs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OrganizationId_OccurredAtUtc",
                schema: "audit",
                table: "AuditLogs",
                columns: new[] { "OrganizationId", "OccurredAtUtc" });

            // Append-only: entries can be added, never changed or removed (through the application's login or anyone
            // else's short of dropping the trigger, which needs ALTER permission on the table).
            migrationBuilder.Sql("""
                CREATE TRIGGER [audit].[TR_AuditLogs_AppendOnly]
                ON [audit].[AuditLogs]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'Audit log entries cannot be changed or deleted.', 1;
                END
                """);

            // audit.view for the Administrator, keeping the list sorted (ordinal) like RoleDefinition.SetPermissions does.
            migrationBuilder.Sql($"""
                UPDATE [platform].[RoleDefinitions]
                SET [Permissions] = (
                    SELECT N'[' + STRING_AGG(N'"' + STRING_ESCAPE(p.[value], 'json') + N'"', N',')
                               WITHIN GROUP (ORDER BY p.[value] COLLATE Latin1_General_BIN2) + N']'
                    FROM (
                        SELECT [value] FROM OPENJSON([Permissions])
                        UNION
                        SELECT N'audit.view'
                    ) AS p)
                WHERE [Id] = '{AdministratorRoleId}';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The permission no longer exists after rolling back: take it out of every role that holds it.
            migrationBuilder.Sql("""
                UPDATE r
                SET [Permissions] = COALESCE((
                    SELECT N'[' + STRING_AGG(N'"' + STRING_ESCAPE(p.[value], 'json') + N'"', N',')
                               WITHIN GROUP (ORDER BY p.[value] COLLATE Latin1_General_BIN2) + N']'
                    FROM OPENJSON(r.[Permissions]) AS p
                    WHERE p.[value] <> N'audit.view'), N'[]')
                FROM [platform].[RoleDefinitions] AS r
                WHERE EXISTS (SELECT 1 FROM OPENJSON(r.[Permissions]) AS held WHERE held.[value] = N'audit.view');
                """);

            migrationBuilder.Sql("DROP TRIGGER [audit].[TR_AuditLogs_AppendOnly];");

            migrationBuilder.DropTable(
                name: "AuditLogs",
                schema: "audit");
        }
    }
}
