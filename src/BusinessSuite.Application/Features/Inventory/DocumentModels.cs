using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Inventory;

namespace BusinessSuite.Application.Features.Inventory;

// ---- Stock adjustments ----------------------------------------------------------------------------------

/// <summary>An adjustment line as returned by the API.</summary>
public sealed record StockAdjustmentItemDetails(
    Guid Id, Guid ProductId, string Sku, string ProductName, Guid? VariantId, string? VariantSku, Guid? BatchId, string? BatchNumber,
    decimal QuantityDifference, decimal? UnitCost, string? Notes, Guid? StockMovementId);

/// <summary>An adjustment in a list.</summary>
public sealed record StockAdjustmentSummary(
    Guid Id, string Number, Guid WarehouseId, string WarehouseName, DateOnly AdjustmentDate, StockAdjustmentReason Reason,
    StockAdjustmentStatus Status, int ItemCount, DateTimeOffset? PostedAtUtc, DateTimeOffset CreatedAtUtc);

/// <summary>An adjustment with its lines.</summary>
public sealed record StockAdjustmentDetails(
    Guid Id, string Number, Guid WarehouseId, string WarehouseName, DateOnly AdjustmentDate, StockAdjustmentReason Reason, string? Notes,
    StockAdjustmentStatus Status, DateTimeOffset? PostedAtUtc, Guid? PostedByUserId, DateTimeOffset CreatedAtUtc,
    IReadOnlyList<StockAdjustmentItemDetails> Items);

public sealed record StockAdjustmentQuery
{
    /// <summary>Part of the document number.</summary>
    public string? Search { get; init; }

    public Guid? WarehouseId { get; init; }

    public StockAdjustmentStatus? Status { get; init; }

    public StockAdjustmentReason? Reason { get; init; }

    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>A line of a new adjustment: a signed change in the product's base unit.</summary>
public sealed record StockAdjustmentItemCommand(Guid ProductId, Guid? VariantId, Guid? BatchId, decimal QuantityDifference, decimal? UnitCost, string? Notes);

/// <summary>Input for creating a draft adjustment. The document number is generated; the date defaults to today.</summary>
public sealed record CreateStockAdjustmentCommand(
    Guid WarehouseId, DateOnly? AdjustmentDate, StockAdjustmentReason Reason, string? Notes, IReadOnlyList<StockAdjustmentItemCommand>? Items);

/// <summary>
/// Stock adjustments (counts, write-offs, found goods, and the way opening stock is entered). A draft can be cancelled or
/// posted; <b>posting</b> writes one ledger movement per line and updates the balances in ONE transaction (all lines or none,
/// refused when stock would go negative). A posted adjustment is final; correct it with a new adjustment.
/// Controllers require <c>inventory.view</c> for reads and <c>inventory.stock.adjust</c> for changes.
/// </summary>
public interface IStockAdjustmentService
{
    Task<Result<PagedResult<StockAdjustmentSummary>>> ListAsync(StockAdjustmentQuery query, CancellationToken cancellationToken = default);

    Task<Result<StockAdjustmentDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<StockAdjustmentDetails>> CreateAsync(CreateStockAdjustmentCommand command, CancellationToken cancellationToken = default);

    Task<Result<StockAdjustmentDetails>> PostAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<StockAdjustmentDetails>> CancelAsync(Guid id, CancellationToken cancellationToken = default);
}

// ---- Stock transfers ------------------------------------------------------------------------------------

/// <summary>A transfer line as returned by the API.</summary>
public sealed record StockTransferItemDetails(
    Guid Id, Guid ProductId, string Sku, string ProductName, Guid? VariantId, string? VariantSku, Guid? BatchId, string? BatchNumber,
    decimal Quantity, decimal? ReceivedQuantity, decimal? UnitCost, Guid? DispatchMovementId, Guid? ReceiptMovementId);

/// <summary>A transfer in a list.</summary>
public sealed record StockTransferSummary(
    Guid Id, string Number, Guid FromWarehouseId, string FromWarehouseName, Guid ToWarehouseId, string ToWarehouseName,
    DateOnly TransferDate, StockTransferStatus Status, int ItemCount, DateTimeOffset? ReceivedAtUtc, DateTimeOffset CreatedAtUtc);

/// <summary>A transfer with its lines.</summary>
public sealed record StockTransferDetails(
    Guid Id, string Number, Guid FromWarehouseId, string FromWarehouseName, Guid ToWarehouseId, string ToWarehouseName,
    DateOnly TransferDate, string? Notes, StockTransferStatus Status, DateTimeOffset? DispatchedAtUtc, DateTimeOffset? ReceivedAtUtc,
    DateTimeOffset CreatedAtUtc, IReadOnlyList<StockTransferItemDetails> Items);

public sealed record StockTransferQuery
{
    public string? Search { get; init; }

    /// <summary>Transfers leaving or arriving at this warehouse.</summary>
    public Guid? WarehouseId { get; init; }

    public StockTransferStatus? Status { get; init; }

    public DateOnly? FromDate { get; init; }

