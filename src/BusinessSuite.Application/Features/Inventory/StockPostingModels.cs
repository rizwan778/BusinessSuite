using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Inventory;

namespace BusinessSuite.Application.Features.Inventory;

/// <summary>What a posting does to stock.</summary>
public enum StockPostingKind
{
    /// <summary>Stock comes in (a purchase receipt, a customer return). Becomes <see cref="StockMovementType.Receipt"/>.</summary>
    Receipt = 1,

    /// <summary>Stock goes out (a sale, a consumption). Becomes <see cref="StockMovementType.Issue"/>.</summary>
    Issue = 2,
}

/// <summary>One line of a posting.</summary>
/// <param name="SourceLineId">The line of the source document this movement belongs to. With the source document it is what makes a posting idempotent.</param>
/// <param name="ProductId">The product.</param>
/// <param name="VariantId">The variant, for products with variants.</param>
/// <param name="BatchId">The batch, for products that track batches.</param>
/// <param name="Quantity">Quantity in the product's BASE unit, greater than zero (the direction comes from the <see cref="StockPostingKind"/>).</param>
/// <param name="UnitCost">Cost per base unit of stock coming in; it blends into the moving average. Ignored for issues, which leave at the average cost.</param>
public sealed record StockPostingLine(Guid SourceLineId, Guid ProductId, Guid? VariantId, Guid? BatchId, decimal Quantity, decimal? UnitCost = null);

/// <summary>
/// A request to move stock on behalf of another module's document. There is deliberately NO organization in it: the posting always
/// happens in the organization the current request acts in (<c>ICurrentUser.OrganizationId</c>, verified on the server).
/// </summary>
/// <param name="SourceType">What kind of document asks, e.g. <c>SalesInvoice</c> or <c>PurchaseReceipt</c>: letters, digits and underscores. The inventory module's own <c>StockAdjustment</c>/<c>StockTransfer</c>/<c>Manual</c> are not available.</param>
/// <param name="SourceId">The document that asks. Stock is posted once per source line.</param>
/// <param name="WarehouseId">The warehouse the stock moves in or out of.</param>
/// <param name="Kind">In or out.</param>
/// <param name="Lines">The lines (at most <see cref="StockPostingRules.MaxLines"/>).</param>
/// <param name="Reference">The document number, shown in the ledger.</param>
/// <param name="Notes">Free text, shown in the ledger.</param>
public sealed record StockPostingRequest(
    string SourceType, Guid SourceId, Guid WarehouseId, StockPostingKind Kind, IReadOnlyList<StockPostingLine> Lines, string? Reference = null, string? Notes = null);

/// <summary>A ledger movement written by a posting.</summary>
/// <param name="MovementId">The movement's id in the ledger.</param>
/// <param name="SourceLineId">The source line it belongs to.</param>
/// <param name="ProductId">The product.</param>
/// <param name="VariantId">The variant, for products with variants.</param>
/// <param name="BatchId">The batch, for products that track batches.</param>
/// <param name="Type">What it is in the ledger.</param>
/// <param name="Quantity">Signed quantity in base units.</param>
/// <param name="QuantityAfter">The quantity on hand after it.</param>
/// <param name="UnitCost">The cost per unit it carried.</param>
public sealed record PostedMovement(
    Guid MovementId, Guid SourceLineId, Guid ProductId, Guid? VariantId, Guid? BatchId, StockMovementType Type, decimal Quantity, decimal QuantityAfter, decimal? UnitCost);

/// <summary>What a posting or reversal wrote.</summary>
public sealed record StockPostingResult(IReadOnlyList<PostedMovement> Movements);

