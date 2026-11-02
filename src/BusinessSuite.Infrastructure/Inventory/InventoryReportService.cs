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
/// <see cref="IInventoryReportService"/> on SQL Server. Tenant isolation: every query is filtered by
/// <c>ICurrentUser.OrganizationId</c>. The dashboard reuses the stock and batch services for its lists, so it always agrees
/// with them on what "low stock", "out of stock" and "near expiry" mean. Read-only. Logs name user and organization ids only.
/// </summary>
internal sealed class InventoryReportService(
    AppDbContext db,
    ICurrentUser currentUser,
    IStockService stock,
    IBatchService batches,
    TimeProvider timeProvider,
    ILogger<InventoryReportService> logger) : IInventoryReportService
{
    private const int NearExpiryDays = 30;
    private const int DashboardTopItems = 5;
    private const int DashboardRecentMovements = 10;

    public async Task<Result<InventoryDashboard>> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var limit = today.AddDays(NearExpiryDays);

        // The context runs one query at a time, so these are awaited one after another.
        var totalProducts = await db.Products.CountAsync(product => product.OrganizationId == organizationId, cancellationToken);
        var activeProducts = await db.Products.CountAsync(product => product.OrganizationId == organizationId && product.IsActive, cancellationToken);
        var totalVariants = await db.ProductVariants.CountAsync(variant => variant.OrganizationId == organizationId, cancellationToken);
        var totalWarehouses = await db.Warehouses.CountAsync(warehouse => warehouse.OrganizationId == organizationId, cancellationToken);
        var value = await db.InventoryBalances.Where(balance => balance.OrganizationId == organizationId && balance.QuantityOnHand > 0)
            .SumAsync(balance => (decimal?)(balance.QuantityOnHand * balance.AverageCost), cancellationToken) ?? 0;

        var items = StockQueries.Items(db, organizationId, null);
        var lowStockCount = await items.CountAsync(item => item.ReorderLevel != null && item.OnHand <= item.ReorderLevel, cancellationToken);
        var outOfStockCount = await items.CountAsync(item => item.OnHand <= 0, cancellationToken);

        var heldBatches = db.ProductBatches.Where(batch => batch.OrganizationId == organizationId && batch.ExpiryDate != null
            && db.InventoryBalances.Any(balance => balance.BatchId == batch.Id && balance.QuantityOnHand > 0));
        var expiredBatchCount = await heldBatches.CountAsync(batch => batch.ExpiryDate < today, cancellationToken);
        var nearExpiryBatchCount = await heldBatches.CountAsync(batch => batch.ExpiryDate >= today && batch.ExpiryDate <= limit, cancellationToken);

        var low = await stock.ListLowStockAsync(new StockItemQuery { PageSize = DashboardTopItems }, cancellationToken);
        var outOfStock = await stock.ListOutOfStockAsync(new StockItemQuery { PageSize = DashboardTopItems }, cancellationToken);
        var recent = await stock.ListMovementsAsync(new StockMovementQuery { PageSize = DashboardRecentMovements }, cancellationToken);
        var nearExpiry = await batches.ListAsync(new BatchListQuery { ExpiringWithinDays = NearExpiryDays, IsActive = true, PageSize = DashboardTopItems }, cancellationToken);

        foreach (var part in new Result[] { low, outOfStock, recent, nearExpiry })
        {
            if (part.IsFailure)
            {
                return part.Error;
            }
        }

        logger.LogInformation("User {ActorId} opened the inventory dashboard of organization {OrganizationId}.", currentUser.UserId, organizationId);
        return new InventoryDashboard(
            totalProducts, activeProducts, totalVariants, totalWarehouses, value, lowStockCount, outOfStockCount,
            nearExpiryBatchCount, expiredBatchCount, NearExpiryDays,
            low.Value.Items, outOfStock.Value.Items, recent.Value.Items, nearExpiry.Value.Items);
    }

    public async Task<Result<InventoryValuation>> GetValuationAsync(Guid? warehouseId, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (warehouseId is { } id)
        {
            if (!await db.Warehouses.AnyAsync(warehouse => warehouse.Id == id && warehouse.OrganizationId == organizationId, cancellationToken))
            {
                logger.LogInformation("User {ActorId} asked for a valuation of warehouse {WarehouseId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
                return InventoryErrors.WarehouseNotFound;
            }
        }

        var summaries = await SummariesAsync(organizationId, warehouseId, cancellationToken);
        return new InventoryValuation(summaries.Sum(summary => summary.Value), summaries.Sum(summary => summary.QuantityOnHand), summaries);
    }

    public async Task<Result<IReadOnlyList<WarehouseStockSummary>>> GetStockByWarehouseAsync(CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        return await SummariesAsync(acting.Value, null, cancellationToken);
    }

    public async Task<Result<PagedResult<StockItemDetails>>> GetStockByProductAsync(StockItemQuery query, CancellationToken cancellationToken = default)
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
            return InventoryErrors.WarehouseNotFound;
        }

        var items = StockQueries.Items(db, organizationId, query.WarehouseId);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            items = items.Where(item => item.Name.Contains(search) || item.Sku.Contains(search));
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await items.CountAsync(cancellationToken);
        var rows = await items.OrderBy(item => item.Name).ThenBy(item => item.Sku).ThenBy(item => item.ProductId).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<StockItemDetails>(rows.ConvertAll(row => row.ToDetails()), page, totalCount);
    }

    /// <summary>What each warehouse (or the one asked for) holds and is worth; warehouses with no stock appear with zeros.</summary>
    private async Task<List<WarehouseStockSummary>> SummariesAsync(Guid organizationId, Guid? warehouseId, CancellationToken cancellationToken)
    {
        var warehouses = db.Warehouses.AsNoTracking().Where(warehouse => warehouse.OrganizationId == organizationId && (warehouseId == null || warehouse.Id == warehouseId));
        return await warehouses.OrderBy(warehouse => warehouse.Name).ThenBy(warehouse => warehouse.Id)
            .Select(warehouse => new WarehouseStockSummary(
                warehouse.Id, warehouse.Code, warehouse.Name, warehouse.IsActive,
                db.InventoryBalances.Count(balance => balance.WarehouseId == warehouse.Id && balance.QuantityOnHand > 0),
                db.InventoryBalances.Where(balance => balance.WarehouseId == warehouse.Id).Sum(balance => (decimal?)balance.QuantityOnHand) ?? 0,
                db.InventoryBalances.Where(balance => balance.WarehouseId == warehouse.Id).Sum(balance => (decimal?)balance.QuantityReserved) ?? 0,
                db.InventoryBalances.Where(balance => balance.WarehouseId == warehouse.Id && balance.QuantityOnHand > 0)
                    .Sum(balance => (decimal?)(balance.QuantityOnHand * balance.AverageCost)) ?? 0))
            .ToListAsync(cancellationToken);
    }
}
