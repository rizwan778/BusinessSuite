using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Purchasing;

namespace BusinessSuite.Web.Framework.Preview;

/// <summary>
/// FRONTEND PREVIEW ONLY. There is no purchasing backend yet, so this keeps purchases and a few demo suppliers in memory
/// (lost on restart, shared by every user) to let the Purchases screens be used end to end. It never touches inventory:
/// when a real service exists it replaces this registration, and receiving/returning there must post stock through
/// <c>IStockPostingService</c>. Not for production.
/// </summary>
public sealed class InMemoryPurchasing : IPurchaseService, ISupplierService
{
    private readonly object _lock = new();
    private readonly List<PurchaseRecord> _purchases = [];

    private readonly List<SupplierSummary> _suppliers =
    [
        new(Guid.Parse("a1111111-1111-4111-8111-111111111111"), "Demo Supplier - General Traders", "Ali Raza", "+92 300 0000001", "sales@general.example", "Main Market, Lahore"),
        new(Guid.Parse("a2222222-2222-4222-8222-222222222222"), "Demo Supplier - Tech Distributors", "Sara Khan", "+92 300 0000002", "orders@tech.example", "I.T. Tower, Karachi"),
        new(Guid.Parse("a3333333-3333-4333-8333-333333333333"), "Demo Supplier - Fresh Foods Co.", "Usman Tariq", "+92 300 0000003", "hello@fresh.example", "Sabzi Mandi, Islamabad"),
    ];

    private sealed class PurchaseRecord
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public required string Number { get; init; }

        public required Guid SupplierId { get; init; }

        public Guid WarehouseId { get; init; }

        public required string WarehouseName { get; init; }

        public DateOnly PurchaseDate { get; init; }

        public string? Notes { get; init; }

        public decimal OrderDiscount { get; init; }

        public PurchaseStatus Status { get; set; }

        public List<PurchaseItemDetails> Items { get; set; } = [];

        public List<PurchaseReceiptDetails> Receipts { get; } = [];

