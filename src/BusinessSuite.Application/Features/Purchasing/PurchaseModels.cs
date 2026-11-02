using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Purchasing;

// The contract the Purchases screens are built against. There is no purchasing backend yet: until one implements these
// interfaces, the web project registers a clearly labelled in-memory preview. Receiving a purchase is the ONLY thing that
// may change stock (through IStockPostingService); creating, submitting or editing a purchase never does.

public enum PurchaseStatus
{
    Draft = 1,
    Submitted = 2,
    PartiallyReceived = 3,
    Received = 4,
    Cancelled = 5,
}

public enum PaymentStatus
{
    Unpaid = 1,
    PartiallyPaid = 2,
    Paid = 3,
}

public sealed record SupplierSummary(Guid Id, string Name, string? ContactPerson, string? Phone, string? Email, string? Address, bool IsActive = true);

public sealed record PurchaseItemDetails(
    Guid Id, Guid ProductId, Guid? VariantId, string Sku, string Name, string UnitCode, decimal Quantity, decimal UnitPrice,
    decimal DiscountPercent, decimal TaxPercent, decimal ReceivedQuantity, decimal ReturnedQuantity)
{
    public decimal Remaining => Quantity - ReceivedQuantity;

    /// <summary>Received and not yet returned: the most that can still be returned.</summary>
    public decimal Returnable => ReceivedQuantity - ReturnedQuantity;

    public decimal LineNet => PurchaseTotals.LineNet(Quantity, UnitPrice, DiscountPercent);

    public decimal LineTax => PurchaseTotals.LineTax(Quantity, UnitPrice, DiscountPercent, TaxPercent);
}

public sealed record StockMovementLine(Guid ItemId, string Sku, string Name, decimal Quantity);

public sealed record PurchaseReceiptDetails(Guid Id, string Number, DateOnly ReceivedOn, string? Notes, IReadOnlyList<StockMovementLine> Lines);

public sealed record PurchaseReturnDetails(
    Guid Id, string Number, Guid PurchaseId, string PurchaseNumber, string SupplierName, DateOnly ReturnedOn, string Reason, IReadOnlyList<StockMovementLine> Lines);

public sealed record PurchaseSummary(
    Guid Id, string Number, Guid SupplierId, string SupplierName, string WarehouseName, DateOnly PurchaseDate, decimal GrandTotal,
    PaymentStatus PaymentStatus, PurchaseStatus Status, decimal AmountPaid = 0m);

public sealed record PurchaseDetails(
    Guid Id, string Number, SupplierSummary Supplier, Guid WarehouseId, string WarehouseName, DateOnly PurchaseDate, string? Notes,
    decimal OrderDiscount, PurchaseStatus Status, PaymentStatus PaymentStatus, decimal AmountPaid,
    IReadOnlyList<PurchaseItemDetails> Items, IReadOnlyList<PurchaseReceiptDetails> Receipts, IReadOnlyList<PurchaseReturnDetails> Returns)
{
    public PurchaseTotalsResult Totals => PurchaseTotals.Calculate(Items.Select(item => (item.Quantity, item.UnitPrice, item.DiscountPercent, item.TaxPercent)), OrderDiscount);

    public decimal BalanceDue => Totals.GrandTotal - AmountPaid;
}

public sealed record PurchaseListQuery
{
    public string? Number { get; init; }

    public Guid? SupplierId { get; init; }

    public DateOnly? From { get; init; }

    public DateOnly? To { get; init; }

    public PurchaseStatus? Status { get; init; }

