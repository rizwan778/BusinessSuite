using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Inventory;

/// <summary>
/// <see cref="IStockService"/> on SQL Server: read-only views of the balances and the ledger. Tenant isolation: every query is
/// filtered by <c>ICurrentUser.OrganizationId</c>, so other organizations' stock never appears. Logs name user and organization
/// ids only.
/// </summary>
internal sealed class StockService(AppDbContext db, ICurrentUser currentUser, ILogger<StockService> logger) : IStockService
{
    public async Task<Result<PagedResult<StockBalanceDetails>>> ListBalancesAsync(StockBalanceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var balances = db.InventoryBalances.AsNoTracking().Where(balance => balance.OrganizationId == organizationId);
        if (query.ProductId is { } productId)
        {
            balances = balances.Where(balance => balance.ProductId == productId);
        }

        if (query.VariantId is { } variantId)
        {
            balances = balances.Where(balance => balance.ProductVariantId == variantId);
        }

        if (query.WarehouseId is { } warehouseId)
        {
            balances = balances.Where(balance => balance.WarehouseId == warehouseId);
        }

        if (query.BatchId is { } batchId)
        {
            balances = balances.Where(balance => balance.BatchId == batchId);
        }

        if (query.HideEmpty)
        {
            balances = balances.Where(balance => balance.QuantityOnHand != 0);
        }

        var rows =
            from balance in balances
            join product in db.Products on balance.ProductId equals product.Id
            join warehouse in db.Warehouses on balance.WarehouseId equals warehouse.Id
            join unit in db.UnitsOfMeasure on product.BaseUnitId equals unit.Id
            join variant in db.ProductVariants on balance.ProductVariantId equals variant.Id into variants
            from variant in variants.DefaultIfEmpty()
            join batch in db.ProductBatches on balance.BatchId equals batch.Id into batches
            from batch in batches.DefaultIfEmpty()
            select new { balance, product, warehouse, unit, variant, batch };

        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            rows = rows.Where(row => row.product.Name.Contains(search) || row.product.Sku.Contains(search)
                || (row.product.Barcode != null && row.product.Barcode.Contains(search))
                || (row.variant != null && (row.variant.Sku.Contains(search) || (row.variant.Barcode != null && row.variant.Barcode.Contains(search)))));
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await rows.CountAsync(cancellationToken);
        var items = await rows
            .OrderBy(row => row.product.Name).ThenBy(row => row.variant == null ? string.Empty : row.variant.Sku).ThenBy(row => row.warehouse.Name).ThenBy(row => row.balance.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .Select(row => new StockBalanceDetails(
                row.product.Id, row.product.Sku, row.product.Name, row.variant == null ? null : row.variant.Id, row.variant == null ? null : row.variant.Sku,
                row.unit.Code, row.warehouse.Id, row.warehouse.Code, row.warehouse.Name, row.batch == null ? null : row.batch.Id,
                row.batch == null ? null : row.batch.BatchNumber, row.batch == null ? null : row.batch.ExpiryDate,
                row.balance.QuantityOnHand, row.balance.QuantityReserved, row.balance.QuantityOnHand - row.balance.QuantityReserved,
                row.balance.AverageCost, Math.Round(row.balance.QuantityOnHand * row.balance.AverageCost, 4)))
            .ToListAsync(cancellationToken);

        return new PagedResult<StockBalanceDetails>(items, page, totalCount);
    }

    public Task<Result<PagedResult<StockItemDetails>>> ListLowStockAsync(StockItemQuery query, CancellationToken cancellationToken = default) =>
        ListItemsAsync(query, item => item.ReorderLevel != null && item.OnHand <= item.ReorderLevel, cancellationToken);

    public Task<Result<PagedResult<StockItemDetails>>> ListOutOfStockAsync(StockItemQuery query, CancellationToken cancellationToken = default) =>
        ListItemsAsync(query, item => item.OnHand <= 0, cancellationToken);

    public async Task<Result<PagedResult<StockMovementDetails>>> ListMovementsAsync(StockMovementQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var movements = db.StockMovements.AsNoTracking().Where(movement => movement.OrganizationId == organizationId);
        if (query.ProductId is { } productId)
        {
            movements = movements.Where(movement => movement.ProductId == productId);
        }

        if (query.VariantId is { } variantId)
        {
            movements = movements.Where(movement => movement.ProductVariantId == variantId);
        }

        if (query.WarehouseId is { } warehouseId)
        {
            movements = movements.Where(movement => movement.WarehouseId == warehouseId);
        }

        if (query.BatchId is { } batchId)
        {
            movements = movements.Where(movement => movement.BatchId == batchId);
        }

        if (query.Type is { } type)
        {
            movements = movements.Where(movement => movement.Type == type);
        }

        if (query.FromUtc is { } from)
        {
            movements = movements.Where(movement => movement.OccurredAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            movements = movements.Where(movement => movement.OccurredAtUtc < to);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await movements.CountAsync(cancellationToken);
        var rows = await (
                from movement in movements.OrderByDescending(movement => movement.OccurredAtUtc).ThenByDescending(movement => movement.Id).Skip(page.Skip).Take(page.PageSize)
                join product in db.Products on movement.ProductId equals product.Id
                join warehouse in db.Warehouses on movement.WarehouseId equals warehouse.Id
                join variant in db.ProductVariants on movement.ProductVariantId equals variant.Id into variants
                from variant in variants.DefaultIfEmpty()
                join batch in db.ProductBatches on movement.BatchId equals batch.Id into batches
                from batch in batches.DefaultIfEmpty()
                select new StockMovementDetails(
                    movement.Id, movement.OccurredAtUtc, movement.Type, product.Id, product.Sku, product.Name,
                    variant == null ? null : variant.Id, variant == null ? null : variant.Sku, warehouse.Id, warehouse.Code, warehouse.Name,
                    batch == null ? null : batch.Id, batch == null ? null : batch.BatchNumber, movement.Quantity, movement.QuantityAfter,
                    movement.UnitCost, movement.Reference, movement.Notes, movement.SourceType, movement.SourceId, movement.ReversesMovementId,
                    movement.CreatedByUserId))
            .ToListAsync(cancellationToken);

        // The joins can reorder rows; the page is already chosen, so only the order within it is restored.
        rows = rows.OrderByDescending(row => row.OccurredAtUtc).ThenByDescending(row => row.Id).ToList();
        return new PagedResult<StockMovementDetails>(rows, page, totalCount);
    }

    private async Task<Result<PagedResult<StockItemDetails>>> ListItemsAsync(
        StockItemQuery query, System.Linq.Expressions.Expression<Func<StockItemRow, bool>> filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (query.WarehouseId is { } warehouseId
            && !await db.Warehouses.AnyAsync(warehouse => warehouse.Id == warehouseId && warehouse.OrganizationId == organizationId, cancellationToken))
        {
            logger.LogInformation("User {ActorId} asked for stock at warehouse {WarehouseId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, warehouseId, organizationId);
            return InventoryErrors.WarehouseNotFound;
        }

        var items = StockQueries.Items(db, organizationId, query.WarehouseId).Where(filter);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            items = items.Where(item => item.Name.Contains(search) || item.Sku.Contains(search));
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await items.CountAsync(cancellationToken);
        var rows = await items.OrderBy(item => item.Name).ThenBy(item => item.Sku).ThenBy(item => item.ProductId).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<StockItemDetails>(rows.ConvertAll(row => row.ToDetails()), page, totalCount);
    }
}
