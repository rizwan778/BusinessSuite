using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Web.Models.Api.Catalog;

namespace BusinessSuite.Web.Models.Api.Inventory;

// ---- Warehouses -----------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/warehouses</c>.</summary>
public sealed class WarehouseListRequest : PagedRequest
{
    [StringLength(100)]
    public string? Search { get; set; }

    public bool? IsActive { get; set; }

    public Guid? BranchId { get; set; }

    public WarehouseListQuery ToQuery() => new() { Search = Search, IsActive = IsActive, BranchId = BranchId, Page = Page, PageSize = PageSize };
}

/// <summary>JSON body of <c>POST /api/v1/warehouses</c> and <c>PUT /api/v1/warehouses/{id}</c>. The warehouse belongs to the organization the request acts in.</summary>
public sealed class SaveWarehouseRequest
{
    /// <summary>The branch it belongs to, if any.</summary>
    public Guid? BranchId { get; set; }

    [Required(ErrorMessage = "Enter a warehouse code.")]
    [StringLength(Warehouse.CodeMaxLength)]
    public string? Code { get; set; }

    [Required(ErrorMessage = "Enter a warehouse name.")]
    [StringLength(Warehouse.NameMaxLength)]
    public string? Name { get; set; }

    [StringLength(Warehouse.AddressMaxLength)]
    public string? Address { get; set; }

    /// <summary>Make this the default warehouse (the previous default stops being it). The first warehouse is always the default.</summary>
    public bool MakeDefault { get; set; }

    public SaveWarehouseCommand ToCommand() => new(BranchId, Code, Name, Address, MakeDefault);
}

// ---- Stock ---------------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/stock</c>: stock by product and warehouse.</summary>
public sealed class StockBalanceListRequest : PagedRequest
{
    /// <summary>Part of the product name, SKU or barcode (or the variant's).</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    public Guid? ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid? WarehouseId { get; set; }

    public Guid? BatchId { get; set; }

    /// <summary>Leave out balances at zero.</summary>
    public bool HideEmpty { get; set; }

    public StockBalanceQuery ToQuery() => new() { Search = Search, ProductId = ProductId, VariantId = VariantId, WarehouseId = WarehouseId, BatchId = BatchId, HideEmpty = HideEmpty, Page = Page, PageSize = PageSize };
}

/// <summary>Query string of the low-stock, out-of-stock and product-wise stock endpoints.</summary>
public sealed class StockItemListRequest : PagedRequest
{
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary>Count only stock at this warehouse.</summary>
    public Guid? WarehouseId { get; set; }

    public StockItemQuery ToQuery() => new() { Search = Search, WarehouseId = WarehouseId, Page = Page, PageSize = PageSize };
}

/// <summary>Query string of <c>GET /api/v1/stock-movements</c>. With <c>productId</c> it is that product's transaction history.</summary>
public sealed class StockMovementListRequest : PagedRequest
{
    public Guid? ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid? WarehouseId { get; set; }

    public Guid? BatchId { get; set; }

    /// <summary><c>OpeningBalance</c>, <c>Receipt</c>, <c>Issue</c>, <c>AdjustmentIncrease</c>, <c>AdjustmentDecrease</c>, <c>TransferOut</c>, <c>TransferIn</c>, <c>ReversalIncrease</c> or <c>ReversalDecrease</c>.</summary>
    public StockMovementType? Type { get; set; }

    /// <summary>Recorded at or after this instant (ISO 8601, UTC).</summary>
    public DateTimeOffset? FromUtc { get; set; }

    /// <summary>Recorded before this instant.</summary>
    public DateTimeOffset? ToUtc { get; set; }

    public StockMovementQuery ToQuery() => new() { ProductId = ProductId, VariantId = VariantId, WarehouseId = WarehouseId, BatchId = BatchId, Type = Type, FromUtc = FromUtc, ToUtc = ToUtc, Page = Page, PageSize = PageSize };
}

