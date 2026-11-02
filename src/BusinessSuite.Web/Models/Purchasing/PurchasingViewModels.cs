using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Purchasing;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BusinessSuite.Web.Models.Purchasing;

public sealed class PurchaseListViewModel
{
    public required PurchaseListQuery Query { get; init; }

    public required PagedResult<PurchaseSummary> Purchases { get; init; }

    public IReadOnlyList<SelectListItem> Suppliers { get; init; } = [];
}

/// <summary>One purchase line the user types; the product or variant is found by SKU.</summary>
public sealed class PurchaseLineInput
{
    public string? Sku { get; set; }

    public decimal? Quantity { get; set; }

    public decimal? UnitPrice { get; set; }

    public decimal? DiscountPercent { get; set; }

    public decimal? TaxPercent { get; set; }

    [BindNever]
    public bool IsBlank => string.IsNullOrWhiteSpace(Sku) && Quantity is null && UnitPrice is null;
}

public sealed class PurchaseFormModel
{
    public const int LineCount = 8;

    public Guid SupplierId { get; set; }

    public Guid WarehouseId { get; set; }

    public DateOnly? PurchaseDate { get; set; }

    public string? Notes { get; set; }

    public decimal? OrderDiscount { get; set; }

    public List<PurchaseLineInput> Lines { get; set; } = [];

    /// <summary><c>calculate</c>, <c>draft</c> or <c>submit</c> (the button pressed).</summary>
    public string? Submission { get; set; }

    [BindNever]
    public IReadOnlyList<SelectListItem> Suppliers { get; set; } = [];

    [BindNever]
    public IReadOnlyList<SelectListItem> Warehouses { get; set; } = [];

    [BindNever]
    public PurchaseTotalsResult? Totals { get; set; }

    public void PadLines()
    {
        while (Lines.Count < LineCount)
        {
            Lines.Add(new PurchaseLineInput());
        }
    }
}

public sealed class QuantityInput
{
    public Guid ItemId { get; set; }

    public decimal? Quantity { get; set; }
}

/// <summary>Receiving or returning: a date, a note or reason, and a quantity per purchase item.</summary>
public sealed class QuantityFormModel
{
    public DateOnly? Date { get; set; }

    public string? Notes { get; set; }

    public List<QuantityInput> Lines { get; set; } = [];

    [BindNever]
    public PurchaseDetails? Purchase { get; set; }

    public IReadOnlyList<QuantityLine> ToLines() =>
        Lines.Where(line => line.Quantity is > 0).Select(line => new QuantityLine(line.ItemId, line.Quantity!.Value)).ToList();
}

/// <summary>Finds the product or variant a typed SKU means and builds the purchase line command.</summary>
public sealed class ProductLineResolver(IProductService products)
{
    public async Task<(PurchaseItemCommand? Item, string? Error)> ResolveAsync(int number, PurchaseLineInput line, CancellationToken cancellationToken)
    {
        var sku = line.Sku?.Trim();
        if (string.IsNullOrEmpty(sku))
        {
            return (null, $"Line {number}: enter a SKU.");
        }

        if (line.Quantity is not > 0)
        {
            return (null, $"Line {number}: enter a quantity above zero.");
        }

        if (line.UnitPrice is null or < 0)
        {
            return (null, $"Line {number}: enter the purchase price.");
        }

        var found = await products.ListAsync(new ProductListQuery { Search = sku, PageSize = 25 }, cancellationToken);
        if (found.IsFailure)
        {
            return (null, found.Error.Description);
        }

        var simple = found.Value.Items.FirstOrDefault(product => string.Equals(product.Sku, sku, StringComparison.OrdinalIgnoreCase));
        if (simple is { HasVariants: true })
        {
            return (null, $"Line {number}: '{sku}' has variants; enter a variant SKU.");
        }

        Guid? variantId = null;
        string? variantName = null;
        ProductSummary? product = simple;
        if (product is null)
        {
            foreach (var candidate in found.Value.Items.Where(item => item.HasVariants))
            {
                var variants = await products.ListVariantsAsync(candidate.Id, cancellationToken);
                if (variants.IsSuccess && variants.Value.FirstOrDefault(variant => string.Equals(variant.Sku, sku, StringComparison.OrdinalIgnoreCase)) is { } variant)
                {
                    (product, variantId, variantName) = (candidate, variant.Id, variant.Name);
                    break;
                }
            }
        }

        if (product is null)
        {
            return (null, $"Line {number}: no product or variant with SKU '{sku}'.");
        }

        var name = variantName is null ? product.Name : $"{product.Name} - {variantName}";
        return (new PurchaseItemCommand(product.Id, variantId, sku, name, product.BaseUnitCode, line.Quantity.Value, line.UnitPrice.Value, line.DiscountPercent ?? 0, line.TaxPercent ?? 0), null);
    }
}
