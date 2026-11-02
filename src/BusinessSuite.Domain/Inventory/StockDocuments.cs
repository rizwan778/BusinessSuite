using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Inventory;

/// <summary>Lifecycle of a <see cref="StockAdjustment"/>. Stored as a number: do not renumber.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum StockAdjustmentStatus
{
    /// <summary>Being prepared: items can change, the document can be deleted.</summary>
    Draft = 1,

    /// <summary>Applied to stock. Final: it can no longer be changed or deleted.</summary>
    Posted = 2,

    /// <summary>Abandoned before posting.</summary>
    Cancelled = 3,
}

/// <summary>Why stock was adjusted. Stored as a number: do not renumber.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum StockAdjustmentReason
{
    /// <summary>A physical count differed from the system.</summary>
    StockCount = 1,

    /// <summary>Goods were damaged.</summary>
    Damage = 2,

    /// <summary>Goods expired.</summary>
    Expiry = 3,

    /// <summary>Goods were lost or stolen.</summary>
    Loss = 4,

    /// <summary>Goods turned up that were not recorded.</summary>
    Found = 5,

    /// <summary>Anything else (explain in the notes).</summary>
    Other = 6,
}

/// <summary>A stock count or write-off document (<c>inventory.StockAdjustments</c>): a draft until posted, then final.</summary>
public sealed class StockAdjustment : TenantEntity
{
    /// <summary>Maximum length of <see cref="Number"/>.</summary>
    public const int NumberMaxLength = 30;

    /// <summary>Maximum length of <see cref="Notes"/>.</summary>
    public const int NotesMaxLength = 500;

    private readonly List<StockAdjustmentItem> _items = [];

    private StockAdjustment()
    {
    }

    /// <summary>Document number, unique within the organization.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>The warehouse adjusted.</summary>
    public Guid WarehouseId { get; private set; }

    /// <summary>The date the adjustment applies to.</summary>
    public DateOnly AdjustmentDate { get; private set; }

    /// <summary>Why.</summary>
    public StockAdjustmentReason Reason { get; private set; }

    /// <summary>Free text.</summary>
    public string? Notes { get; private set; }

    /// <summary>Where it is in its lifecycle.</summary>
    public StockAdjustmentStatus Status { get; private set; } = StockAdjustmentStatus.Draft;

    /// <summary>When it was posted.</summary>
    public DateTimeOffset? PostedAtUtc { get; private set; }

    /// <summary>Who posted it.</summary>
    public Guid? PostedByUserId { get; private set; }

    /// <summary>Concurrency token, so a document cannot be posted twice at once.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>The lines.</summary>
    public IReadOnlyList<StockAdjustmentItem> Items => _items;

    /// <summary>A draft adjustment.</summary>
    /// <exception cref="DomainException">A missing organization, number or warehouse, or an unknown reason.</exception>
    public static StockAdjustment Create(Guid organizationId, string? number, Guid warehouseId, DateOnly adjustmentDate, StockAdjustmentReason reason, string? notes)
    {
        DomainGuard.Present(warehouseId, "An adjustment needs a warehouse.");
        DomainGuard.Require(Enum.IsDefined(reason), "Choose a reason.");

        var adjustment = new StockAdjustment
        {
            Number = DomainGuard.Text(number, NumberMaxLength, "The document number"),
            WarehouseId = warehouseId,
            AdjustmentDate = adjustmentDate,
            Reason = reason,
            Notes = DomainGuard.OptionalText(notes, NotesMaxLength, "The notes"),
        };
        adjustment.BelongTo(organizationId);
        return adjustment;
    }

    /// <summary>Adds a line. The same product/variant/batch can appear once.</summary>
    /// <exception cref="DomainException">Not a draft, a duplicate line, or an invalid line.</exception>
    public StockAdjustmentItem AddItem(Guid productId, Guid? productVariantId, Guid? batchId, decimal quantityDifference, decimal? unitCost, string? notes)
    {
        DomainGuard.Require(Status == StockAdjustmentStatus.Draft, "Only a draft can be changed.");
        DomainGuard.Require(!_items.Any(item => item.ProductId == productId && item.ProductVariantId == productVariantId && item.BatchId == batchId),
            "This item is already on the adjustment.");

        var item = StockAdjustmentItem.Create(OrganizationId, Id, productId, productVariantId, batchId, quantityDifference, unitCost, notes);
        _items.Add(item);
        return item;
    }

    /// <summary>Marks the adjustment as applied to stock.</summary>
    /// <exception cref="DomainException">Not a draft, or without lines.</exception>
    public void MarkPosted(DateTimeOffset nowUtc, Guid? userId)
    {
        DomainGuard.Require(Status == StockAdjustmentStatus.Draft, "Only a draft can be posted.");
        DomainGuard.Require(_items.Count > 0, "Add at least one item before posting.");

        Status = StockAdjustmentStatus.Posted;
        PostedAtUtc = nowUtc;
        PostedByUserId = userId;
    }

