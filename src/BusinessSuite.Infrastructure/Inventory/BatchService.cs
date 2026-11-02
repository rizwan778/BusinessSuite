using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Inventory;

/// <summary>
/// <see cref="IBatchService"/> on SQL Server. Tenant isolation: every query is filtered by <c>ICurrentUser.OrganizationId</c>; a
/// batch belongs to a product (and variant) of the same organization (also enforced by composite foreign keys). "Today" for
/// expiry comes from the injected <see cref="TimeProvider"/> (UTC). Logs name user, organization and batch ids only.
/// </summary>
internal sealed class BatchService(AppDbContext db, ICurrentUser currentUser, AuditTrail audit, TimeProvider timeProvider, ILogger<BatchService> logger) : IBatchService
{
    public async Task<Result<PagedResult<BatchDetails>>> ListAsync(BatchListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var today = Today();
        var batches = db.ProductBatches.AsNoTracking().Where(batch => batch.OrganizationId == organizationId);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            batches = batches.Where(batch => batch.BatchNumber.Contains(search)
                || db.Products.Any(product => product.Id == batch.ProductId && (product.Name.Contains(search) || product.Sku.Contains(search))));
        }

        if (query.ProductId is { } productId)
        {
            batches = batches.Where(batch => batch.ProductId == productId);
        }

        if (query.VariantId is { } variantId)
        {
            batches = batches.Where(batch => batch.ProductVariantId == variantId);
        }

        if (query.IsActive is { } isActive)
        {
            batches = batches.Where(batch => batch.IsActive == isActive);
        }

        if (query.ExpiredOnly)
        {
            batches = batches.Where(batch => batch.ExpiryDate != null && batch.ExpiryDate < today);
        }

        if (query.ExpiringWithinDays is { } days)
        {
            var limit = today.AddDays(days);
            batches = batches.Where(batch => batch.ExpiryDate != null && batch.ExpiryDate <= limit
                && db.InventoryBalances.Any(balance => balance.BatchId == batch.Id && balance.QuantityOnHand > 0));
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await batches.CountAsync(cancellationToken);
        // Soonest expiry first (batches without an expiry date last), then batch number.
        var ordered = batches.OrderBy(batch => batch.ExpiryDate == null).ThenBy(batch => batch.ExpiryDate).ThenBy(batch => batch.BatchNumber).ThenBy(batch => batch.Id)
            .Skip(page.Skip).Take(page.PageSize);
        return new PagedResult<BatchDetails>(await ProjectAsync(ordered, today, cancellationToken), page, totalCount);
    }

    public async Task<Result<BatchDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var batch = await LoadAsync(id, acting.Value, cancellationToken);
        if (batch is null)
        {
            logger.LogInformation("User {ActorId} asked for batch {BatchId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return InventoryErrors.BatchNotFound;
        }

        return batch;
    }

    public async Task<Result<IReadOnlyList<BatchStockDetails>>> GetStockAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (!await db.ProductBatches.AnyAsync(batch => batch.Id == id && batch.OrganizationId == organizationId, cancellationToken))
        {
            return InventoryErrors.BatchNotFound;
        }