/// <summary>
/// How other modules (Sales, Purchases, returns) change stock. The ONLY way for them to do it: they never touch balances or the
/// ledger themselves, so every rule of the inventory module (ownership of warehouse, product, variant and batch, units and decimals,
/// no negative stock, moving-average cost, the append-only ledger) applies to them too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two ways to call.</b> <c>Stage…</c> joins the CALLER's unit of work: it changes the tracked balances and adds the movements
/// to the shared context but saves nothing, so the caller saves them together with its own document in ONE transaction
/// (<c>DatabaseTransactions.RunAsync</c>): an invoice can never be confirmed without its stock movement or the other way round.
/// If a staging call returns a failure, the caller must roll its unit of work back. <c>Post…</c> does the same in a transaction of
/// its own, for callers that have no document to commit with.
/// </para>
/// <para>
/// <b>Idempotent.</b> A source line posts a given movement once (a unique index in the database). Posting a line again is refused
/// with <c>Stock.AlreadyPosted</c> and changes nothing, so a retry or a double click cannot double the stock.
/// </para>
/// <para>
/// <b>Corrections are reversals.</b> The ledger is append-only. Cancelling a document reverses what it posted with new, opposite
/// movements that point at the originals (<c>ReverseAsync</c>); each movement can be reversed once.
/// </para>
/// </remarks>
public interface IStockPostingService
{
    /// <summary>Applies <paramref name="request"/> in the caller's unit of work. Saves nothing.</summary>
    Task<Result<StockPostingResult>> StageAsync(StockPostingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Applies <paramref name="request"/> in a transaction of its own.</summary>
    Task<Result<StockPostingResult>> PostAsync(StockPostingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reverses everything the source document posted that is not reversed yet, in the caller's unit of work. Saves nothing.</summary>
    Task<Result<StockPostingResult>> StageReversalAsync(string sourceType, Guid sourceId, string? notes = null, CancellationToken cancellationToken = default);

    /// <summary>Reverses everything the source document posted that is not reversed yet, in a transaction of its own.</summary>
    Task<Result<StockPostingResult>> ReverseAsync(string sourceType, Guid sourceId, string? notes = null, CancellationToken cancellationToken = default);
}

/// <summary>Pure checks of a <see cref="StockPostingRequest"/> (no database), unit-tested.</summary>
public static partial class StockPostingRules
{
    /// <summary>Most lines in one posting.</summary>
    public const int MaxLines = 500;

    /// <summary>Sources the inventory module uses itself; other modules cannot post as them.</summary>
    private static readonly string[] ReservedSourceTypes = [StockSourceTypes.Adjustment, StockSourceTypes.Transfer, StockSourceTypes.Manual];

    /// <summary>Whether <paramref name="sourceType"/> is a name another module may post as.</summary>
    public static bool IsValidSourceType([NotNullWhen(true)] string? sourceType) =>
        sourceType is { Length: > 0 and <= StockMovement.SourceTypeMaxLength }
        && SourceTypePattern().IsMatch(sourceType)
        && !ReservedSourceTypes.Contains(sourceType, StringComparer.OrdinalIgnoreCase);

    /// <summary>The first thing wrong with the request itself, or <see langword="null"/>. Whether the products, warehouse and stock exist is checked against the database.</summary>
    public static Error? Validate(StockPostingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsValidSourceType(request.SourceType))
        {
            return InventoryErrors.PostingInvalid(
                $"The source type must be 1 to {StockMovement.SourceTypeMaxLength} letters, digits or underscores (starting with a letter), and not one the inventory module uses itself.");
        }

        if (request.SourceId == Guid.Empty)
        {
            return InventoryErrors.PostingInvalid("The source document is required.");
        }

        if (request.WarehouseId == Guid.Empty)
        {
            return InventoryErrors.PostingInvalid("The warehouse is required.");
        }

        if (!Enum.IsDefined(request.Kind))
        {
            return InventoryErrors.PostingInvalid("Choose whether stock comes in or goes out.");
        }

        if (request.Lines is not { Count: > 0 })
        {
            return InventoryErrors.PostingInvalid("Add at least one line.");
        }

        if (request.Lines.Count > MaxLines)
        {
            return InventoryErrors.PostingInvalid($"A posting can have at most {MaxLines} lines. Split it into several.");
        }

        foreach (var line in request.Lines)
        {
            if (line.SourceLineId == Guid.Empty || line.ProductId == Guid.Empty)
            {
                return InventoryErrors.PostingInvalid("Every line needs its source line and a product.");
            }

            if (line.Quantity <= 0)
            {
                return InventoryErrors.PostingInvalid("Every quantity must be greater than zero.");
            }

            if (line.UnitCost is < 0)
            {
                return InventoryErrors.PostingInvalid("A unit cost cannot be negative.");
            }
        }

        // The same source line twice would be the same fact twice (the database refuses it too).
        var duplicate = request.Lines
            .GroupBy(line => (line.SourceLineId, line.ProductId, line.VariantId, line.BatchId))
            .FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : InventoryErrors.PostingInvalid("The same source line appears twice in the posting.");
    }

    /// <summary>The movement type a <paramref name="kind"/> of posting writes.</summary>
    public static StockMovementType MovementType(StockPostingKind kind) =>
        kind == StockPostingKind.Receipt ? StockMovementType.Receipt : StockMovementType.Issue;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex SourceTypePattern();
}
