using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Inventory;

/// <summary>Why stock changed. Stored as a number and part of the API: do not renumber.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum StockMovementType
{
    /// <summary>Stock that existed when the organization started using the system.</summary>
    OpeningBalance = 1,

    /// <summary>Stock received, e.g. from a supplier.</summary>
    Receipt = 2,

    /// <summary>Stock that left, e.g. sold or used.</summary>
    Issue = 3,

    /// <summary>A count or correction added stock.</summary>
    AdjustmentIncrease = 4,

    /// <summary>A count or correction removed stock (loss, damage, expiry).</summary>
    AdjustmentDecrease = 5,

    /// <summary>The sending half of a transfer between warehouses.</summary>
    TransferOut = 6,

    /// <summary>The receiving half of a transfer between warehouses.</summary>
    TransferIn = 7,

    /// <summary>Undoes an earlier movement that removed stock.</summary>
    ReversalIncrease = 8,

    /// <summary>Undoes an earlier movement that added stock.</summary>
    ReversalDecrease = 9,
}

/// <summary>What caused a movement (the document that posted it). Open set of short codes, stored as text.</summary>
public static class StockSourceTypes
{
    /// <summary>A <see cref="StockAdjustment"/> (the source id is the adjustment, the line id its item).</summary>
    public const string Adjustment = "StockAdjustment";

    /// <summary>A <see cref="StockTransfer"/> (the source id is the transfer, the line id its item).</summary>
    public const string Transfer = "StockTransfer";

    /// <summary>Entered directly, with no document.</summary>
    public const string Manual = "Manual";
}

/// <summary>
/// One line of the stock ledger (<c>inventory.StockMovements</c>): append-only. A mistake is corrected by a new, reversing
/// movement, never by editing or deleting this one (the database refuses both).
/// </summary>
public sealed class StockMovement : TenantEntity
{
    /// <summary>Maximum length of <see cref="Reference"/>.</summary>
    public const int ReferenceMaxLength = 100;

    /// <summary>Maximum length of <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 500;

    /// <summary>Maximum length of <see cref="SourceType"/>.</summary>
    public const int SourceTypeMaxLength = 30;

    private StockMovement()
    {
    }

    /// <summary>The warehouse stock moved in or out of.</summary>
    public Guid WarehouseId { get; private set; }

    /// <summary>The product.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>The variant, for products with variants.</summary>
    public Guid? ProductVariantId { get; private set; }

    /// <summary>The batch, for products that track batches.</summary>
    public Guid? BatchId { get; private set; }

    /// <summary>Why stock moved.</summary>
    public StockMovementType Type { get; private set; }

    /// <summary>Signed quantity in the product's base unit: positive adds stock, negative removes it.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>The unit the user entered the quantity in (e.g. carton), if not the base unit.</summary>
    public Guid? EnteredUnitId { get; private set; }

    /// <summary>The quantity as the user entered it, in <see cref="EnteredUnitId"/> (unsigned).</summary>
    public decimal? EnteredQuantity { get; private set; }

    /// <summary>Cost per base unit of the stock that came in, or of the average cost that left.</summary>
    public decimal? UnitCost { get; private set; }

    /// <summary>The quantity on hand after this movement (a snapshot, so history reads without replaying the ledger).</summary>
    public decimal QuantityAfter { get; private set; }

    /// <summary>When it happened (UTC).</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>An outside document number, e.g. a supplier delivery note.</summary>
    public string? Reference { get; private set; }

    /// <summary>Free text.</summary>
    public string? Notes { get; private set; }

    /// <summary>Kind of document that caused it (<see cref="StockSourceTypes"/>).</summary>
    public string SourceType { get; private set; } = StockSourceTypes.Manual;

    /// <summary>The document that caused it.</summary>
    public Guid? SourceId { get; private set; }

    /// <summary>The line of that document.</summary>
    public Guid? SourceLineId { get; private set; }

    /// <summary>The movement this one reverses; a movement can be reversed once.</summary>
    public Guid? ReversesMovementId { get; private set; }

    /// <summary>Whether <paramref name="type"/> adds stock (otherwise it removes stock).</summary>
    public static bool IncreasesStock(StockMovementType type) => type is
        StockMovementType.OpeningBalance or StockMovementType.Receipt or StockMovementType.AdjustmentIncrease
        or StockMovementType.TransferIn or StockMovementType.ReversalIncrease;

    /// <summary>A movement of <paramref name="quantity"/> (positive; the sign follows <paramref name="type"/>).</summary>
    /// <exception cref="DomainException">A missing owner, warehouse or product, an unknown type, a quantity that is not positive, or a value too long.</exception>
    public static StockMovement Create(
        Guid organizationId,
        Guid warehouseId,
        Guid productId,
        Guid? productVariantId,
        Guid? batchId,
        StockMovementType type,
        decimal quantity,
        decimal quantityAfter,
        DateTimeOffset occurredAtUtc,
        decimal? unitCost = null,
        Guid? enteredUnitId = null,
        decimal? enteredQuantity = null,
        string? reference = null,
        string? notes = null,
        string sourceType = StockSourceTypes.Manual,
        Guid? sourceId = null,
        Guid? sourceLineId = null,
        Guid? reversesMovementId = null)
    {
        DomainGuard.Present(warehouseId, "A stock movement needs a warehouse.");
        DomainGuard.Present(productId, "A stock movement needs a product.");
        DomainGuard.Require(Enum.IsDefined(type), "Unknown stock movement type.");
        DomainGuard.Require(quantity > 0 && quantity <= DomainGuard.MaxAmount, "A stock movement quantity must be greater than zero.");
        DomainGuard.Require(unitCost is null or (>= 0 and <= DomainGuard.MaxAmount), "A unit cost must be zero or more.");
        DomainGuard.Require(enteredUnitId is null == enteredQuantity is null, "Give the entered unit and the entered quantity together.");
        DomainGuard.Require(enteredQuantity is null or > 0, "The entered quantity must be greater than zero.");
        DomainGuard.Require(reversesMovementId is null || type is StockMovementType.ReversalIncrease or StockMovementType.ReversalDecrease,
            "Only a reversal can reverse a movement.");

        var movement = new StockMovement
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            ProductVariantId = productVariantId,
            BatchId = batchId,
            Type = type,
            Quantity = IncreasesStock(type) ? quantity : -quantity,
            EnteredUnitId = enteredUnitId,
            EnteredQuantity = enteredQuantity,
            UnitCost = unitCost,
            QuantityAfter = quantityAfter,
            OccurredAtUtc = occurredAtUtc,
            Reference = DomainGuard.OptionalText(reference, ReferenceMaxLength, "The reference"),
            Notes = DomainGuard.OptionalText(notes, NotesMaxLength, "The notes"),
            SourceType = DomainGuard.Text(sourceType, SourceTypeMaxLength, "The source type"),
            SourceId = sourceId,
            SourceLineId = sourceLineId,
            ReversesMovementId = reversesMovementId,
        };
        movement.BelongTo(organizationId);
        return movement;
    }
}
