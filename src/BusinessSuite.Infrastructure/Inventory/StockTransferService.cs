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
/// <see cref="IStockTransferService"/> on SQL Server.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> every query is filtered by <c>ICurrentUser.OrganizationId</c>; both warehouses, the products,
/// variants and batches must belong to the same organization (also enforced by composite foreign keys).</item>
/// <item><b>Posting is atomic:</b> for every line the source balance is reduced and the destination balance increased, both ledger
/// movements are written, and the document moves to Received, in ONE transaction. If the source lacks stock for any line,
/// nothing at all changes.</item>
/// <item><b>Concurrency:</b> both balances and the document carry row versions; a lost race is reported as
/// <see cref="InventoryErrors.StockConcurrentChange"/> (409), logged as a warning, with no change made.</item>
/// <item>Logs contain ids and counts only.</item>
/// </list>
/// </remarks>
internal sealed class StockTransferService(
    AppDbContext db,
    ICurrentUser currentUser,
    StockLedger ledger,
    AuditTrail audit,
    TimeProvider timeProvider,
    ILogger<StockTransferService> logger) : IStockTransferService
{
    private const int MaxItems = 500;

    public async Task<Result<PagedResult<StockTransferSummary>>> ListAsync(StockTransferQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var transfers = db.StockTransfers.AsNoTracking().Where(transfer => transfer.OrganizationId == acting.Value);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            transfers = transfers.Where(transfer => transfer.Number.Contains(search));
        }

        if (query.WarehouseId is { } warehouseId)
        {
            transfers = transfers.Where(transfer => transfer.FromWarehouseId == warehouseId || transfer.ToWarehouseId == warehouseId);
        }

        if (query.Status is { } status)
        {
            transfers = transfers.Where(transfer => transfer.Status == status);
        }

        if (query.FromDate is { } fromDate)
        {
            transfers = transfers.Where(transfer => transfer.TransferDate >= fromDate);
        }

        if (query.ToDate is { } toDate)
        {
            transfers = transfers.Where(transfer => transfer.TransferDate <= toDate);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await transfers.CountAsync(cancellationToken);
        var items = await (
                from transfer in transfers.OrderByDescending(transfer => transfer.TransferDate).ThenByDescending(transfer => transfer.CreatedAtUtc)
                    .ThenByDescending(transfer => transfer.Id).Skip(page.Skip).Take(page.PageSize)
                join source in db.Warehouses on transfer.FromWarehouseId equals source.Id
                join target in db.Warehouses on transfer.ToWarehouseId equals target.Id
                select new StockTransferSummary(
                    transfer.Id, transfer.Number, source.Id, source.Name, target.Id, target.Name, transfer.TransferDate, transfer.Status,
                    db.StockTransferItems.Count(item => item.StockTransferId == transfer.Id), transfer.ReceivedAtUtc, transfer.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        items = items.OrderByDescending(item => item.TransferDate).ThenByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id).ToList();
        return new PagedResult<StockTransferSummary>(items, page, totalCount);
    }

    public async Task<Result<StockTransferDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadAsync(id, acting.Value, cancellationToken);
        if (details is null)
        {
            logger.LogInformation("User {ActorId} asked for stock transfer {TransferId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return InventoryErrors.TransferNotFound;
        }

        return details;
    }

    public async Task<Result<StockTransferDetails>> CreateAsync(CreateStockTransferCommand command, CancellationToken cancellationToken = default)
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
            return InventoryErrors.TransferInvalid("Add at least one item.");
        }

        if (items.Count > MaxItems)
        {
            return InventoryErrors.TransferInvalid($"A transfer can have at most {MaxItems} items. Split it into several.");
        }

        if (command.FromWarehouseId == command.ToWarehouseId)
        {
            return InventoryErrors.TransferInvalid("Choose two different warehouses.");
        }

        foreach (var warehouseId in new[] { command.FromWarehouseId, command.ToWarehouseId })
        {
            if (await ledger.CheckWarehouseAsync(organizationId, warehouseId, cancellationToken) is { IsFailure: true } badWarehouse)
            {
                return badWarehouse.Error;
            }
        }

        foreach (var item in items)
        {
            var line = await ledger.ResolveAsync(organizationId, item.ProductId, item.VariantId, item.BatchId, cancellationToken);
            if (line.IsFailure)
            {
                return line.Error;
            }

            if (item.Quantity <= 0)
            {
                return InventoryErrors.StockItemInvalid($"{line.Value.Label}: The quantity must be greater than zero.");
            }

            if (StockRules.CheckQuantity(item.Quantity, line.Value.DecimalPlaces) is { } problem)
            {
                return InventoryErrors.StockItemInvalid($"{line.Value.Label}: {problem}");
            }
        }

        StockTransfer transfer;
        try
        {
            var count = await db.StockTransfers.CountAsync(candidate => candidate.OrganizationId == organizationId, cancellationToken);
            transfer = StockTransfer.Create(organizationId, $"TRF-{count + 1:D6}", command.FromWarehouseId, command.ToWarehouseId,
                command.TransferDate ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime), command.Notes);
            foreach (var item in items)
            {
                transfer.AddItem(item.ProductId, item.VariantId, item.BatchId, item.Quantity);
            }
        }
        catch (DomainException exception)
        {
            return InventoryErrors.TransferInvalid(exception.Message);
        }

        db.StockTransfers.Add(transfer);
        audit.Record(AuditActions.StockTransferCreated, AuditActions.EntityTypes.StockTransfer, transfer.Id, organizationId,
            new { number = transfer.Number, fromWarehouseId = transfer.FromWarehouseId, toWarehouseId = transfer.ToWarehouseId, itemCount = items.Count });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "stock transfer", currentUser.UserId, InventoryErrors.StockConcurrentChange, InventoryErrors.StockConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} created draft stock transfer {TransferId} with {ItemCount} item(s) in organization {OrganizationId}.",
            currentUser.UserId, transfer.Id, items.Count, organizationId);
        return (await LoadAsync(transfer.Id, organizationId, cancellationToken))!;
    }

    public async Task<Result<StockTransferDetails>> PostAsync(Guid id, CancellationToken cancellationToken = default)
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
            // Both warehouses' balances, both movements per line and the document's status commit together or not at all.
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                movements = 0;
                var transfer = await db.StockTransfers.Include(candidate => candidate.Items)
                    .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
                if (transfer is null)
                {
                    return InventoryErrors.TransferNotFound;
                }

                if (transfer.Status != StockTransferStatus.Draft)
                {
                    return InventoryErrors.TransferNotDraft(transfer.Status);
                }

                foreach (var warehouseId in new[] { transfer.FromWarehouseId, transfer.ToWarehouseId })
                {
                    if (await ledger.CheckWarehouseAsync(organizationId, warehouseId, cancellationToken) is { IsFailure: true } badWarehouse)
                    {
                        return badWarehouse.Error;
                    }
                }

                foreach (var item in transfer.Items)
                {
                    var line = await ledger.ResolveAsync(organizationId, item.ProductId, item.ProductVariantId, item.BatchId, cancellationToken);
                    if (line.IsFailure)
                    {
                        return line.Error;
                    }

                    var movementOut = await ledger.ApplyAsync(organizationId, transfer.FromWarehouseId, line.Value, StockMovementType.TransferOut, item.Quantity, null,
                        transfer.Number, transfer.Notes, StockSourceTypes.Transfer, transfer.Id, item.Id, cancellationToken);
                    if (movementOut.IsFailure)
                    {
                        return movementOut.Error;
                    }

                    // The stock keeps the cost it had where it came from.
                    var cost = movementOut.Value.UnitCost ?? 0m;
                    var movementIn = await ledger.ApplyAsync(organizationId, transfer.ToWarehouseId, line.Value, StockMovementType.TransferIn, item.Quantity, cost,
                        transfer.Number, transfer.Notes, StockSourceTypes.Transfer, transfer.Id, item.Id, cancellationToken);
                    if (movementIn.IsFailure)
                    {
                        return movementIn.Error;
                    }

                    item.RecordDispatch(movementOut.Value.Id, cost);
                    item.RecordReceipt(movementIn.Value.Id, item.Quantity);
                    movements += 2;
                }

                var now = timeProvider.GetUtcNow();
                transfer.MarkDispatched(now);
                transfer.MarkReceived(now);
                audit.Record(AuditActions.StockTransferPosted, AuditActions.EntityTypes.StockTransfer, transfer.Id, organizationId,
                    new { number = transfer.Number, fromWarehouseId = transfer.FromWarehouseId, toWarehouseId = transfer.ToWarehouseId, itemCount = transfer.Items.Count, movementCount = movements });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Posting of stock transfer {TransferId} by user {ActorId} lost a race with a concurrent change; nothing was changed.", id, currentUser.UserId);
            return InventoryErrors.StockConcurrentChange;
        }
        catch (DomainException exception)
        {
            logger.LogInformation("Posting of stock transfer {TransferId} by user {ActorId} refused: {Reason}", id, currentUser.UserId, exception.Message);
            return InventoryErrors.TransferInvalid(exception.Message);
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not post stock transfer {TransferId} in organization {OrganizationId} ({Code}).", currentUser.UserId, id, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} posted stock transfer {TransferId} with {MovementCount} movement(s) in organization {OrganizationId}.", currentUser.UserId, id, movements, organizationId);
        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<StockTransferDetails>> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var transfer = await db.StockTransfers.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (transfer is null)
        {
            return InventoryErrors.TransferNotFound;
        }

        if (transfer.Status != StockTransferStatus.Draft)
        {
            logger.LogInformation("User {ActorId} could not cancel stock transfer {TransferId}: it is {Status}.", currentUser.UserId, id, transfer.Status);
            return InventoryErrors.TransferNotDraft(transfer.Status);
        }

        transfer.Cancel();
        audit.Record(AuditActions.StockTransferCancelled, AuditActions.EntityTypes.StockTransfer, transfer.Id, organizationId,
            new { number = transfer.Number, fromWarehouseId = transfer.FromWarehouseId, toWarehouseId = transfer.ToWarehouseId });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "stock transfer", currentUser.UserId, InventoryErrors.StockConcurrentChange, InventoryErrors.StockConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} cancelled stock transfer {TransferId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private async Task<StockTransferDetails?> LoadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var header = await (
                from transfer in db.StockTransfers.AsNoTracking().Where(candidate => candidate.Id == id && candidate.OrganizationId == organizationId)
                join source in db.Warehouses on transfer.FromWarehouseId equals source.Id
                join target in db.Warehouses on transfer.ToWarehouseId equals target.Id
                select new { Transfer = transfer, FromName = source.Name, ToName = target.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var items = await (
                from item in db.StockTransferItems.AsNoTracking().Where(candidate => candidate.StockTransferId == id && candidate.OrganizationId == organizationId)
                join product in db.Products on item.ProductId equals product.Id
                join variant in db.ProductVariants on item.ProductVariantId equals variant.Id into variants
                from variant in variants.DefaultIfEmpty()
                join batch in db.ProductBatches on item.BatchId equals batch.Id into batches
                from batch in batches.DefaultIfEmpty()
                orderby product.Name, item.Id
                select new StockTransferItemDetails(
                    item.Id, product.Id, product.Sku, product.Name, variant == null ? null : variant.Id, variant == null ? null : variant.Sku,
                    batch == null ? null : batch.Id, batch == null ? null : batch.BatchNumber, item.Quantity, item.ReceivedQuantity, item.UnitCost,
                    item.DispatchMovementId, item.ReceiptMovementId))
            .ToListAsync(cancellationToken);

        var stored = header.Transfer;
        return new StockTransferDetails(
            stored.Id, stored.Number, stored.FromWarehouseId, header.FromName, stored.ToWarehouseId, header.ToName, stored.TransferDate, stored.Notes,
            stored.Status, stored.DispatchedAtUtc, stored.ReceivedAtUtc, stored.CreatedAtUtc, items);
    }
}
