using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Inventory;

namespace BusinessSuite.Application.Features.Inventory;

// ---- Warehouses -----------------------------------------------------------------------------------------

/// <summary>A warehouse with a summary of what it holds.</summary>
public sealed record WarehouseDetails(
    Guid Id, Guid? BranchId, string Code, string Name, string? Address, bool IsActive, bool IsDefault,
    int StockLineCount, decimal StockValue, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);

public sealed record WarehouseListQuery
{
    public string? Search { get; init; }

    public bool? IsActive { get; init; }

    public Guid? BranchId { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Input for creating or replacing a warehouse. <c>MakeDefault</c> promotes it to the default warehouse.</summary>
public sealed record SaveWarehouseCommand(Guid? BranchId, string? Code, string? Name, string? Address, bool MakeDefault);

/// <summary>
/// Warehouses. Controllers require <c>inventory.view</c> / <c>inventory.manage</c>. The first warehouse of an organization is
/// its default; there is never more than one. Used warehouses are deactivated, unused ones can be deleted.
/// </summary>
public interface IWarehouseService
{
    Task<Result<PagedResult<WarehouseDetails>>> ListAsync(WarehouseListQuery query, CancellationToken cancellationToken = default);

    Task<Result<WarehouseDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<WarehouseDetails>> GetDefaultAsync(CancellationToken cancellationToken = default);

    Task<Result<WarehouseDetails>> CreateAsync(SaveWarehouseCommand command, CancellationToken cancellationToken = default);

    Task<Result<WarehouseDetails>> UpdateAsync(Guid id, SaveWarehouseCommand command, CancellationToken cancellationToken = default);

    Task<Result<WarehouseDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Makes a warehouse the default (the previous default stops being it, atomically).</summary>
    Task<Result<WarehouseDetails>> SetDefaultAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

// ---- Stock ---------------------------------------------------------------------------------------------

/// <summary>Stock of one product (or variant, or batch) at one warehouse.</summary>
public sealed record StockBalanceDetails(
    Guid ProductId, string Sku, string ProductName, Guid? VariantId, string? VariantSku, string UnitCode,
    Guid WarehouseId, string WarehouseCode, string WarehouseName, Guid? BatchId, string? BatchNumber, DateOnly? ExpiryDate,
    decimal OnHand, decimal Reserved, decimal Available, decimal AverageCost, decimal Value);

public sealed record StockBalanceQuery
{
    /// <summary>Part of the product name, SKU or barcode (or the variant's).</summary>
    public string? Search { get; init; }

    public Guid? ProductId { get; init; }

    public Guid? VariantId { get; init; }

    public Guid? WarehouseId { get; init; }

    public Guid? BatchId { get; init; }

    /// <summary>Leave out balances that have run down to zero.</summary>
    public bool HideEmpty { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Total stock of one stockable item (a simple product, or one variant) across the warehouses asked for.</summary>
public sealed record StockItemDetails(
    Guid ProductId, Guid? VariantId, string Sku, string Name, string UnitCode, decimal? ReorderLevel,
    decimal OnHand, decimal Reserved, decimal Available, decimal Value, bool IsLowStock, bool IsOutOfStock);

public sealed record StockItemQuery
{
    public string? Search { get; init; }

    /// <summary>Count only stock at this warehouse.</summary>
    public Guid? WarehouseId { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>One line of the stock ledger.</summary>
public sealed record StockMovementDetails(
    Guid Id, DateTimeOffset OccurredAtUtc, StockMovementType Type, Guid ProductId, string Sku, string ProductName,
    Guid? VariantId, string? VariantSku, Guid WarehouseId, string WarehouseCode, string WarehouseName, Guid? BatchId, string? BatchNumber,
    decimal Quantity, decimal QuantityAfter, decimal? UnitCost, string? Reference, string? Notes, string SourceType, Guid? SourceId,
    Guid? ReversesMovementId, Guid? RecordedByUserId);

public sealed record StockMovementQuery
{
    public Guid? ProductId { get; init; }

    public Guid? VariantId { get; init; }

    public Guid? WarehouseId { get; init; }

    public Guid? BatchId { get; init; }

    public StockMovementType? Type { get; init; }

    /// <summary>Recorded at or after this instant.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Recorded before this instant.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>
/// Stock levels and the movement history. Read-only: stock changes only through adjustments and transfers (and, later,
/// purchase and sales documents) that write the ledger. Controllers require <c>inventory.view</c>.
/// </summary>
/// <remarks>
/// <b>Quantities.</b> <c>onHand</c> is what is physically there, <c>reserved</c> what is promised to open orders (0 until sales
/// orders exist), <c>available</c> = on hand - reserved. <b>Low stock</b>: the item has a reorder level and its on-hand stock is
/// at or below it (an out-of-stock item is low too). <b>Out of stock</b>: nothing on hand. Only active, stock-tracking items count.
/// </remarks>
public interface IStockService
{
    Task<Result<PagedResult<StockBalanceDetails>>> ListBalancesAsync(StockBalanceQuery query, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<StockItemDetails>>> ListLowStockAsync(StockItemQuery query, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<StockItemDetails>>> ListOutOfStockAsync(StockItemQuery query, CancellationToken cancellationToken = default);

    /// <summary>The ledger, newest first. Filtered by product it is that product's transaction history.</summary>
    Task<Result<PagedResult<StockMovementDetails>>> ListMovementsAsync(StockMovementQuery query, CancellationToken cancellationToken = default);
}