// ---- Adjustments ---------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/stock-adjustments</c>.</summary>
public sealed class StockAdjustmentListRequest : PagedRequest
{
    /// <summary>Part of the document number.</summary>
    [StringLength(30)]
    public string? Search { get; set; }

    public Guid? WarehouseId { get; set; }

    /// <summary><c>Draft</c>, <c>Posted</c> or <c>Cancelled</c>.</summary>
    public StockAdjustmentStatus? Status { get; set; }

    /// <summary><c>StockCount</c>, <c>Damage</c>, <c>Expiry</c>, <c>Loss</c>, <c>Found</c> or <c>Other</c>.</summary>
    public StockAdjustmentReason? Reason { get; set; }

    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public StockAdjustmentQuery ToQuery() => new() { Search = Search, WarehouseId = WarehouseId, Status = Status, Reason = Reason, FromDate = FromDate, ToDate = ToDate, Page = Page, PageSize = PageSize };
}

/// <summary>One line of a new adjustment.</summary>
public sealed class StockAdjustmentItemRequest
{
    [Required(ErrorMessage = "Choose a product.")]
    public Guid? ProductId { get; set; }

    /// <summary>Required for a product with variants, not allowed otherwise.</summary>
    public Guid? VariantId { get; set; }

    /// <summary>Required for a product that tracks batches, not allowed otherwise.</summary>
    public Guid? BatchId { get; set; }

    /// <summary>Signed change in the product's base unit: positive adds stock, negative removes it. Not zero.</summary>
    [Required(ErrorMessage = "Enter the quantity difference.")]
    [Range(typeof(decimal), "-99999999999999", "99999999999999")]
    public decimal? QuantityDifference { get; set; }

    /// <summary>Cost per unit of stock added (only for a positive difference); sets or blends the average cost.</summary>
    [Range(typeof(decimal), "0", "99999999999999")]
    public decimal? UnitCost { get; set; }

    [StringLength(StockMovement.NotesMaxLength)]
    public string? Notes { get; set; }

    public StockAdjustmentItemCommand ToCommand() => new(ProductId!.Value, VariantId, BatchId, QuantityDifference!.Value, UnitCost, Notes);
}

/// <summary>JSON body of <c>POST /api/v1/stock-adjustments</c> (creates a draft; the number is generated).</summary>
public sealed class CreateStockAdjustmentRequest
{
    [Required(ErrorMessage = "Choose a warehouse.")]
    public Guid? WarehouseId { get; set; }

    /// <summary>The date the adjustment applies to; defaults to today (UTC).</summary>
    public DateOnly? AdjustmentDate { get; set; }

    /// <summary><c>StockCount</c>, <c>Damage</c>, <c>Expiry</c>, <c>Loss</c>, <c>Found</c> or <c>Other</c>.</summary>
    [Required(ErrorMessage = "Choose a reason.")]
    public StockAdjustmentReason? Reason { get; set; }

    [StringLength(StockAdjustment.NotesMaxLength)]
    public string? Notes { get; set; }

    [Required(ErrorMessage = "Add at least one item.")]
    [MinLength(1, ErrorMessage = "Add at least one item.")]
    [MaxLength(500)]
    public List<StockAdjustmentItemRequest>? Items { get; set; }

    public CreateStockAdjustmentCommand ToCommand() => new(WarehouseId!.Value, AdjustmentDate, Reason!.Value, Notes, Items!.ConvertAll(item => item.ToCommand()));
}

// ---- Transfers -----------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/stock-transfers</c>.</summary>
public sealed class StockTransferListRequest : PagedRequest
{
    [StringLength(30)]
    public string? Search { get; set; }

    /// <summary>Transfers leaving or arriving at this warehouse.</summary>
    public Guid? WarehouseId { get; set; }

