using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Inventory;

/// <summary>
/// <see cref="IStockPostingService"/>: how Sales, Purchases and returns will move stock. A thin layer over <see cref="StockLedger"/>,
/// so a posting by another module obeys exactly the rules adjustments and transfers do.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> the organization is <c>ICurrentUser.OrganizationId</c>; the request has no organization field. The
/// warehouse, products, variants and batches must belong to it (checked, and enforced by composite foreign keys).</item>
/// <item><b>Idempotency:</b> a source line that already posted the same movement is refused up front (<c>Stock.AlreadyPosted</c>); the
/// unique index <c>IX_StockMovements_SourceLine</c> is the guarantee when two requests race, which then answers
/// <c>Stock.ConcurrentChange</c> and the retry meets the up-front check.</item>
/// <item><b>Staging</b> adds balance changes and movements to the shared context and saves nothing; the caller commits them with
/// its own document. <b>Posting</b> and <b>Reversing</b> wrap the same work in a transaction of their own.</item>
/// <item><b>Reversal</b> writes opposite movements that point at the originals; a movement is reversed once (unique index). It
/// works for products, variants and warehouses retired since. Adjustments and transfers are corrected by new documents.</item>
/// <item>Expected failures are <see cref="Result"/> values logged at Information; races are warnings; anything else is unexpected and
/// left to the API exception handler. Logs contain ids and counts only.</item>
/// </list>
/// </remarks>
internal sealed class StockPostingService(
    AppDbContext db,
    ICurrentUser currentUser,
    StockLedger ledger,
    ILogger<StockPostingService> logger) : IStockPostingService
{
    // ---- Posting ------------------------------------------------------------------------------------------

    public async Task<Result<StockPostingResult>> StageAsync(StockPostingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (StockPostingRules.Validate(request) is { } invalid)
        {
            logger.LogInformation("User {ActorId} sent an invalid stock posting ({Code}).", currentUser.UserId, invalid.Code);
            return invalid;
        }

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (await ledger.CheckWarehouseAsync(organizationId, request.WarehouseId, cancellationToken) is { IsFailure: true } badWarehouse)
        {
            return badWarehouse.Error;
        }

        var type = StockPostingRules.MovementType(request.Kind);
        if (await AlreadyPostedAsync(organizationId, request, type, cancellationToken))
        {
            logger.LogInformation("Stock posting of {SourceType} {SourceId} in organization {OrganizationId} refused: it was posted already.", request.SourceType, request.SourceId, organizationId);
            return InventoryErrors.AlreadyPosted;
        }

        // Every line is checked before any is staged, so an invalid request leaves the unit of work untouched.
        var lines = new List<(StockPostingLine Line, StockLedger.StockLine Resolved)>(request.Lines.Count);
        foreach (var line in request.Lines)
        {
            var resolved = await ledger.ResolveAsync(organizationId, line.ProductId, line.VariantId, line.BatchId, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error;
            }

            lines.Add((line, resolved.Value));
        }

        var posted = new List<PostedMovement>(lines.Count);
        try
        {
            foreach (var (line, resolved) in lines)
            {
                var movement = await ledger.ApplyAsync(
                    organizationId, request.WarehouseId, resolved, type, line.Quantity, request.Kind == StockPostingKind.Receipt ? line.UnitCost : null,
                    request.Reference, request.Notes, request.SourceType, request.SourceId, line.SourceLineId, cancellationToken);
                if (movement.IsFailure)
                {
                    return movement.Error; // the caller rolls its unit of work back
                }

                posted.Add(ToPosted(movement.Value));
            }
        }
        catch (DomainException exception)
        {
            logger.LogInformation("Stock posting of {SourceType} {SourceId} refused by the domain: {Reason}", request.SourceType, request.SourceId, exception.Message);
            return InventoryErrors.PostingInvalid(exception.Message);
        }

        logger.LogInformation("User {ActorId} staged {MovementCount} {Kind} movement(s) for {SourceType} {SourceId} in organization {OrganizationId}.",
            currentUser.UserId, posted.Count, request.Kind, request.SourceType, request.SourceId, organizationId);
        return new StockPostingResult(posted);
    }

    public Task<Result<StockPostingResult>> PostAsync(StockPostingRequest request, CancellationToken cancellationToken = default) =>
        InOwnTransactionAsync(() => StageAsync(request, cancellationToken), request?.SourceType, request?.SourceId, cancellationToken);

    // ---- Reversal -----------------------------------------------------------------------------------------

    public async Task<Result<StockPostingResult>> StageReversalAsync(string sourceType, Guid sourceId, string? notes = null, CancellationToken cancellationToken = default)
    {
        if (sourceType is not null && new[] { StockSourceTypes.Adjustment, StockSourceTypes.Transfer, StockSourceTypes.Manual }.Contains(sourceType, StringComparer.OrdinalIgnoreCase))
        {
            return InventoryErrors.SourceNotReversible;
        }

        if (!StockPostingRules.IsValidSourceType(sourceType) || sourceId == Guid.Empty)
        {
            return InventoryErrors.PostingInvalid("Name the source document that posted the stock.");
        }

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;

        // What the document posted (its reversals excluded), newest first so the stock is unwound in the opposite order.
        var originals = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.OrganizationId == organizationId && movement.SourceType == sourceType && movement.SourceId == sourceId && movement.ReversesMovementId == null)
            .OrderByDescending(movement => movement.OccurredAtUtc).ThenByDescending(movement => movement.Id)
            .ToListAsync(cancellationToken);
        if (originals.Count == 0)
        {
            logger.LogInformation("User {ActorId} asked to reverse {SourceType} {SourceId}, which posted nothing in organization {OrganizationId}.", currentUser.UserId, sourceType, sourceId, organizationId);
            return InventoryErrors.SourceNotFound;
        }

        var originalIds = originals.Select(movement => movement.Id).ToList();
        var reversed = (await db.StockMovements.AsNoTracking()
                .Where(movement => movement.OrganizationId == organizationId && movement.ReversesMovementId != null && originalIds.Contains(movement.ReversesMovementId.Value))
                .Select(movement => movement.ReversesMovementId!.Value)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        reversed.UnionWith(db.StockMovements.Local.Where(movement => movement.ReversesMovementId != null).Select(movement => movement.ReversesMovementId!.Value));

        var pending = originals.Where(movement => !reversed.Contains(movement.Id)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Reversal of {SourceType} {SourceId} in organization {OrganizationId} refused: everything is reversed already.", sourceType, sourceId, organizationId);
            return InventoryErrors.AlreadyReversed;
        }

        var posted = new List<PostedMovement>(pending.Count);
        try
        {
            foreach (var original in pending)
            {
                var line = await ledger.ResolveHistoricalAsync(organizationId, original.ProductId, original.ProductVariantId, original.BatchId, cancellationToken);
                if (line.IsFailure)
                {
                    return line.Error;
                }

                // Stock that came in goes out again; stock that left comes back at the cost it left with.
                var type = original.Quantity > 0 ? StockMovementType.ReversalDecrease : StockMovementType.ReversalIncrease;
                var movement = await ledger.ApplyAsync(
                    organizationId, original.WarehouseId, line.Value, type, Math.Abs(original.Quantity), type == StockMovementType.ReversalIncrease ? original.UnitCost : null,
                    original.Reference, notes ?? "Reversal", sourceType, sourceId, original.SourceLineId, cancellationToken, reversesMovementId: original.Id);
                if (movement.IsFailure)
                {
                    return movement.Error;
                }

                posted.Add(ToPosted(movement.Value));
            }
        }
        catch (DomainException exception)
        {
            return InventoryErrors.PostingInvalid(exception.Message);
        }

        logger.LogInformation("User {ActorId} staged the reversal of {MovementCount} movement(s) of {SourceType} {SourceId} in organization {OrganizationId}.",
            currentUser.UserId, posted.Count, sourceType, sourceId, organizationId);
        return new StockPostingResult(posted);
    }

    public Task<Result<StockPostingResult>> ReverseAsync(string sourceType, Guid sourceId, string? notes = null, CancellationToken cancellationToken = default) =>
        InOwnTransactionAsync(() => StageReversalAsync(sourceType, sourceId, notes, cancellationToken), sourceType, sourceId, cancellationToken);

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="stage"/> and saves it in one transaction; a lost race (two requests posting the same line) is a conflict, not a crash.</summary>
    private async Task<Result<StockPostingResult>> InOwnTransactionAsync(
        Func<Task<Result<StockPostingResult>>> stage, string? sourceType, Guid? sourceId, CancellationToken cancellationToken)
    {
        StockPostingResult? result = null;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var staged = await stage();
                if (staged.IsFailure)
                {
                    return staged.Error;
                }

                await db.SaveChangesAsync(cancellationToken);
                result = staged.Value;
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Stock posting of {SourceType} {SourceId} by user {ActorId} lost a race with a concurrent request; nothing was changed.", sourceType, sourceId, currentUser.UserId);
            return InventoryErrors.StockConcurrentChange;
        }

        return outcome.IsFailure ? outcome.Error : result!;
    }

    /// <summary>Whether any line already posted this movement: in the database, or staged earlier in the same unit of work.</summary>
    private async Task<bool> AlreadyPostedAsync(Guid organizationId, StockPostingRequest request, StockMovementType type, CancellationToken cancellationToken)
    {
        var stored = await db.StockMovements.AsNoTracking()
            .Where(movement => movement.OrganizationId == organizationId && movement.SourceType == request.SourceType && movement.SourceId == request.SourceId
                && movement.Type == type && movement.WarehouseId == request.WarehouseId)
            .Select(movement => new { movement.SourceLineId, movement.ProductId, movement.ProductVariantId, movement.BatchId })
            .ToListAsync(cancellationToken);
        var staged = db.StockMovements.Local
            .Where(movement => movement.OrganizationId == organizationId && string.Equals(movement.SourceType, request.SourceType, StringComparison.OrdinalIgnoreCase)
                && movement.SourceId == request.SourceId && movement.Type == type && movement.WarehouseId == request.WarehouseId)
            .Select(movement => new { movement.SourceLineId, movement.ProductId, movement.ProductVariantId, movement.BatchId });

        var existing = stored.Concat(staged).ToList();
        return request.Lines.Any(line => existing.Any(known =>
            known.SourceLineId == line.SourceLineId && known.ProductId == line.ProductId && known.ProductVariantId == line.VariantId && known.BatchId == line.BatchId));
    }

    private static PostedMovement ToPosted(StockMovement movement) => new(
        movement.Id, movement.SourceLineId ?? Guid.Empty, movement.ProductId, movement.ProductVariantId, movement.BatchId,
        movement.Type, movement.Quantity, movement.QuantityAfter, movement.UnitCost);
}
