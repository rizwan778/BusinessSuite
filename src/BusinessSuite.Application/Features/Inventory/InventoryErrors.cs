using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Inventory;

namespace BusinessSuite.Application.Features.Inventory;

/// <summary>Errors of the inventory module (warehouses, stock, adjustments, transfers, batches). Codes are part of the API contract: do not rename them.</summary>
public static class InventoryErrors
{
    // ---- Warehouses -------------------------------------------------------------------------------------

    public static readonly Error WarehouseNotFound = Error.NotFound("Warehouses.NotFound", "No warehouse with this id exists in your organization.");
    public static readonly Error WarehouseDuplicate = Error.Conflict("Warehouses.Duplicate", "A warehouse with this code or name already exists.");
    public static readonly Error WarehouseInUse = Error.Conflict("Warehouses.InUse", "Stock or documents refer to this warehouse, so it cannot be deleted. Deactivate it instead.");
    public static readonly Error WarehouseInactive = Error.Validation("Warehouses.Inactive", "The warehouse is not active.");
    public static readonly Error DefaultWarehouseMustBeActive = Error.Validation("Warehouses.DefaultMustBeActive", "The default warehouse must be active. Choose another default first.");
    public static Error WarehouseInvalid(string description) => Error.Validation("Warehouses.Invalid", description);
    public static readonly Error BranchInvalid = Error.Validation("Warehouses.BranchInvalid", "Choose an existing branch of your organization.");

    // ---- Stock ------------------------------------------------------------------------------------------

    /// <summary>The product, variant or batch of a stock line is unknown, retired, or does not fit (e.g. a variant of another product).</summary>
    public static Error StockItemInvalid(string description) => Error.Validation("Stock.ItemInvalid", description);

    public static Error InsufficientStock(string item, decimal available) =>
        Error.Conflict("Stock.InsufficientStock", $"Not enough stock of {item}: only {available:0.####} available at this warehouse.");

    public static readonly Error StockConcurrentChange =
        Error.Conflict("Stock.ConcurrentChange", "The stock was changed by someone else at the same time. Check the new levels and try again.");

    // ---- Postings by other modules (sales, purchases) ---------------------------------------------------

    /// <summary>The posting request itself is wrong.</summary>
    public static Error PostingInvalid(string description) => Error.Validation("Stock.PostingInvalid", description);

    /// <summary>A source line has already posted this movement (a retry, a double click): nothing was changed.</summary>
    public static readonly Error AlreadyPosted =
        Error.Conflict("Stock.AlreadyPosted", "This document has already posted these stock movements. Nothing was changed.");

    /// <summary>The source document posted nothing in this organization.</summary>
    public static readonly Error SourceNotFound =
        Error.NotFound("Stock.SourceNotFound", "No stock movements were posted by this document in your organization.");

    /// <summary>Everything the source document posted has been reversed already.</summary>
    public static readonly Error AlreadyReversed =
        Error.Conflict("Stock.AlreadyReversed", "Everything this document posted has already been reversed.");

    /// <summary>Adjustments and transfers are corrected by a new document, not by reversal.</summary>
    public static readonly Error SourceNotReversible =
        Error.Validation("Stock.SourceNotReversible", "Stock adjustments and transfers are corrected with a new document, not reversed.");

    // ---- Adjustments and transfers ----------------------------------------------------------------------

    public static readonly Error AdjustmentNotFound = Error.NotFound("Adjustments.NotFound", "No stock adjustment with this id exists in your organization.");
    public static Error AdjustmentInvalid(string description) => Error.Validation("Adjustments.Invalid", description);
    public static Error AdjustmentNotDraft(StockAdjustmentStatus status) => Error.Conflict("Adjustments.NotDraft", $"Only a draft adjustment can be posted or cancelled. This one is {status}.");

    public static readonly Error TransferNotFound = Error.NotFound("Transfers.NotFound", "No stock transfer with this id exists in your organization.");
    public static Error TransferInvalid(string description) => Error.Validation("Transfers.Invalid", description);
    public static Error TransferNotDraft(StockTransferStatus status) => Error.Conflict("Transfers.NotDraft", $"Only a draft transfer can be posted or cancelled. This one is {status}.");

    // ---- Batches ----------------------------------------------------------------------------------------

    public static readonly Error BatchNotFound = Error.NotFound("Batches.NotFound", "No batch with this id exists in your organization.");
    public static readonly Error BatchDuplicate = Error.Conflict("Batches.Duplicate", "This product already has a batch with this number.");
    public static Error BatchInvalid(string description) => Error.Validation("Batches.Invalid", description);
}
