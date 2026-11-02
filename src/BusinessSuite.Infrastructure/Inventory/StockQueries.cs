using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Infrastructure.Inventory;

/// <summary>One stockable item (a simple product, or one variant) with its stock totals.</summary>
internal sealed class StockItemRow
{
    public Guid ProductId { get; init; }

    public Guid? VariantId { get; init; }

    public string Sku { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string UnitCode { get; init; } = string.Empty;

    public decimal? ReorderLevel { get; init; }

    public decimal OnHand { get; init; }

    public decimal Reserved { get; init; }

    public decimal Value { get; init; }

    public StockItemDetails ToDetails() => new(
        ProductId, VariantId, Sku, Name, UnitCode, ReorderLevel, OnHand, Reserved, OnHand - Reserved, Value,
        StockRules.IsLow(OnHand, ReorderLevel), StockRules.IsOutOfStock(OnHand));
}

/// <summary>Queries shared by the stock service and the reports, so they always agree on what "low" and "out of stock" mean.</summary>
internal static class StockQueries
{
    /// <summary>
    /// Every active, stock-tracking item of the organization (simple products and the variants of products with variants), with
    /// the stock held at <paramref name="warehouseId"/> (or in total). Items without any balance row appear with zero stock.
    /// </summary>
    public static IQueryable<StockItemRow> Items(AppDbContext db, Guid organizationId, Guid? warehouseId)
    {
        var balances = db.InventoryBalances.Where(balance => balance.OrganizationId == organizationId && (warehouseId == null || balance.WarehouseId == warehouseId));

        var simple =
            from product in db.Products
            where product.OrganizationId == organizationId && product.IsActive && product.TracksStock && !product.HasVariants
            join unit in db.UnitsOfMeasure on product.BaseUnitId equals unit.Id
            select new StockItemRow
            {
                ProductId = product.Id,
                VariantId = null,
                Sku = product.Sku,
                Name = product.Name,
                UnitCode = unit.Code,
                ReorderLevel = product.ReorderLevel,
                OnHand = balances.Where(balance => balance.ProductId == product.Id).Sum(balance => (decimal?)balance.QuantityOnHand) ?? 0,
                Reserved = balances.Where(balance => balance.ProductId == product.Id).Sum(balance => (decimal?)balance.QuantityReserved) ?? 0,
                Value = balances.Where(balance => balance.ProductId == product.Id).Sum(balance => (decimal?)(balance.QuantityOnHand * balance.AverageCost)) ?? 0,
            };

        var variants =
            from variant in db.ProductVariants
            where variant.OrganizationId == organizationId && variant.IsActive
            join product in db.Products on variant.ProductId equals product.Id
            where product.IsActive && product.TracksStock && product.HasVariants
            join unit in db.UnitsOfMeasure on product.BaseUnitId equals unit.Id
            select new StockItemRow
            {
                ProductId = product.Id,
                VariantId = variant.Id,
                Sku = variant.Sku,
                Name = variant.Name == null ? product.Name : product.Name + " - " + variant.Name,
                UnitCode = unit.Code,
                ReorderLevel = variant.ReorderLevel,
                OnHand = balances.Where(balance => balance.ProductVariantId == variant.Id).Sum(balance => (decimal?)balance.QuantityOnHand) ?? 0,
                Reserved = balances.Where(balance => balance.ProductVariantId == variant.Id).Sum(balance => (decimal?)balance.QuantityReserved) ?? 0,
                Value = balances.Where(balance => balance.ProductVariantId == variant.Id).Sum(balance => (decimal?)(balance.QuantityOnHand * balance.AverageCost)) ?? 0,
            };

        return simple.Concat(variants);
    }
}
