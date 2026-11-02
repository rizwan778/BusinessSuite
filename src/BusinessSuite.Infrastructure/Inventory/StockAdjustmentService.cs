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
/// <see cref="IStockAdjustmentService"/> on SQL Server.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> every query is filtered by <c>ICurrentUser.OrganizationId</c>; the warehouse, products, variants
/// and batches on a document must belong to the same organization (also enforced by composite foreign keys).</item>
/// <item><b>Posting is atomic:</b> every line's balance change and ledger movement, and the document's status, are saved in ONE
/// transaction. A line that cannot be applied (not enough stock) refuses the whole document and changes nothing.</item>
/// <item><b>Concurrency:</b> balances and the document carry row versions; a lost race is reported as
/// <see cref="InventoryErrors.StockConcurrentChange"/> (409) and logged as a warning, with no change made.</item>
/// <item>Expected failures are <see cref="Result"/> values logged at Information; anything else is unexpected and left to the
/// API exception handler (logged, 500). Logs contain ids and counts only, never product names, SKUs or notes.</item>
/// </list>
/// </remarks>
internal sealed class StockAdjustmentService(
    AppDbContext db,
    ICurrentUser currentUser,
    StockLedger ledger,
    AuditTrail audit,
    TimeProvider timeProvider,
    ILogger<StockAdjustmentService> logger) : IStockAdjustmentService
{
    private const int MaxItems = 500;

    public async Task<Result<PagedResult<StockAdjustmentSummary>>> ListAsync(StockAdjustmentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var adjustments = db.StockAdjustments.AsNoTracking().Where(adjustment => adjustment.OrganizationId == acting.Value);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            adjustments = adjustments.Where(adjustment => adjustment.Number.Contains(search));
        }

        if (query.WarehouseId is { } warehouseId)
        {
            adjustments = adjustments.Where(adjustment => adjustment.WarehouseId == warehouseId);
        }

        if (query.Status is { } status)
        {
            adjustments = adjustments.Where(adjustment => adjustment.Status == status);
        }

        if (query.Reason is { } reason)
        {
            adjustments = adjustments.Where(adjustment => adjustment.Reason == reason);
        }

        if (query.FromDate is { } fromDate)
        {
            adjustments = adjustments.Where(adjustment => adjustment.AdjustmentDate >= fromDate);
        }

        if (query.ToDate is { } toDate)
        {
            adjustments = adjustments.Where(adjustment => adjustment.AdjustmentDate <= toDate);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await adjustments.CountAsync(cancellationToken);
        var items = await (
                from adjustment in adjustments.OrderByDescending(adjustment => adjustment.AdjustmentDate).ThenByDescending(adjustment => adjustment.CreatedAtUtc)
                    .ThenByDescending(adjustment => adjustment.Id).Skip(page.Skip).Take(page.PageSize)
                join warehouse in db.Warehouses on adjustment.WarehouseId equals warehouse.Id
                select new StockAdjustmentSummary(
                    adjustment.Id, adjustment.Number, warehouse.Id, warehouse.Name, adjustment.AdjustmentDate, adjustment.Reason, adjustment.Status,
                    db.StockAdjustmentItems.Count(item => item.StockAdjustmentId == adjustment.Id), adjustment.PostedAtUtc, adjustment.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        items = items.OrderByDescending(item => item.AdjustmentDate).ThenByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id).ToList();
        return new PagedResult<StockAdjustmentSummary>(items, page, totalCount);
    }

    public async Task<Result<StockAdjustmentDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadAsync(id, acting.Value, cancellationToken);
        if (details is null)
        {
            logger.LogInformation("User {ActorId} asked for stock adjustment {AdjustmentId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return InventoryErrors.AdjustmentNotFound;
        }

        return details;
    }

    public async Task<Result<StockAdjustmentDetails>> CreateAsync(CreateStockAdjustmentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (command.Items is not { Count: > 0 } items)
        {
            return InventoryErrors.AdjustmentInvalid("Add at least one item.");
        }

        if (items.Count > MaxItems)
        {
            return InventoryErrors.AdjustmentInvalid($"An adjustment can have at most {MaxItems} items. Split it into several.");
        }

        if (await ledger.CheckWarehouseAsync(organizationId, command.WarehouseId, cancellationToken) is { IsFailure: true } badWarehouse)
        {
            return badWarehouse.Error;
        }

        // Every line is checked now (not only at posting), so a bad draft is refused up front.
        foreach (var item in items)
        {
            var line = await ledger.ResolveAsync(organizationId, item.ProductId, item.VariantId, item.BatchId, cancellationToken);
            if (line.IsFailure)
            {
                return line.Error;
            }

            if (StockRules.CheckQuantity(item.QuantityDifference, line.Value.DecimalPlaces) is { } problem)
            {
                return InventoryErrors.StockItemInvalid($"{line.Value.Label}: {problem}");
            }
        }

        StockAdjustment adjustment;
        try
        {
            var count = await db.StockAdjustments.CountAsync(candidate => candidate.OrganizationId == organizationId, cancellationToken);
            adjustment = StockAdjustment.Create(organizationId, $"ADJ-{count + 1:D6}", command.WarehouseId,
                command.AdjustmentDate ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime), command.Reason, command.Notes);
            foreach (var item in items)
            {
                adjustment.AddItem(item.ProductId, item.VariantId, item.BatchId, item.QuantityDifference, item.UnitCost, item.Notes);
            }
        }
        catch (DomainException exception)
        {
            return InventoryErrors.AdjustmentInvalid(exception.Message);
        }

        db.StockAdjustments.Add(adjustment);
        audit.Record(AuditActions.StockAdjustmentCreated, AuditActions.EntityTypes.StockAdjustment, adjustment.Id, organizationId,
            new { number = adjustment.Number, warehouseId = adjustment.WarehouseId, reason = adjustment.Reason.ToString(), itemCount = items.Count });
        // A duplicate document number means two documents were created at once: the caller simply retries.
        if (await DatabaseErrors.TrySaveAsync(db, logger, "stock adjustment", currentUser.UserId, InventoryErrors.StockConcurrentChange, InventoryErrors.StockConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} created draft stock adjustment {AdjustmentId} with {ItemCount} item(s) in organization {OrganizationId}.",
            currentUser.UserId, adjustment.Id, items.Count, organizationId);
        return (await LoadAsync(adjustment.Id, organizationId, cancellationToken))!;
    }

    public async Task<Result<StockAdjustmentDetails>> PostAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        var movements = 0;
        try
        {
            // Balances, ledger movements and the document's status commit together or not at all.
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                movements = 0;
                var adjustment = await db.StockAdjustments.Include(candidate => candidate.Items)
                    .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
                if (adjustment is null)
                {
                    return InventoryErrors.AdjustmentNotFound;
                }

                if (adjustment.Status != StockAdjustmentStatus.Draft)
                {
                    return InventoryErrors.AdjustmentNotDraft(adjustment.Status);
                }

                if (await ledger.CheckWarehouseAsync(organizationId, adjustment.WarehouseId, cancellationToken) is { IsFailure: true } badWarehouse)
                {
                    return badWarehouse.Error;
                }

                foreach (var item in adjustment.Items)
                {
                    var line = await ledger.ResolveAsync(organizationId, item.ProductId, item.ProductVariantId, item.BatchId, cancellationToken);
                    if (line.IsFailure)
                    {
                        return line.Error;
                    }

                    var type = StockRules.AdjustmentType(item.QuantityDifference);
                    var movement = await ledger.ApplyAsync(
                        organizationId, adjustment.WarehouseId, line.Value, type, Math.Abs(item.QuantityDifference), item.UnitCost,
                        adjustment.Number, item.Notes ?? adjustment.Reason.ToString(), StockSourceTypes.Adjustment, adjustment.Id, item.Id, cancellationToken);
                    if (movement.IsFailure)
                    {
                        return movement.Error;
                    }

                    item.LinkMovement(movement.Value.Id);
                    movements++;
                }

                adjustment.MarkPosted(timeProvider.GetUtcNow(), currentUser.UserId);
                audit.Record(AuditActions.StockAdjustmentPosted, AuditActions.EntityTypes.StockAdjustment, adjustment.Id, organizationId,
                    new { number = adjustment.Number, warehouseId = adjustment.WarehouseId, reason = adjustment.Reason.ToString(), itemCount = adjustment.Items.Count, movementCount = movements });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Posting of stock adjustment {AdjustmentId} by user {ActorId} lost a race with a concurrent change; nothing was changed.", id, currentUser.UserId);
            return InventoryErrors.StockConcurrentChange;
        }
        catch (DomainException exception)
        {
            // Values the domain refuses that the checks above could not see (e.g. a reference too long): nothing was saved.
            logger.LogInformation("Posting of stock adjustment {AdjustmentId} by user {ActorId} refused: {Reason}", id, currentUser.UserId, exception.Message);
            return InventoryErrors.AdjustmentInvalid(exception.Message);
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not post stock adjustment {AdjustmentId} in organization {OrganizationId} ({Code}).", currentUser.UserId, id, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} posted stock adjustment {AdjustmentId} with {MovementCount} movement(s) in organization {OrganizationId}.", currentUser.UserId, id, movements, organizationId);
        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<StockAdjustmentDetails>> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var adjustment = await db.StockAdjustments.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (adjustment is null)
        {
            return InventoryErrors.AdjustmentNotFound;
        }

        if (adjustment.Status != StockAdjustmentStatus.Draft)
        {
            logger.LogInformation("User {ActorId} could not cancel stock adjustment {AdjustmentId}: it is {Status}.", currentUser.UserId, id, adjustment.Status);
            return InventoryErrors.AdjustmentNotDraft(adjustment.Status);
        }

        adjustment.Cancel();
        audit.Record(AuditActions.StockAdjustmentCancelled, AuditActions.EntityTypes.StockAdjustment, adjustment.Id, organizationId,
            new { number = adjustment.Number, warehouseId = adjustment.WarehouseId });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "stock adjustment", currentUser.UserId, InventoryErrors.StockConcurrentChange, InventoryErrors.StockConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} cancelled stock adjustment {AdjustmentId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private async Task<StockAdjustmentDetails?> LoadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var header = await (
                from adjustment in db.StockAdjustments.AsNoTracking().Where(candidate => candidate.Id == id && candidate.OrganizationId == organizationId)
                join warehouse in db.Warehouses on adjustment.WarehouseId equals warehouse.Id
                select new { Adjustment = adjustment, WarehouseName = warehouse.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var items = await (
                from item in db.StockAdjustmentItems.AsNoTracking().Where(candidate => candidate.StockAdjustmentId == id && candidate.OrganizationId == organizationId)
                join product in db.Products on item.ProductId equals product.Id
                join variant in db.ProductVariants on item.ProductVariantId equals variant.Id into variants
                from variant in variants.DefaultIfEmpty()
                join batch in db.ProductBatches on item.BatchId equals batch.Id into batches
                from batch in batches.DefaultIfEmpty()
                orderby product.Name, item.Id
                select new StockAdjustmentItemDetails(
                    item.Id, product.Id, product.Sku, product.Name, variant == null ? null : variant.Id, variant == null ? null : variant.Sku,
                    batch == null ? null : batch.Id, batch == null ? null : batch.BatchNumber, item.QuantityDifference, item.UnitCost, item.Notes, item.StockMovementId))
            .ToListAsync(cancellationToken);

        var stored = header.Adjustment;
        return new StockAdjustmentDetails(
            stored.Id, stored.Number, stored.WarehouseId, header.WarehouseName, stored.AdjustmentDate, stored.Reason, stored.Notes,
            stored.Status, stored.PostedAtUtc, stored.PostedByUserId, stored.CreatedAtUtc, items);
    }
}