        var rows = await (
                from balance in db.InventoryBalances.AsNoTracking().Where(candidate => candidate.BatchId == id && candidate.OrganizationId == organizationId)
                join warehouse in db.Warehouses on balance.WarehouseId equals warehouse.Id
                orderby warehouse.Name
                select new BatchStockDetails(warehouse.Id, warehouse.Code, warehouse.Name, balance.QuantityOnHand, balance.QuantityReserved,
                    balance.QuantityOnHand - balance.QuantityReserved, balance.AverageCost))
            .ToListAsync(cancellationToken);
        return rows;
    }

    public async Task<Result<BatchDetails>> RegisterAsync(RegisterBatchCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var product = await db.Products.AsNoTracking().Where(candidate => candidate.Id == command.ProductId && candidate.OrganizationId == organizationId)
            .Select(candidate => new { candidate.IsActive, candidate.HasVariants, candidate.TracksBatches, candidate.RequiresExpiry })
            .SingleOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        if (!product.IsActive || !product.TracksBatches)
        {
            return InventoryErrors.BatchInvalid("Batches can only be registered for an active product that tracks batches.");
        }

        if (product.HasVariants != (command.VariantId is not null))
        {
            return InventoryErrors.BatchInvalid(product.HasVariants ? "This product has variants: name the variant the batch is of." : "This product has no variants.");
        }

        if (command.VariantId is { } variantId
            && !await db.ProductVariants.AnyAsync(variant => variant.Id == variantId && variant.ProductId == command.ProductId && variant.OrganizationId == organizationId && variant.IsActive, cancellationToken))
        {
            return InventoryErrors.BatchInvalid("The variant does not exist for this product or is retired.");
        }

        ProductBatch batch;
        try
        {
            batch = ProductBatch.Create(organizationId, command.ProductId, command.VariantId, command.BatchNumber, command.ManufactureDate, command.ExpiryDate, product.RequiresExpiry);
        }
        catch (DomainException exception)
        {
            return InventoryErrors.BatchInvalid(exception.Message);
        }

        var number = batch.BatchNumber;
        if (await db.ProductBatches.AnyAsync(other => other.OrganizationId == organizationId && other.ProductId == command.ProductId
            && other.ProductVariantId == command.VariantId && other.BatchNumber == number, cancellationToken))
        {
            logger.LogInformation("User {ActorId} could not register a batch of product {ProductId} in organization {OrganizationId}: the number is taken.", currentUser.UserId, command.ProductId, organizationId);
            return InventoryErrors.BatchDuplicate;
        }

        db.ProductBatches.Add(batch);
        audit.Record(AuditActions.BatchRegistered, AuditActions.EntityTypes.ProductBatch, batch.Id, organizationId,
            new { productId = command.ProductId, variantId = command.VariantId, batchNumber = batch.BatchNumber, expiryDate = batch.ExpiryDate });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "batch", currentUser.UserId, InventoryErrors.BatchDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} registered batch {BatchId} of product {ProductId} in organization {OrganizationId}.", currentUser.UserId, batch.Id, command.ProductId, organizationId);
        return (await LoadAsync(batch.Id, organizationId, cancellationToken))!;
    }

    public async Task<Result<BatchDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var batch = await db.ProductBatches.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (batch is null)
        {
            return InventoryErrors.BatchNotFound;
        }

        if (batch.IsActive != isActive)
        {
            batch.SetActive(isActive);
            audit.Record(isActive ? AuditActions.BatchActivated : AuditActions.BatchClosed, AuditActions.EntityTypes.ProductBatch, id, organizationId,
                new { productId = batch.ProductId, batchNumber = batch.BatchNumber });
            if (await DatabaseErrors.TrySaveAsync(db, logger, "batch", currentUser.UserId, InventoryErrors.BatchDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
            {
                return error;
            }

            logger.LogInformation("User {ActorId} {Action} batch {BatchId} in organization {OrganizationId}.", currentUser.UserId, isActive ? "reopened" : "closed", id, organizationId);
        }

        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private async Task<BatchDetails?> LoadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await ProjectAsync(db.ProductBatches.AsNoTracking().Where(batch => batch.Id == id && batch.OrganizationId == organizationId), Today(), cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Batches in the order given, with product, variant, stock and expiry status.</summary>
    private async Task<List<BatchDetails>> ProjectAsync(IQueryable<ProductBatch> batches, DateOnly today, CancellationToken cancellationToken)
    {
        var rows = await (
                from batch in batches
                join product in db.Products on batch.ProductId equals product.Id
                join variant in db.ProductVariants on batch.ProductVariantId equals variant.Id into variants
                from variant in variants.DefaultIfEmpty()
                select new
                {
                    Batch = batch,
                    ProductSku = product.Sku,
                    ProductName = product.Name,
                    VariantSku = variant == null ? null : variant.Sku,
                    OnHand = db.InventoryBalances.Where(balance => balance.BatchId == batch.Id).Sum(balance => (decimal?)balance.QuantityOnHand) ?? 0,
                })
            .ToListAsync(cancellationToken);

        // The joins can reorder rows; restore the order the caller asked for (soonest expiry first).
        return rows
            .OrderBy(row => row.Batch.ExpiryDate is null).ThenBy(row => row.Batch.ExpiryDate).ThenBy(row => row.Batch.BatchNumber, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Batch.Id)
            .Select(row => new BatchDetails(
                row.Batch.Id, row.Batch.ProductId, row.ProductSku, row.ProductName, row.Batch.ProductVariantId, row.VariantSku, row.Batch.BatchNumber,
                row.Batch.ManufactureDate, row.Batch.ExpiryDate, row.Batch.IsActive, row.Batch.IsExpiredOn(today), StockRules.DaysToExpiry(row.Batch.ExpiryDate, today),
                row.OnHand, row.Batch.CreatedAtUtc))
            .ToList();
    }
}
