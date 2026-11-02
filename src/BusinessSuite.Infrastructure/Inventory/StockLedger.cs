using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Infrastructure.Inventory;

/// <summary>
/// The one place stock changes: checks that a stock line makes sense, changes the balance and adds the ledger movement to the
/// SAME <see cref="AppDbContext"/>, so the caller's single <c>SaveChanges</c> (inside <c>DatabaseTransactions.RunAsync</c>)
/// commits balance, movement and document together. Nothing here saves.
/// </summary>
/// <remarks>
/// Rules enforced for every stock line: the product exists in the organization, is active and tracks stock; a product with
/// variants needs one of ITS active variants, a product without none; a product that tracks batches needs one of ITS active
/// batches (matching the variant), others none; the quantity fits the unit's decimal places; stock never goes below zero
/// (nor into reserved stock) unless the product allows negative stock.
/// </remarks>
internal sealed class StockLedger(AppDbContext db, TimeProvider timeProvider)
{
    /// <summary>A checked stock line: what is moved and the rules that apply to it.</summary>
    public sealed record StockLine(
        Guid ProductId, Guid? VariantId, Guid? BatchId, string Sku, string Label, int DecimalPlaces, bool AllowNegative);

    /// <summary>Checks a warehouse of this organization exists and is active.</summary>
    public async Task<Result> CheckWarehouseAsync(Guid organizationId, Guid warehouseId, CancellationToken cancellationToken)
    {
        var warehouse = await db.Warehouses.AsNoTracking().Where(candidate => candidate.Id == warehouseId && candidate.OrganizationId == organizationId)
            .Select(candidate => new { candidate.IsActive }).SingleOrDefaultAsync(cancellationToken);
        if (warehouse is null)
        {
            return InventoryErrors.WarehouseNotFound;
        }

        return warehouse.IsActive ? Result.Success() : InventoryErrors.WarehouseInactive;
    }

    /// <summary>Checks a product / variant / batch combination and returns the line with the rules that apply to it.</summary>
    public async Task<Result<StockLine>> ResolveAsync(Guid organizationId, Guid productId, Guid? variantId, Guid? batchId, CancellationToken cancellationToken)
    {
        var product = await (
                from candidate in db.Products.AsNoTracking()
                where candidate.Id == productId && candidate.OrganizationId == organizationId
                join unit in db.UnitsOfMeasure on candidate.BaseUnitId equals unit.Id
                select new
                {
                    candidate.Sku, candidate.Name, candidate.IsActive, candidate.TracksStock, candidate.HasVariants, candidate.TracksBatches,
                    candidate.AllowNegativeStock, unit.DecimalPlaces,
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return InventoryErrors.StockItemInvalid("A product on the document does not exist in your organization.");
        }

        var label = product.Sku;
        if (!product.IsActive)
        {
            return InventoryErrors.StockItemInvalid($"Product {label} is retired.");
        }

        if (!product.TracksStock)
        {
            return InventoryErrors.StockItemInvalid($"Product {label} does not track stock.");
        }

        if (product.HasVariants)
        {
            if (variantId is not { } chosenVariant)
            {
                return InventoryErrors.StockItemInvalid($"Product {label} has variants: name the variant.");
            }

            var variant = await db.ProductVariants.AsNoTracking()
                .Where(candidate => candidate.Id == chosenVariant && candidate.ProductId == productId && candidate.OrganizationId == organizationId)
                .Select(candidate => new { candidate.Sku, candidate.IsActive }).SingleOrDefaultAsync(cancellationToken);
            if (variant is null)
            {
                return InventoryErrors.StockItemInvalid($"A variant on the document is not a variant of product {label}.");
            }

            if (!variant.IsActive)
            {
                return InventoryErrors.StockItemInvalid($"Variant {variant.Sku} is retired.");
            }

            label = variant.Sku;
        }
        else if (variantId is not null)
        {
            return InventoryErrors.StockItemInvalid($"Product {label} has no variants.");
        }

        if (product.TracksBatches)
        {
            if (batchId is not { } chosenBatch)
            {
                return InventoryErrors.StockItemInvalid($"Product {label} tracks batches: name the batch.");
            }

            var batch = await db.ProductBatches.AsNoTracking()
                .Where(candidate => candidate.Id == chosenBatch && candidate.ProductId == productId && candidate.OrganizationId == organizationId)
                .Select(candidate => new { candidate.BatchNumber, candidate.IsActive, candidate.ProductVariantId }).SingleOrDefaultAsync(cancellationToken);
            if (batch is null || batch.ProductVariantId != variantId)
            {
                return InventoryErrors.StockItemInvalid($"A batch on the document is not a batch of {label}.");
            }

            if (!batch.IsActive)
            {
                return InventoryErrors.StockItemInvalid($"Batch {batch.BatchNumber} of {label} is closed.");
            }
        }
        else if (batchId is not null)
        {
            return InventoryErrors.StockItemInvalid($"Product {label} does not track batches.");
        }

        return new StockLine(productId, variantId, batchId, product.Sku, label, product.DecimalPlaces, product.AllowNegativeStock);
    }

    /// <summary>
    /// Like <see cref="ResolveAsync"/> for a movement that already happened (a reversal): the product, variant and batch must still
    /// exist in the organization, but they may have been retired or closed since; correcting history must not depend on that.
    /// </summary>
    public async Task<Result<StockLine>> ResolveHistoricalAsync(Guid organizationId, Guid productId, Guid? variantId, Guid? batchId, CancellationToken cancellationToken)
    {
        var product = await (
                from candidate in db.Products.AsNoTracking()
                where candidate.Id == productId && candidate.OrganizationId == organizationId
                join unit in db.UnitsOfMeasure on candidate.BaseUnitId equals unit.Id
                select new { candidate.Sku, candidate.AllowNegativeStock, unit.DecimalPlaces })
            .SingleOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return InventoryErrors.StockItemInvalid("A product of the document no longer exists in your organization.");
        }

        var label = product.Sku;
        if (variantId is { } variant)
        {
            var variantSku = await db.ProductVariants.AsNoTracking()
                .Where(candidate => candidate.Id == variant && candidate.ProductId == productId && candidate.OrganizationId == organizationId)
                .Select(candidate => candidate.Sku).SingleOrDefaultAsync(cancellationToken);
            if (variantSku is null)
            {
                return InventoryErrors.StockItemInvalid($"A variant of product {label} no longer exists in your organization.");
            }

            label = variantSku;
        }

        return new StockLine(productId, variantId, batchId, product.Sku, label, product.DecimalPlaces, product.AllowNegativeStock);
    }