    public DateOnly? ToDate { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>A line of a new transfer.</summary>
public sealed record StockTransferItemCommand(Guid ProductId, Guid? VariantId, Guid? BatchId, decimal Quantity);

/// <summary>Input for creating a draft transfer. The document number is generated; the date defaults to today.</summary>
public sealed record CreateStockTransferCommand(
    Guid FromWarehouseId, Guid ToWarehouseId, DateOnly? TransferDate, string? Notes, IReadOnlyList<StockTransferItemCommand>? Items);

/// <summary>
/// Transfers between warehouses. <b>Posting</b> removes the stock from the source and adds it to the destination, with both
/// ledger movements and both balances, in ONE transaction: the transfer completes entirely or not at all (refused when the
/// source lacks stock). The stock keeps the cost it had at the source. Controllers require <c>inventory.view</c> /
/// <c>inventory.stock.transfer</c>.
/// </summary>
public interface IStockTransferService
{
    Task<Result<PagedResult<StockTransferSummary>>> ListAsync(StockTransferQuery query, CancellationToken cancellationToken = default);

    Task<Result<StockTransferDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<StockTransferDetails>> CreateAsync(CreateStockTransferCommand command, CancellationToken cancellationToken = default);

    Task<Result<StockTransferDetails>> PostAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<StockTransferDetails>> CancelAsync(Guid id, CancellationToken cancellationToken = default);
}

// ---- Batches --------------------------------------------------------------------------------------------

/// <summary>A batch with its stock and expiry status.</summary>
public sealed record BatchDetails(
    Guid Id, Guid ProductId, string Sku, string ProductName, Guid? VariantId, string? VariantSku, string BatchNumber,
    DateOnly? ManufactureDate, DateOnly? ExpiryDate, bool IsActive, bool IsExpired, int? DaysToExpiry, decimal QuantityOnHand,
    DateTimeOffset CreatedAtUtc);

/// <summary>Stock of one batch at one warehouse.</summary>
public sealed record BatchStockDetails(Guid WarehouseId, string WarehouseCode, string WarehouseName, decimal OnHand, decimal Reserved, decimal Available, decimal AverageCost);

public sealed record BatchListQuery
{
    /// <summary>Part of the batch number, product name or SKU.</summary>
    public string? Search { get; init; }

    public Guid? ProductId { get; init; }

    public Guid? VariantId { get; init; }

    public bool? IsActive { get; init; }

    /// <summary>Batches that expire within this many days from today (already expired ones included) and still hold stock.</summary>
    public int? ExpiringWithinDays { get; init; }

    /// <summary>Only batches whose expiry date has passed.</summary>
    public bool ExpiredOnly { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Input for registering a batch of a product that tracks batches.</summary>
public sealed record RegisterBatchCommand(Guid ProductId, Guid? VariantId, string? BatchNumber, DateOnly? ManufactureDate, DateOnly? ExpiryDate);

/// <summary>
/// Batches (lots) of products that track batches, with expiry. Stock enters a batch through an adjustment (or later a
/// purchase receipt) that names it. Controllers require <c>inventory.view</c> / <c>inventory.stock.adjust</c>.
/// </summary>
public interface IBatchService
{
    Task<Result<PagedResult<BatchDetails>>> ListAsync(BatchListQuery query, CancellationToken cancellationToken = default);

    Task<Result<BatchDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<BatchStockDetails>>> GetStockAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<BatchDetails>> RegisterAsync(RegisterBatchCommand command, CancellationToken cancellationToken = default);

    /// <summary>Closes a batch (recalled, finished) or reopens it. A closed batch cannot receive stock.</summary>
    Task<Result<BatchDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);
}

// ---- Dashboard and reports ------------------------------------------------------------------------------

public sealed record InventoryDashboard(
    int TotalProducts, int ActiveProducts, int TotalVariants, int TotalWarehouses, decimal InventoryValue,
    int LowStockCount, int OutOfStockCount, int NearExpiryBatchCount, int ExpiredBatchCount, int NearExpiryDays,
    IReadOnlyList<StockItemDetails> LowStock, IReadOnlyList<StockItemDetails> OutOfStock,
    IReadOnlyList<StockMovementDetails> RecentMovements, IReadOnlyList<BatchDetails> NearExpiry);

public sealed record WarehouseStockSummary(Guid WarehouseId, string Code, string Name, bool IsActive, int StockLineCount, decimal QuantityOnHand, decimal Reserved, decimal Value);

public sealed record InventoryValuation(decimal TotalValue, decimal TotalQuantity, IReadOnlyList<WarehouseStockSummary> ByWarehouse);

/// <summary>
/// The inventory dashboard and reports. Controllers require <c>inventory.reports.view</c>. Valuation is quantity on hand times the
/// moving average cost.
/// </summary>
public interface IInventoryReportService
{
    /// <summary>Totals, valuation, low-stock and out-of-stock lists (top 5), the 10 latest movements and batches expiring within 30 days (top 5).</summary>
    Task<Result<InventoryDashboard>> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<Result<InventoryValuation>> GetValuationAsync(Guid? warehouseId, CancellationToken cancellationToken = default);

    /// <summary>Warehouse-wise stock: what each warehouse holds and is worth.</summary>
    Task<Result<IReadOnlyList<WarehouseStockSummary>>> GetStockByWarehouseAsync(CancellationToken cancellationToken = default);

    /// <summary>Product-wise stock: each item's total across warehouses (or at one warehouse), with value.</summary>
    Task<Result<PagedResult<StockItemDetails>>> GetStockByProductAsync(StockItemQuery query, CancellationToken cancellationToken = default);
}
