using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Inventory;

/// <summary>
/// How much of one product (or variant, or batch) is in one warehouse, and what it cost on average
/// (<c>inventory.InventoryBalances</c>). It is the running total of the <see cref="StockMovement"/> ledger and changes in the
/// SAME transaction as the movement that changes it. The row version makes two people changing the same balance at once a
/// conflict instead of a lost update, which is what prevents overselling.
/// </summary>
/// <remarks>
/// Quantities are in the product's base unit. Costing is the moving weighted average: stock coming in at a cost blends into
/// <see cref="AverageCost"/>, stock going out leaves it unchanged. A balance cannot go below zero unless the product allows it.
/// </remarks>
public sealed class InventoryBalance : TenantEntity
{
    private InventoryBalance()
    {
    }

    /// <summary>The warehouse.</summary>
    public Guid WarehouseId { get; private set; }

    /// <summary>The product.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>The variant, for products with variants.</summary>
    public Guid? ProductVariantId { get; private set; }

    /// <summary>The batch, for products that track batches.</summary>
    public Guid? BatchId { get; private set; }

    /// <summary>Quantity on hand in the product's base unit.</summary>
    public decimal QuantityOnHand { get; private set; }

    /// <summary>
    /// Quantity promised to open orders and not yet issued, in the base unit. Maintained by sales orders (a later module);
    /// until then it stays 0. Reserved stock cannot be issued or adjusted away.
    /// </summary>
    public decimal QuantityReserved { get; private set; }

    /// <summary>What can still be promised or issued: on hand minus reserved.</summary>
    public decimal QuantityAvailable => QuantityOnHand - QuantityReserved;

    /// <summary>Moving weighted-average cost per base unit.</summary>
    public decimal AverageCost { get; private set; }

    /// <summary>When a movement last changed this balance.</summary>
    public DateTimeOffset? LastMovementAtUtc { get; private set; }

    /// <summary>Concurrency token, maintained by SQL Server.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>The value of the stock on hand.</summary>
    public decimal StockValue => Math.Round(QuantityOnHand * AverageCost, 4, MidpointRounding.AwayFromZero);

    /// <summary>An empty balance.</summary>
    /// <exception cref="DomainException">A missing organization, warehouse or product.</exception>
    public static InventoryBalance Open(Guid organizationId, Guid warehouseId, Guid productId, Guid? productVariantId, Guid? batchId)
    {
        DomainGuard.Present(warehouseId, "A balance needs a warehouse.");
        DomainGuard.Present(productId, "A balance needs a product.");

        var balance = new InventoryBalance { WarehouseId = warehouseId, ProductId = productId, ProductVariantId = productVariantId, BatchId = batchId };
        balance.BelongTo(organizationId);
        return balance;
    }

    /// <summary>Whether <paramref name="quantity"/> can be taken out now (reserved stock cannot).</summary>
    public bool CanDecrease(decimal quantity, bool allowNegative) =>
        quantity > 0 && (allowNegative || quantity <= QuantityAvailable);

    /// <summary>
    /// Adds stock. With a <paramref name="unitCost"/> the average cost becomes the weighted average of what was here and what
    /// came in (or just the new cost when nothing, or less than nothing, was here); without one it stays as it was.
    /// </summary>
    /// <exception cref="DomainException">A quantity that is not positive, or a negative cost.</exception>
    public void Increase(decimal quantity, decimal? unitCost, DateTimeOffset nowUtc)
    {
        DomainGuard.Require(quantity > 0 && quantity <= DomainGuard.MaxAmount, "A quantity to add must be greater than zero.");
        DomainGuard.Require(unitCost is null or (>= 0 and <= DomainGuard.MaxAmount), "A unit cost must be zero or more.");

        if (unitCost is { } cost)
        {
            AverageCost = QuantityOnHand <= 0
                ? cost
                : Math.Round((QuantityOnHand * AverageCost + quantity * cost) / (QuantityOnHand + quantity), 4, MidpointRounding.AwayFromZero);
        }

        QuantityOnHand += quantity;
        LastMovementAtUtc = nowUtc;
    }

    /// <summary>Removes stock at the current average cost.</summary>
    /// <exception cref="DomainException">A quantity that is not positive, or more than is on hand while negative stock is not allowed.</exception>
    public void Decrease(decimal quantity, bool allowNegative, DateTimeOffset nowUtc)
    {
        DomainGuard.Require(CanDecrease(quantity, allowNegative), "A quantity to remove must be greater than zero and no more than the stock on hand.");

        QuantityOnHand -= quantity;
        LastMovementAtUtc = nowUtc;
    }
}