        public List<PurchaseReturnDetails> Returns { get; } = [];
    }

    // ---- suppliers ----

    Task<Result<PagedResult<SupplierSummary>>> ISupplierService.ListAsync(SupplierListQuery query, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var search = query.Search?.Trim();
            var rows = _suppliers
                .Where(supplier => (query.IsActive is null || supplier.IsActive == query.IsActive)
                    && (string.IsNullOrEmpty(search)
                        || supplier.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || (supplier.ContactPerson?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (supplier.Phone?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (supplier.Email?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)))
                .OrderBy(supplier => supplier.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var request = new PageRequest(query.Page, query.PageSize);
            return Task.FromResult(Result.Success(new PagedResult<SupplierSummary>(rows.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList(), request, rows.Count)));
        }
    }

    Task<Result<IReadOnlyList<SupplierSummary>>> ISupplierService.ListActiveAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<SupplierSummary> active = _suppliers.Where(supplier => supplier.IsActive).OrderBy(supplier => supplier.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return Task.FromResult(Result.Success(active));
        }
    }

    Task<Result<SupplierSummary>> ISupplierService.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_suppliers.FirstOrDefault(supplier => supplier.Id == id) is { } found
                ? Result.Success(found)
                : Result.Failure<SupplierSummary>(SupplierNotFound));
        }
    }

    Task<Result<SupplierSummary>> ISupplierService.CreateAsync(SaveSupplierCommand command, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (Validate(command, null) is { } error)
            {
                return Task.FromResult(Result.Failure<SupplierSummary>(error));
            }

            var supplier = new SupplierSummary(Guid.NewGuid(), command.Name!.Trim(), Clean(command.ContactPerson), Clean(command.Phone), Clean(command.Email), Clean(command.Address));
            _suppliers.Add(supplier);
            return Task.FromResult(Result.Success(supplier));
        }
    }

    Task<Result<SupplierSummary>> ISupplierService.UpdateAsync(Guid id, SaveSupplierCommand command, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _suppliers.FindIndex(supplier => supplier.Id == id);
            if (index < 0)
            {
                return Task.FromResult(Result.Failure<SupplierSummary>(SupplierNotFound));
            }

            if (Validate(command, id) is { } error)
            {
                return Task.FromResult(Result.Failure<SupplierSummary>(error));
            }

            _suppliers[index] = _suppliers[index] with
            {
                Name = command.Name!.Trim(),
                ContactPerson = Clean(command.ContactPerson),
                Phone = Clean(command.Phone),
                Email = Clean(command.Email),
                Address = Clean(command.Address),
            };
            return Task.FromResult(Result.Success(_suppliers[index]));
        }
    }

    Task<Result<SupplierSummary>> ISupplierService.SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _suppliers.FindIndex(supplier => supplier.Id == id);
            if (index < 0)
            {
                return Task.FromResult(Result.Failure<SupplierSummary>(SupplierNotFound));
            }

            _suppliers[index] = _suppliers[index] with { IsActive = isActive };
            return Task.FromResult(Result.Success(_suppliers[index]));
        }
    }

    private static readonly Error SupplierNotFound = Error.NotFound("Suppliers.NotFound", "Supplier not found.");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Error? Validate(SaveSupplierCommand command, Guid? ignoreId)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            return Error.Validation("Suppliers.Invalid", "Enter the supplier name.");
        }

        if (!string.IsNullOrWhiteSpace(command.Email) && !command.Email.Contains('@', StringComparison.Ordinal))
        {
            return Error.Validation("Suppliers.Invalid", "Enter a valid e-mail address.");
        }

        return _suppliers.Any(supplier => supplier.Id != ignoreId && string.Equals(supplier.Name, command.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            ? Error.Conflict("Suppliers.Duplicate", "A supplier with this name already exists.")
            : null;
    }

    private SupplierSummary SupplierOf(PurchaseRecord purchase) => _suppliers.First(supplier => supplier.Id == purchase.SupplierId);

    // ---- purchases ----

    public Task<Result<PagedResult<PurchaseSummary>>> ListAsync(PurchaseListQuery query, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var rows = _purchases.Where(purchase =>
                    (string.IsNullOrWhiteSpace(query.Number) || purchase.Number.Contains(query.Number.Trim(), StringComparison.OrdinalIgnoreCase))
                    && (query.SupplierId is null || purchase.SupplierId == query.SupplierId)
                    && (query.From is null || purchase.PurchaseDate >= query.From)
                    && (query.To is null || purchase.PurchaseDate <= query.To)
                    && (query.Status is null || purchase.Status == query.Status))
                .Select(ToSummary)
                .Where(summary => query.PaymentStatus is null || summary.PaymentStatus == query.PaymentStatus)
                .OrderByDescending(summary => summary.PurchaseDate).ThenByDescending(summary => summary.Number)
                .ToList();

            var request = new PageRequest(query.Page, query.PageSize);
            var page = rows.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList();
            return Task.FromResult(Result.Success(new PagedResult<PurchaseSummary>(page, request, rows.Count)));
        }
    }

    public Task<Result<PurchaseDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(Find(id) is { } purchase ? Result.Success(ToDetails(purchase)) : Result.Failure<PurchaseDetails>(NotFound));
        }
    }

    public Task<Result<PurchaseDetails>> CreateAsync(CreatePurchaseCommand command, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var supplier = _suppliers.FirstOrDefault(candidate => candidate.Id == command.SupplierId && candidate.IsActive);
            if (supplier is null)
            {
                return Task.FromResult(Result.Failure<PurchaseDetails>(Error.Validation("Purchases.SupplierInvalid", "Select a supplier.")));
            }

            if (command.Items.Count == 0 || command.Items.Any(item => item.Quantity <= 0 || item.UnitPrice < 0 || item.DiscountPercent is < 0 or > 100 || item.TaxPercent < 0))
            {
                return Task.FromResult(Result.Failure<PurchaseDetails>(Error.Validation("Purchases.ItemsInvalid", "Add at least one item with a quantity above zero, a price, and discount between 0 and 100%.")));
            }

            var record = new PurchaseRecord
            {
                Number = $"PO-{DateTime.UtcNow:yyyyMM}-{_purchases.Count + 1:0000}",
                SupplierId = supplier.Id,
                WarehouseId = command.WarehouseId,
                WarehouseName = command.WarehouseName,
                PurchaseDate = command.PurchaseDate,
                Notes = command.Notes,
                OrderDiscount = Math.Max(0, command.OrderDiscount),
                Status = command.Submit ? PurchaseStatus.Submitted : PurchaseStatus.Draft,
            };
            record.Items = command.Items
                .Select(item => new PurchaseItemDetails(Guid.NewGuid(), item.ProductId, item.VariantId, item.Sku, item.Name, item.UnitCode, item.Quantity, item.UnitPrice, item.DiscountPercent, item.TaxPercent, 0, 0))
                .ToList();
            _purchases.Add(record);
            return Task.FromResult(Result.Success(ToDetails(record)));
        }
    }

    public Task<Result<PurchaseDetails>> SubmitAsync(Guid id, CancellationToken cancellationToken = default) =>
        Change(id, purchase => purchase.Status == PurchaseStatus.Draft ? Apply(purchase, PurchaseStatus.Submitted) : Wrong("Only a draft can be submitted."));

    public Task<Result<PurchaseDetails>> CancelAsync(Guid id, CancellationToken cancellationToken = default) =>
        Change(id, purchase => purchase.Status is PurchaseStatus.Draft or PurchaseStatus.Submitted && purchase.Receipts.Count == 0
            ? Apply(purchase, PurchaseStatus.Cancelled)
            : Wrong("Only a draft or submitted purchase with nothing received can be cancelled."));

    public Task<Result<PurchaseDetails>> ReceiveAsync(Guid id, DateOnly receivedOn, string? notes, IReadOnlyList<QuantityLine> lines, CancellationToken cancellationToken = default) =>
        Change(id, purchase =>
        {
            if (purchase.Status is not (PurchaseStatus.Submitted or PurchaseStatus.PartiallyReceived))
            {
                return Wrong("Only a submitted or partly received purchase can receive goods.");
            }

            var wanted = lines.Where(line => line.Quantity > 0).ToList();
            if (wanted.Count == 0)
            {
                return Wrong("Enter a received quantity for at least one item.");
            }

            foreach (var line in wanted)
            {
                var item = purchase.Items.FirstOrDefault(candidate => candidate.Id == line.ItemId);
                if (item is null || line.Quantity > item.Remaining)
                {
                    return Wrong($"You cannot receive more than is still outstanding{(item is null ? "" : $" for {item.Sku} ({item.Remaining:0.####})")}.");
                }
            }

            // PREVIEW: only the purchase record changes. A real service posts the receipt to stock here (IStockPostingService).
            purchase.Items = purchase.Items
                .Select(item => wanted.FirstOrDefault(line => line.ItemId == item.Id) is { } line ? item with { ReceivedQuantity = item.ReceivedQuantity + line.Quantity } : item)
                .ToList();
            purchase.Receipts.Add(new PurchaseReceiptDetails(
                Guid.NewGuid(), $"GRN-{purchase.Number[3..]}-{purchase.Receipts.Count + 1:00}", receivedOn, notes,
                wanted.Select(line => ToLine(purchase, line)).ToList()));
            purchase.Status = purchase.Items.All(item => item.Remaining <= 0) ? PurchaseStatus.Received : PurchaseStatus.PartiallyReceived;
            return null;
        });

    public Task<Result<PurchaseDetails>> ReturnAsync(Guid id, DateOnly returnedOn, string reason, IReadOnlyList<QuantityLine> lines, CancellationToken cancellationToken = default) =>
        Change(id, purchase =>
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Wrong("Enter the reason for the return.");
            }

            var wanted = lines.Where(line => line.Quantity > 0).ToList();
            if (wanted.Count == 0)
            {
                return Wrong("Enter a return quantity for at least one item.");
            }

            foreach (var line in wanted)
            {
                var item = purchase.Items.FirstOrDefault(candidate => candidate.Id == line.ItemId);
                if (item is null || line.Quantity > item.Returnable)
                {
                    return Wrong($"You cannot return more than was received and not yet returned{(item is null ? "" : $" for {item.Sku} ({item.Returnable:0.####})")}.");
                }
            }

            // PREVIEW: a real service posts the return out of stock here (IStockPostingService).
            purchase.Items = purchase.Items
                .Select(item => wanted.FirstOrDefault(line => line.ItemId == item.Id) is { } line ? item with { ReturnedQuantity = item.ReturnedQuantity + line.Quantity } : item)
                .ToList();
            purchase.Returns.Add(new PurchaseReturnDetails(
                Guid.NewGuid(), $"PR-{purchase.Number[3..]}-{purchase.Returns.Count + 1:00}", purchase.Id, purchase.Number, SupplierOf(purchase).Name, returnedOn,
                reason.Trim(), wanted.Select(line => ToLine(purchase, line)).ToList()));
            return null;
        });

    public Task<Result<PagedResult<PurchaseReturnDetails>>> ListReturnsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var all = _purchases.SelectMany(purchase => purchase.Returns).OrderByDescending(entry => entry.ReturnedOn).ToList();
            var request = new PageRequest(page, pageSize);
            return Task.FromResult(Result.Success(new PagedResult<PurchaseReturnDetails>(all.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToList(), request, all.Count)));
        }
    }

    // ---- helpers ----

    private static readonly Error NotFound = Error.NotFound("Purchases.NotFound", "Purchase not found.");

    private static Error Wrong(string message) => Error.Conflict("Purchases.InvalidOperation", message);

    private static Error? Apply(PurchaseRecord purchase, PurchaseStatus status)
    {
        purchase.Status = status;
        return null;
    }

    /// <summary>Runs <paramref name="action"/> under the lock; it returns an error to refuse, or null after changing the record.</summary>
    private Task<Result<PurchaseDetails>> Change(Guid id, Func<PurchaseRecord, Error?> action)
    {
        lock (_lock)
        {
            if (Find(id) is not { } purchase)
            {
                return Task.FromResult(Result.Failure<PurchaseDetails>(NotFound));
            }

            return Task.FromResult(action(purchase) is { } error ? Result.Failure<PurchaseDetails>(error) : Result.Success(ToDetails(purchase)));
        }
    }

    private PurchaseRecord? Find(Guid id) => _purchases.FirstOrDefault(purchase => purchase.Id == id);

    private static StockMovementLine ToLine(PurchaseRecord purchase, QuantityLine line)
    {
        var item = purchase.Items.First(candidate => candidate.Id == line.ItemId);
        return new StockMovementLine(item.Id, item.Sku, item.Name, line.Quantity);
    }

    private PurchaseDetails ToDetails(PurchaseRecord purchase) => new(
        purchase.Id, purchase.Number, SupplierOf(purchase), purchase.WarehouseId, purchase.WarehouseName, purchase.PurchaseDate, purchase.Notes,
        purchase.OrderDiscount, purchase.Status, PaymentStatus.Unpaid, 0m, purchase.Items.ToList(), purchase.Receipts.ToList(), purchase.Returns.ToList());

    private PurchaseSummary ToSummary(PurchaseRecord purchase)
    {
        var details = ToDetails(purchase);
        return new PurchaseSummary(purchase.Id, purchase.Number, purchase.SupplierId, SupplierOf(purchase).Name, purchase.WarehouseName, purchase.PurchaseDate, details.Totals.GrandTotal, details.PaymentStatus, purchase.Status, details.AmountPaid);
    }
}

public static class PurchasingPreviewExtensions
{
    /// <summary>Registers the in-memory preview for the purchase and supplier contracts (frontend work before the backend exists).</summary>
    public static IServiceCollection AddPurchasingPreview(this IServiceCollection services)
    {
        services.AddScoped<BusinessSuite.Web.Models.Purchasing.ProductLineResolver>();
        services.AddSingleton<InMemoryPurchasing>();
        services.AddSingleton<IPurchaseService>(provider => provider.GetRequiredService<InMemoryPurchasing>());
        services.AddSingleton<ISupplierService>(provider => provider.GetRequiredService<InMemoryPurchasing>());
        return services;
    }
}