    /// <summary>
    /// Changes the balance of <paramref name="line"/> at <paramref name="warehouseId"/> and adds the ledger movement for it.
    /// <paramref name="quantity"/> is positive; the movement <paramref name="type"/> decides the direction.
    /// </summary>
    /// <returns>The movement (added to the context, not saved), or <see cref="InventoryErrors.InsufficientStock"/>.</returns>
    /// <exception cref="Domain.Common.DomainException">A value the domain refuses (e.g. a reference that is too long): the caller reports it.</exception>
    public async Task<Result<StockMovement>> ApplyAsync(
        Guid organizationId, Guid warehouseId, StockLine line, StockMovementType type, decimal quantity, decimal? unitCost,
        string? reference, string? notes, string sourceType, Guid sourceId, Guid? sourceLineId, CancellationToken cancellationToken,
        Guid? reversesMovementId = null)
    {
        if (StockRules.CheckQuantity(quantity, line.DecimalPlaces) is { } problem)
        {
            return InventoryErrors.StockItemInvalid($"{line.Label}: {problem}");
        }

        var balance = await GetOrOpenBalanceAsync(organizationId, warehouseId, line, cancellationToken);
        var now = timeProvider.GetUtcNow();
        decimal? movementCost;
        if (StockMovement.IncreasesStock(type))
        {
            balance.Increase(quantity, unitCost, now);
            movementCost = unitCost;
        }
        else
        {
            if (!balance.CanDecrease(quantity, line.AllowNegative))
            {
                return InventoryErrors.InsufficientStock(line.Label, Math.Max(balance.QuantityAvailable, 0));
            }

            movementCost = balance.AverageCost; // stock leaves at the average cost
            balance.Decrease(quantity, line.AllowNegative, now);
        }

        var movement = StockMovement.Create(
            organizationId, warehouseId, line.ProductId, line.VariantId, line.BatchId, type, quantity, balance.QuantityOnHand, now,
            movementCost, reference: reference, notes: notes, sourceType: sourceType, sourceId: sourceId, sourceLineId: sourceLineId,
            reversesMovementId: reversesMovementId);
        db.StockMovements.Add(movement);
        return movement;
    }

    /// <summary>The tracked balance of the line at the warehouse; a new empty one (added to the context) when there is none yet.</summary>
    private async Task<InventoryBalance> GetOrOpenBalanceAsync(Guid organizationId, Guid warehouseId, StockLine line, CancellationToken cancellationToken)
    {
        // A balance created earlier in this same unit of work is not in the database yet: look at what is tracked first.
        var balance = db.InventoryBalances.Local.FirstOrDefault(candidate =>
            candidate.OrganizationId == organizationId && candidate.WarehouseId == warehouseId && candidate.ProductId == line.ProductId
            && candidate.ProductVariantId == line.VariantId && candidate.BatchId == line.BatchId);

        balance ??= await db.InventoryBalances.SingleOrDefaultAsync(candidate =>
            candidate.OrganizationId == organizationId && candidate.WarehouseId == warehouseId && candidate.ProductId == line.ProductId
            && candidate.ProductVariantId == line.VariantId && candidate.BatchId == line.BatchId, cancellationToken);

        if (balance is null)
        {
            balance = InventoryBalance.Open(organizationId, warehouseId, line.ProductId, line.VariantId, line.BatchId);
            db.InventoryBalances.Add(balance);
        }

        return balance;
    }
}