    /// <summary><c>Draft</c>, <c>Dispatched</c>, <c>Received</c> or <c>Cancelled</c>.</summary>
    public StockTransferStatus? Status { get; set; }

    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public StockTransferQuery ToQuery() => new() { Search = Search, WarehouseId = WarehouseId, Status = Status, FromDate = FromDate, ToDate = ToDate, Page = Page, PageSize = PageSize };
}

/// <summary>One line of a new transfer.</summary>
public sealed class StockTransferItemRequest
{
    [Required(ErrorMessage = "Choose a product.")]
    public Guid? ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid? BatchId { get; set; }

    /// <summary>Quantity to move, in the product's base unit. Greater than zero.</summary>
    [Required(ErrorMessage = "Enter the quantity.")]
    [Range(typeof(decimal), "0.0001", "99999999999999")]
    public decimal? Quantity { get; set; }

    public StockTransferItemCommand ToCommand() => new(ProductId!.Value, VariantId, BatchId, Quantity!.Value);
}

/// <summary>JSON body of <c>POST /api/v1/stock-transfers</c> (creates a draft; the number is generated).</summary>
public sealed class CreateStockTransferRequest
{
    [Required(ErrorMessage = "Choose the warehouse the stock leaves.")]
    public Guid? FromWarehouseId { get; set; }

    [Required(ErrorMessage = "Choose the warehouse the stock arrives at.")]
    public Guid? ToWarehouseId { get; set; }

    /// <summary>The date of the transfer; defaults to today (UTC).</summary>
    public DateOnly? TransferDate { get; set; }

    [StringLength(StockAdjustment.NotesMaxLength)]
    public string? Notes { get; set; }

    [Required(ErrorMessage = "Add at least one item.")]
    [MinLength(1, ErrorMessage = "Add at least one item.")]
    [MaxLength(500)]
    public List<StockTransferItemRequest>? Items { get; set; }

    public CreateStockTransferCommand ToCommand() => new(FromWarehouseId!.Value, ToWarehouseId!.Value, TransferDate, Notes, Items!.ConvertAll(item => item.ToCommand()));
}

// ---- Batches -------------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/batches</c>.</summary>
public sealed class BatchListRequest : PagedRequest
{
    /// <summary>Part of the batch number, product name or SKU.</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    public Guid? ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public bool? IsActive { get; set; }

    /// <summary>Batches that expire within this many days from today (expired ones included) and still hold stock.</summary>
    [Range(0, 3650)]
    public int? ExpiringWithinDays { get; set; }

    /// <summary>Only batches whose expiry date has passed.</summary>
    public bool ExpiredOnly { get; set; }

    public BatchListQuery ToQuery() => new() { Search = Search, ProductId = ProductId, VariantId = VariantId, IsActive = IsActive, ExpiringWithinDays = ExpiringWithinDays, ExpiredOnly = ExpiredOnly, Page = Page, PageSize = PageSize };
}

/// <summary>JSON body of <c>POST /api/v1/batches</c>: registers a batch of a product that tracks batches.</summary>
public sealed class RegisterBatchRequest
{
    [Required(ErrorMessage = "Choose a product.")]
    public Guid? ProductId { get; set; }

    /// <summary>Required for a product with variants.</summary>
    public Guid? VariantId { get; set; }

    [Required(ErrorMessage = "Enter the batch number.")]
    [StringLength(ProductBatch.BatchNumberMaxLength)]
    public string? BatchNumber { get; set; }

    public DateOnly? ManufactureDate { get; set; }

    /// <summary>Required when the product requires expiry dates.</summary>
    public DateOnly? ExpiryDate { get; set; }

    public RegisterBatchCommand ToCommand() => new(ProductId!.Value, VariantId, BatchNumber, ManufactureDate, ExpiryDate);
}

// ---- Reports -------------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/inventory/reports/valuation</c>.</summary>
public sealed class ValuationRequest
{
    /// <summary>Value only this warehouse; leave empty for all.</summary>
    public Guid? WarehouseId { get; set; }
}
