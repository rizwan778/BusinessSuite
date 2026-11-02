using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessSuite.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes two stock guarantees a database matter instead of an application habit: a source line posts a given movement only
    /// once, and a posted document (and its lines) can no longer be changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Duplicate posting.</b> <c>IX_StockMovements_SourceLine</c> is unique over (source type, source id, source line, movement
    /// type, warehouse, product, variant, batch). A sales or purchase line that is posted twice (a retry, a double click, a bug)
    /// fails on the second insert instead of doubling the stock. Movements without a source line (manual ones) are not covered.
    /// </para>
    /// <para>
    /// <b>Immutability.</b> The ledger was already append-only (<c>TR_StockMovements_AppendOnly</c>) and posted documents could not
    /// be deleted. These AFTER triggers add: a header whose stored status is final (adjustment Posted/Cancelled, transfer
    /// Received/Cancelled) cannot be updated; lines of a document that is no longer a draft cannot be added or removed, and cannot
    /// change except to be linked ONCE to the ledger movement they became (posting itself does that: EF may save the header's new
    /// status before or after its lines, so the line rule does not depend on the order). Written by hand: EF Core can name a trigger
    /// in the model (<c>HasTrigger</c>) but not model its body.
    /// </para>
    /// Status numbers: adjustment 1 Draft, 2 Posted, 3 Cancelled; transfer 1 Draft, 2 Dispatched, 3 Received, 4 Cancelled.
    /// </remarks>
    public partial class AddStockPostingIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_SourceLine",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "OrganizationId", "SourceType", "SourceId", "SourceLineId", "Type", "WarehouseId", "ProductId", "ProductVariantId", "BatchId" },
                unique: true,
                filter: "[SourceId] IS NOT NULL AND [SourceLineId] IS NOT NULL");

            // ---- Headers: final once posted or cancelled -------------------------------------------------------------
            // Looks at the status BEFORE the update (deleted), so Draft -> Posted is allowed and anything after it is not.
            migrationBuilder.Sql("""
                CREATE TRIGGER [inventory].[TR_StockAdjustments_Immutable]
                ON [inventory].[StockAdjustments]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted WHERE [Status] <> 1)
                        THROW 51013, 'A posted or cancelled stock adjustment cannot be changed. Record a new adjustment instead.', 1;
                END
                """);

            // A dispatched transfer (2) may still be received; a received (3) or cancelled (4) one is final.
            migrationBuilder.Sql("""
                CREATE TRIGGER [inventory].[TR_StockTransfers_Immutable]
                ON [inventory].[StockTransfers]
                AFTER UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM deleted WHERE [Status] IN (3, 4))
                        THROW 51014, 'A received or cancelled stock transfer cannot be changed. Record a new transfer instead.', 1;
                END
                """);

            // ---- Lines: final once the document is no longer a draft -------------------------------------------------
            // Added = in inserted only; removed = in deleted only; changed = in both. A line whose document is gone (cascade of a
            // deleted draft) is never a violation here: deleting a non-draft document is refused by its own trigger.
            migrationBuilder.Sql("""
                CREATE TRIGGER [inventory].[TR_StockAdjustmentItems_Immutable]
                ON [inventory].[StockAdjustmentItems]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;

                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS i
                        JOIN [inventory].[StockAdjustments] AS a ON a.[OrganizationId] = i.[OrganizationId] AND a.[Id] = i.[StockAdjustmentId]
                        WHERE a.[Status] <> 1 AND NOT EXISTS (SELECT 1 FROM deleted AS d WHERE d.[Id] = i.[Id]))
                        THROW 51015, 'Lines cannot be added to a stock adjustment that is no longer a draft.', 1;

                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        JOIN [inventory].[StockAdjustments] AS a ON a.[OrganizationId] = d.[OrganizationId] AND a.[Id] = d.[StockAdjustmentId]
                        WHERE a.[Status] <> 1 AND NOT EXISTS (SELECT 1 FROM inserted AS i WHERE i.[Id] = d.[Id]))
                        THROW 51015, 'Lines cannot be removed from a stock adjustment that is no longer a draft.', 1;

                    -- A change is allowed only when it links the line to its movement for the first time and alters nothing else
                    -- (the update/created-by stamps move along with it, so they are not compared).
                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS i
                        JOIN deleted AS d ON d.[Id] = i.[Id]
                        JOIN [inventory].[StockAdjustments] AS a ON a.[OrganizationId] = i.[OrganizationId] AND a.[Id] = i.[StockAdjustmentId]
                        WHERE a.[Status] <> 1
                          AND NOT (
                                d.[StockMovementId] IS NULL
                            AND i.[StockMovementId] IS NOT NULL
                            AND NOT EXISTS (
                                SELECT i.[OrganizationId], i.[StockAdjustmentId], i.[ProductId], i.[ProductVariantId], i.[BatchId],
                                       i.[QuantityDifference], i.[UnitCost], i.[Notes], i.[CreatedAtUtc], i.[CreatedByUserId]
                                EXCEPT
                                SELECT d.[OrganizationId], d.[StockAdjustmentId], d.[ProductId], d.[ProductVariantId], d.[BatchId],
                                       d.[QuantityDifference], d.[UnitCost], d.[Notes], d.[CreatedAtUtc], d.[CreatedByUserId])))
                        THROW 51015, 'The lines of a stock adjustment that is no longer a draft cannot be changed.', 1;
                END
                """);

            // Transfer lines also receive their cost and received quantity at posting: each may be set once (from NULL), never changed.
            // (value IS NULL OR (new IS NOT NULL AND old = new)) keeps the test two-valued; a plain comparison would hide a change to NULL.
            migrationBuilder.Sql("""
                CREATE TRIGGER [inventory].[TR_StockTransferItems_Immutable]
                ON [inventory].[StockTransferItems]
                AFTER INSERT, UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;

                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS i
                        JOIN [inventory].[StockTransfers] AS t ON t.[OrganizationId] = i.[OrganizationId] AND t.[Id] = i.[StockTransferId]
                        WHERE t.[Status] <> 1 AND NOT EXISTS (SELECT 1 FROM deleted AS d WHERE d.[Id] = i.[Id]))
                        THROW 51016, 'Lines cannot be added to a stock transfer that is no longer a draft.', 1;

                    IF EXISTS (
                        SELECT 1
                        FROM deleted AS d
                        JOIN [inventory].[StockTransfers] AS t ON t.[OrganizationId] = d.[OrganizationId] AND t.[Id] = d.[StockTransferId]
                        WHERE t.[Status] <> 1 AND NOT EXISTS (SELECT 1 FROM inserted AS i WHERE i.[Id] = d.[Id]))
                        THROW 51016, 'Lines cannot be removed from a stock transfer that is no longer a draft.', 1;

                    IF EXISTS (
                        SELECT 1
                        FROM inserted AS i
                        JOIN deleted AS d ON d.[Id] = i.[Id]
                        JOIN [inventory].[StockTransfers] AS t ON t.[OrganizationId] = i.[OrganizationId] AND t.[Id] = i.[StockTransferId]
                        WHERE t.[Status] <> 1
                          AND NOT (
                                (d.[DispatchMovementId] IS NULL OR (i.[DispatchMovementId] IS NOT NULL AND d.[DispatchMovementId] = i.[DispatchMovementId]))
                            AND (d.[ReceiptMovementId] IS NULL OR (i.[ReceiptMovementId] IS NOT NULL AND d.[ReceiptMovementId] = i.[ReceiptMovementId]))
                            AND (d.[UnitCost] IS NULL OR (i.[UnitCost] IS NOT NULL AND d.[UnitCost] = i.[UnitCost]))
                            AND (d.[ReceivedQuantity] IS NULL OR (i.[ReceivedQuantity] IS NOT NULL AND d.[ReceivedQuantity] = i.[ReceivedQuantity]))
                            AND NOT EXISTS (
                                SELECT i.[OrganizationId], i.[StockTransferId], i.[ProductId], i.[ProductVariantId], i.[BatchId],
                                       i.[Quantity], i.[CreatedAtUtc], i.[CreatedByUserId]
                                EXCEPT
                                SELECT d.[OrganizationId], d.[StockTransferId], d.[ProductId], d.[ProductVariantId], d.[BatchId],
                                       d.[Quantity], d.[CreatedAtUtc], d.[CreatedByUserId])))
                        THROW 51016, 'The lines of a stock transfer that is no longer a draft cannot be changed.', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER [inventory].[TR_StockTransferItems_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER [inventory].[TR_StockAdjustmentItems_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER [inventory].[TR_StockTransfers_Immutable];");
            migrationBuilder.Sql("DROP TRIGGER [inventory].[TR_StockAdjustments_Immutable];");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_SourceLine",
                schema: "inventory",
                table: "StockMovements");
        }
    }
}