    /// <summary>Abandons a draft.</summary>
    /// <exception cref="DomainException">Not a draft: a posted adjustment is corrected by a new one.</exception>
    public void Cancel()
    {
        DomainGuard.Require(Status == StockAdjustmentStatus.Draft, "Only a draft can be cancelled. Correct a posted adjustment with a new one.");
        Status = StockAdjustmentStatus.Cancelled;
    }
}

/// <summary>One line of a <see cref="StockAdjustment"/> (<c>inventory.StockAdjustmentItems</c>).</summary>
public sealed class StockAdjustmentItem : TenantEntity
{
    private StockAdjustmentItem()
    {
    }

    /// <summary>The adjustment.</summary>
    public Guid StockAdjustmentId { get; private set; }

    /// <summary>The product.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>The variant, for products with variants.</summary>
    public Guid? ProductVariantId { get; private set; }

    /// <summary>The batch, for products that track batches.</summary>
    public Guid? BatchId { get; private set; }

    /// <summary>Signed change in base units: positive adds stock, negative removes it. Never zero.</summary>
    public decimal QuantityDifference { get; private set; }

    /// <summary>Cost per unit for stock added. Optional.</summary>
    public decimal? UnitCost { get; private set; }

    /// <summary>Free text.</summary>
    public string? Notes { get; private set; }

    /// <summary>The ledger movement this line became when the adjustment was posted.</summary>
    public Guid? StockMovementId { get; private set; }

    internal static StockAdjustmentItem Create(
        Guid organizationId, Guid adjustmentId, Guid productId, Guid? productVariantId, Guid? batchId,
        decimal quantityDifference, decimal? unitCost, string? notes)
    {
        DomainGuard.Present(productId, "A line needs a product.");
        DomainGuard.Require(quantityDifference != 0 && Math.Abs(quantityDifference) <= DomainGuard.MaxAmount, "The quantity difference cannot be zero.");
        DomainGuard.Require(unitCost is null or (>= 0 and <= DomainGuard.MaxAmount), "A unit cost must be zero or more.");
        DomainGuard.Require(unitCost is null || quantityDifference > 0, "A unit cost applies only to stock added.");

        var item = new StockAdjustmentItem
        {
            StockAdjustmentId = adjustmentId,
            ProductId = productId,
            ProductVariantId = productVariantId,
            BatchId = batchId,
            QuantityDifference = quantityDifference,
            UnitCost = unitCost,
            Notes = DomainGuard.OptionalText(notes, StockMovement.NotesMaxLength, "The notes"),
        };
        item.BelongTo(organizationId);
        return item;
    }

    /// <summary>Records the ledger movement this line produced.</summary>
    public void LinkMovement(Guid stockMovementId)
    {
        DomainGuard.Present(stockMovementId, "A movement is required.");
        StockMovementId = stockMovementId;
    }
}

/// <summary>Lifecycle of a <see cref="StockTransfer"/>. Stored as a number: do not renumber.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum StockTransferStatus
{
    /// <summary>Being prepared: items can change, the document can be deleted.</summary>
    Draft = 1,

    /// <summary>Left the source warehouse and is on its way (in transit).</summary>
    Dispatched = 2,

    /// <summary>Arrived at the destination warehouse. Final.</summary>
    Received = 3,

    /// <summary>Abandoned before dispatch.</summary>
    Cancelled = 4,
}

/// <summary>Moves stock between two warehouses (<c>inventory.StockTransfers</c>): draft, dispatched (in transit), received.</summary>
public sealed class StockTransfer : TenantEntity
{
    private readonly List<StockTransferItem> _items = [];

    private StockTransfer()
    {
    }

    /// <summary>Document number, unique within the organization.</summary>
    public string Number { get; private set; } = string.Empty;

    /// <summary>Where the stock leaves.</summary>
    public Guid FromWarehouseId { get; private set; }

    /// <summary>Where it arrives.</summary>
    public Guid ToWarehouseId { get; private set; }

    /// <summary>The date of the transfer.</summary>
    public DateOnly TransferDate { get; private set; }

    /// <summary>Free text.</summary>
    public string? Notes { get; private set; }

    /// <summary>Where it is in its lifecycle.</summary>
    public StockTransferStatus Status { get; private set; } = StockTransferStatus.Draft;

    /// <summary>When it left the source.</summary>
    public DateTimeOffset? DispatchedAtUtc { get; private set; }

    /// <summary>When it arrived.</summary>
    public DateTimeOffset? ReceivedAtUtc { get; private set; }

    /// <summary>Concurrency token, so a document cannot be dispatched or received twice at once.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>The lines.</summary>
    public IReadOnlyList<StockTransferItem> Items => _items;