    public PaymentStatus? PaymentStatus { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

public sealed record PurchaseItemCommand(Guid ProductId, Guid? VariantId, string Sku, string Name, string UnitCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent);

/// <summary>Creates a purchase. <c>Submit</c> false saves a draft. Never touches stock.</summary>
public sealed record CreatePurchaseCommand(
    Guid SupplierId, Guid WarehouseId, string WarehouseName, DateOnly PurchaseDate, string? Notes, decimal OrderDiscount, bool Submit, IReadOnlyList<PurchaseItemCommand> Items);

public sealed record QuantityLine(Guid ItemId, decimal Quantity);

public sealed record SupplierListQuery
{
    public string? Search { get; init; }

    public bool? IsActive { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

public sealed record SaveSupplierCommand(string? Name, string? ContactPerson, string? Phone, string? Email, string? Address);

/// <summary>The supplier contract the Suppliers screens are built against (no backend yet; see the in-memory preview).</summary>
public interface ISupplierService
{
    Task<Result<PagedResult<SupplierSummary>>> ListAsync(SupplierListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Active suppliers for drop-downs.</summary>
    Task<Result<IReadOnlyList<SupplierSummary>>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<Result<SupplierSummary>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<SupplierSummary>> CreateAsync(SaveSupplierCommand command, CancellationToken cancellationToken = default);

    Task<Result<SupplierSummary>> UpdateAsync(Guid id, SaveSupplierCommand command, CancellationToken cancellationToken = default);

    Task<Result<SupplierSummary>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);
}

/// <summary>What a supplier's purchases add up to.</summary>
public sealed record SupplierStatistics(int PurchaseCount, decimal TotalPurchases, decimal AmountPaid, decimal Outstanding, DateOnly? LastPurchaseDate)
{
    /// <summary>
    /// Counts submitted, partly received and received purchases (not drafts or cancelled ones). Outstanding = total - paid, never
    /// below zero; it is only meaningful once payments are recorded by a backend.
    /// </summary>
    public static SupplierStatistics From(IEnumerable<PurchaseSummary> purchases)
    {
        var counted = purchases.Where(purchase => purchase.Status is PurchaseStatus.Submitted or PurchaseStatus.PartiallyReceived or PurchaseStatus.Received).ToList();
        var total = counted.Sum(purchase => purchase.GrandTotal);
        var paid = counted.Sum(purchase => purchase.AmountPaid);
        return new SupplierStatistics(counted.Count, total, paid, Math.Max(0, total - paid), counted.Count == 0 ? null : counted.Max(purchase => purchase.PurchaseDate));
    }
}

public interface IPurchaseService
{
    Task<Result<PagedResult<PurchaseSummary>>> ListAsync(PurchaseListQuery query, CancellationToken cancellationToken = default);

    Task<Result<PurchaseDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PurchaseDetails>> CreateAsync(CreatePurchaseCommand command, CancellationToken cancellationToken = default);

    /// <summary>Draft to Submitted (sent to the supplier). Stock is unchanged.</summary>
    Task<Result<PurchaseDetails>> SubmitAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PurchaseDetails>> CancelAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Records goods received (partial receipts allowed). This is the operation that must post stock into the warehouse.</summary>
    Task<Result<PurchaseDetails>> ReceiveAsync(Guid id, DateOnly receivedOn, string? notes, IReadOnlyList<QuantityLine> lines, CancellationToken cancellationToken = default);

    /// <summary>Returns received goods to the supplier. This is the operation that must post stock out of the warehouse.</summary>
    Task<Result<PurchaseDetails>> ReturnAsync(Guid id, DateOnly returnedOn, string reason, IReadOnlyList<QuantityLine> lines, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<PurchaseReturnDetails>>> ListReturnsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
}

public sealed record PurchaseTotalsResult(decimal Subtotal, decimal LineDiscounts, decimal OrderDiscount, decimal Tax, decimal GrandTotal);

/// <summary>
/// Purchase totals, always calculated here and never trusted from a browser. Line net = quantity x price less the line
/// discount %; tax % applies to the line net; the order discount (an amount, capped at the subtotal) comes off the total.
/// </summary>
public static class PurchaseTotals
{
    public static decimal LineGross(decimal quantity, decimal unitPrice) => Math.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    public static decimal LineNet(decimal quantity, decimal unitPrice, decimal discountPercent) =>
        Math.Round(quantity * unitPrice * (1 - (discountPercent / 100m)), 2, MidpointRounding.AwayFromZero);

    public static decimal LineTax(decimal quantity, decimal unitPrice, decimal discountPercent, decimal taxPercent) =>
        Math.Round(LineNet(quantity, unitPrice, discountPercent) * (taxPercent / 100m), 2, MidpointRounding.AwayFromZero);

    public static PurchaseTotalsResult Calculate(IEnumerable<(decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal TaxPercent)> lines, decimal orderDiscount)
    {
        var all = lines.ToList();
        var gross = all.Sum(line => LineGross(line.Quantity, line.UnitPrice));
        var net = all.Sum(line => LineNet(line.Quantity, line.UnitPrice, line.DiscountPercent));
        var tax = all.Sum(line => LineTax(line.Quantity, line.UnitPrice, line.DiscountPercent, line.TaxPercent));
        var discount = Math.Clamp(orderDiscount, 0m, net);
        return new PurchaseTotalsResult(net, gross - net, discount, tax, net - discount + tax);
    }
}
