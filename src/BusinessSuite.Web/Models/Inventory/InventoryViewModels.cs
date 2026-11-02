using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Inventory;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BusinessSuite.Web.Models.Inventory;

/// <summary>Page position for the shared pager partial.</summary>
public sealed record PagerModel(int Page, int TotalPages, int TotalCount)
{
    public static PagerModel From<T>(PagedResult<T> page) => new(page.Page, page.TotalPages, page.TotalCount);
}

/// <summary>One row of the stock overview: a balance plus what the catalogue says about its category and reorder level.</summary>
public sealed record StockRow(StockBalanceDetails Balance, string? CategoryName, string Status);

public sealed class StockOverviewViewModel
{
    public string? Search { get; init; }

    public Guid? WarehouseId { get; init; }

    public bool HideEmpty { get; init; }

    public required PagedResult<StockBalanceDetails> Page { get; init; }

    public required IReadOnlyList<StockRow> Rows { get; init; }

    public IReadOnlyList<SelectListItem> Warehouses { get; init; } = [];
}

public sealed class WarehouseFormModel
{
    public Guid? Id { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Address { get; set; }

    public bool MakeDefault { get; set; }

    [BindNever]
    public bool IsEdit => Id is not null;

    public SaveWarehouseCommand ToCommand() => new(null, Code, Name, Address, MakeDefault);

    public static WarehouseFormModel From(WarehouseDetails warehouse) => new()
    {
        Id = warehouse.Id,
        Code = warehouse.Code,
        Name = warehouse.Name,
        Address = warehouse.Address,
        MakeDefault = warehouse.IsDefault,
    };
}

public sealed class WarehouseDetailsViewModel
{
    public required WarehouseDetails Warehouse { get; init; }

    public required PagedResult<StockBalanceDetails> Stock { get; init; }
}

/// <summary>One line the user types: the item is found by SKU (product or variant) and, optionally, batch number.</summary>
public sealed class StockLineInput
{
    public string? Sku { get; set; }

    public string? BatchNumber { get; set; }

    /// <summary>Adjustments only: <c>in</c> (increase) or <c>out</c> (decrease).</summary>
    public string? Direction { get; set; } = "in";

    public decimal? Quantity { get; set; }

    public decimal? UnitCost { get; set; }

    public string? Notes { get; set; }

    [BindNever]
    public bool IsBlank => string.IsNullOrWhiteSpace(Sku) && Quantity is null;
}

public sealed class AdjustmentFormModel
{
    public const int LineCount = 6;

    public Guid WarehouseId { get; set; }

    public DateOnly? AdjustmentDate { get; set; }

    public StockAdjustmentReason Reason { get; set; } = StockAdjustmentReason.StockCount;

    public string? Notes { get; set; }

    public bool PostNow { get; set; } = true;

    public List<StockLineInput> Lines { get; set; } = [];

    [BindNever]
    public IReadOnlyList<SelectListItem> Warehouses { get; set; } = [];

    public void PadLines()
    {
        while (Lines.Count < LineCount)
        {
            Lines.Add(new StockLineInput());
        }
    }
}

public sealed class TransferFormModel
{
    public const int LineCount = 6;

    public Guid FromWarehouseId { get; set; }

    public Guid ToWarehouseId { get; set; }

    public DateOnly? TransferDate { get; set; }

    public string? Notes { get; set; }

    public bool PostNow { get; set; } = true;

    public List<StockLineInput> Lines { get; set; } = [];

    [BindNever]
    public IReadOnlyList<SelectListItem> Warehouses { get; set; } = [];

    public void PadLines()
    {
        while (Lines.Count < LineCount)
        {
            Lines.Add(new StockLineInput());
        }
    }
}

public sealed class BatchFormModel
{
    public string? Sku { get; set; }

    public string? BatchNumber { get; set; }

    public DateOnly? ManufactureDate { get; set; }

    public DateOnly? ExpiryDate { get; set; }
}

public sealed class BatchDetailsViewModel
{
    public required BatchDetails Batch { get; init; }

    public required IReadOnlyList<BatchStockDetails> Stock { get; init; }
}

/// <summary>Which inventory report is shown.</summary>
public static class ReportTypes
{
    public const string Summary = "summary";
    public const string Warehouse = "warehouse";
    public const string Product = "product";
    public const string LowStock = "low";
    public const string OutOfStock = "out";
    public const string Movements = "movements";
    public const string Expiry = "expiry";
    public const string Valuation = "valuation";

    public static readonly IReadOnlyList<(string Key, string Title)> All =
    [
        (Summary, "Stock summary"),
        (Warehouse, "Warehouse-wise stock"),
        (Product, "Product-wise stock"),
        (LowStock, "Low stock"),
        (OutOfStock, "Out of stock"),
        (Movements, "Stock movements"),
        (Expiry, "Expiry"),
        (Valuation, "Valuation"),
    ];
}

public sealed class ReportViewModel
{
    public required string Type { get; init; }

    public string? Search { get; init; }

    public Guid? WarehouseId { get; init; }

    public int Days { get; init; } = 30;

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public IReadOnlyList<SelectListItem> Warehouses { get; init; } = [];

    public InventoryDashboard? Dashboard { get; init; }

    public InventoryValuation? Valuation { get; init; }

    public IReadOnlyList<WarehouseStockSummary>? ByWarehouse { get; init; }

    public PagedResult<StockItemDetails>? Items { get; init; }

    public PagedResult<StockMovementDetails>? Movements { get; init; }

    public PagedResult<BatchDetails>? Batches { get; init; }
}