    /// <summary>A draft transfer.</summary>
    /// <exception cref="DomainException">A missing organization, number or warehouse, or the same warehouse twice.</exception>
    public static StockTransfer Create(Guid organizationId, string? number, Guid fromWarehouseId, Guid toWarehouseId, DateOnly transferDate, string? notes)
    {
        DomainGuard.Present(fromWarehouseId, "A transfer needs a source warehouse.");
        DomainGuard.Present(toWarehouseId, "A transfer needs a destination warehouse.");
        DomainGuard.Require(fromWarehouseId != toWarehouseId, "Choose two different warehouses.");

        var transfer = new StockTransfer
        {
            Number = DomainGuard.Text(number, StockAdjustment.NumberMaxLength, "The document number"),
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            TransferDate = transferDate,
            Notes = DomainGuard.OptionalText(notes, StockAdjustment.NotesMaxLength, "The notes"),
        };
        transfer.BelongTo(organizationId);
        return transfer;
    }

    /// <summary>Adds a line. The same product/variant/batch can appear once.</summary>
    /// <exception cref="DomainException">Not a draft, a duplicate line, or an invalid line.</exception>
    public StockTransferItem AddItem(Guid productId, Guid? productVariantId, Guid? batchId, decimal quantity)
    {
        DomainGuard.Require(Status == StockTransferStatus.Draft, "Only a draft can be changed.");
        DomainGuard.Require(!_items.Any(item => item.ProductId == productId && item.ProductVariantId == productVariantId && item.BatchId == batchId),
            "This item is already on the transfer.");

        var item = StockTransferItem.Create(OrganizationId, Id, productId, productVariantId, batchId, quantity);
        _items.Add(item);
        return item;
    }

    /// <summary>Marks the transfer as having left the source warehouse.</summary>
    /// <exception cref="DomainException">Not a draft, or without lines.</exception>
    public void MarkDispatched(DateTimeOffset nowUtc)
    {
        DomainGuard.Require(Status == StockTransferStatus.Draft, "Only a draft can be dispatched.");
        DomainGuard.Require(_items.Count > 0, "Add at least one item before dispatching.");

        Status = StockTransferStatus.Dispatched;
        DispatchedAtUtc = nowUtc;
    }

    /// <summary>Marks the transfer as having arrived.</summary>
    /// <exception cref="DomainException">Not dispatched.</exception>
    public void MarkReceived(DateTimeOffset nowUtc)
    {
        DomainGuard.Require(Status == StockTransferStatus.Dispatched, "Only a dispatched transfer can be received.");

        Status = StockTransferStatus.Received;
        ReceivedAtUtc = nowUtc;
    }

    /// <summary>Abandons a draft.</summary>
    /// <exception cref="DomainException">Not a draft.</exception>
    public void Cancel()
    {
        DomainGuard.Require(Status == StockTransferStatus.Draft, "Only a draft can be cancelled.");
        Status = StockTransferStatus.Cancelled;
    }
}

/// <summary>One line of a <see cref="StockTransfer"/> (<c>inventory.StockTransferItems</c>).</summary>
public sealed class StockTransferItem : TenantEntity
{
    private StockTransferItem()
    {
    }

    /// <summary>The transfer.</summary>
    public Guid StockTransferId { get; private set; }

    /// <summary>The product.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>The variant, for products with variants.</summary>
    public Guid? ProductVariantId { get; private set; }

    /// <summary>The batch, for products that track batches.</summary>
    public Guid? BatchId { get; private set; }

    /// <summary>Quantity sent, in base units.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Quantity that arrived, in base units (set on receipt; may differ from what was sent).</summary>
    public decimal? ReceivedQuantity { get; private set; }

    /// <summary>The cost per unit the stock left the source with (its average cost at dispatch).</summary>
    public decimal? UnitCost { get; private set; }

    /// <summary>The ledger movement out of the source.</summary>
    public Guid? DispatchMovementId { get; private set; }

    /// <summary>The ledger movement into the destination.</summary>
    public Guid? ReceiptMovementId { get; private set; }

    internal static StockTransferItem Create(Guid organizationId, Guid transferId, Guid productId, Guid? productVariantId, Guid? batchId, decimal quantity)
    {
        DomainGuard.Present(productId, "A line needs a product.");
        DomainGuard.Require(quantity > 0 && quantity <= DomainGuard.MaxAmount, "The quantity must be greater than zero.");

        var item = new StockTransferItem
        {
            StockTransferId = transferId,
            ProductId = productId,
            ProductVariantId = productVariantId,
            BatchId = batchId,
            Quantity = quantity,
        };
        item.BelongTo(organizationId);
        return item;
    }

    /// <summary>Records the movement out of the source and the cost the stock left with.</summary>
    public void RecordDispatch(Guid movementId, decimal unitCost)
    {
        DomainGuard.Present(movementId, "A movement is required.");
        DomainGuard.Require(unitCost >= 0, "A unit cost must be zero or more.");
        DispatchMovementId = movementId;
        UnitCost = unitCost;
    }

    /// <summary>Records the movement into the destination and the quantity that arrived.</summary>
    public void RecordReceipt(Guid movementId, decimal receivedQuantity)
    {
        DomainGuard.Present(movementId, "A movement is required.");
        DomainGuard.Require(receivedQuantity > 0 && receivedQuantity <= DomainGuard.MaxAmount, "The received quantity must be greater than zero.");
        ReceiptMovementId = movementId;
        ReceivedQuantity = receivedQuantity;
    }
}
