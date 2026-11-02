using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Purchasing;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Purchasing;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// Supplier management and reports. Frontend only for now: the screens call <see cref="ISupplierService"/> and
/// <see cref="IPurchaseService"/>, which the in-memory preview implements until the backend exists.
/// </summary>
[RequirePermission(Permissions.Purchasing.View)]
public sealed class SuppliersController(ISupplierService suppliers, IPurchaseService purchases, ILogger<SuppliersController> logger) : AppController
{
    private const int PageSize = 20;
    private const int MaxHistory = 100;

    // GET /Suppliers
    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await suppliers.ListAsync(
            new SupplierListQuery { Search = search, IsActive = status switch { "active" => true, "inactive" => false, _ => null }, Page = page, PageSize = PageSize }, cancellationToken);
        return result.IsSuccess ? View(new SupplierListViewModel { Search = search, Status = status, Suppliers = result.Value }) : ErrorResult(result.Error);
    }

    // GET /Suppliers/Details/{id}
    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var supplier = await suppliers.GetAsync(id, cancellationToken);
        if (supplier.IsFailure)
        {
            return ErrorResult(supplier.Error);
        }

        var history = await purchases.ListAsync(new PurchaseListQuery { SupplierId = id, PageSize = MaxHistory }, cancellationToken);
        if (history.IsFailure)
        {
            return ErrorResult(history.Error);
        }

        var returns = await purchases.ListReturnsAsync(1, MaxHistory, cancellationToken);
        var ids = history.Value.Items.Select(purchase => purchase.Id).ToHashSet();

        var transactions = history.Value.Items
            .Select(purchase => new SupplierTransaction(purchase.PurchaseDate, "Purchase", purchase.Number, purchase.Id, purchase.GrandTotal, purchase.Status.ToString()))
            .Concat((returns.IsSuccess ? returns.Value.Items : []).Where(entry => ids.Contains(entry.PurchaseId))
                .Select(entry => new SupplierTransaction(entry.ReturnedOn, "Return", entry.Number, entry.PurchaseId, null, entry.Reason)))
            .OrderByDescending(transaction => transaction.Date)
            .Take(10)
            .ToList();

        return View(new SupplierDetailsViewModel
        {
            Supplier = supplier.Value,
            Statistics = SupplierStatistics.From(history.Value.Items),
            Purchases = history.Value.Items.Take(10).ToList(),
            Transactions = transactions,
        });
    }

    // GET /Suppliers/Create
    [HttpGet]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public IActionResult Create() => View("Form", new SupplierFormModel());

    // POST /Suppliers/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Create(SupplierFormModel form, CancellationToken cancellationToken)
    {
        var result = await suppliers.CreateAsync(form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return Rejected(result.Error, form);
        }

        NotifySuccess($"Supplier '{result.Value.Name}' added.");
        return RedirectToAction(nameof(Details), new { id = result.Value.Id });
    }

    // GET /Suppliers/Edit/{id}
    [HttpGet]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var result = await suppliers.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View("Form", SupplierFormModel.From(result.Value)) : ErrorResult(result.Error);
    }

    // POST /Suppliers/Edit/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Edit(Guid id, SupplierFormModel form, CancellationToken cancellationToken)
    {
        form.Id = id;
        var result = await suppliers.UpdateAsync(id, form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Type == ErrorType.NotFound ? ErrorResult(result.Error) : Rejected(result.Error, form);
        }

        NotifySuccess($"Supplier '{result.Value.Name}' saved.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Suppliers/SetStatus/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> SetStatus(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var result = await suppliers.SetActiveAsync(id, isActive, cancellationToken);
        if (result.IsFailure)
        {
            NotifyError(result.Error.Description);
        }
        else
        {
            NotifySuccess($"Supplier '{result.Value.Name}' {(isActive ? "activated" : "deactivated")}.");
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /Suppliers/Reports
    [HttpGet]
    public async Task<IActionResult> Reports(DateOnly? from, DateOnly? to, Guid? supplierId, CancellationToken cancellationToken)
    {
        var all = await suppliers.ListAsync(new SupplierListQuery { PageSize = 100 }, cancellationToken);
        if (all.IsFailure)
        {
            return ErrorResult(all.Error);
        }

        var rows = new List<SupplierReportRow>();
        var everything = new List<PurchaseSummary>();
        foreach (var supplier in all.Value.Items)
        {
            var list = await purchases.ListAsync(new PurchaseListQuery { SupplierId = supplier.Id, From = from, To = to, PageSize = MaxHistory }, cancellationToken);
            var items = list.IsSuccess ? list.Value.Items : [];
            everything.AddRange(items);
            rows.Add(new SupplierReportRow(supplier, SupplierStatistics.From(items)));
        }

        return View(new SupplierReportViewModel
        {
            From = from,
            To = to,
            SupplierId = supplierId,
            Rows = rows.OrderByDescending(row => row.Statistics.TotalPurchases).ToList(),
            Totals = SupplierStatistics.From(everything),
            Selected = rows.Select(row => row.Supplier).FirstOrDefault(supplier => supplier.Id == supplierId),
            History = supplierId is null ? [] : everything.Where(purchase => purchase.SupplierId == supplierId).OrderByDescending(purchase => purchase.PurchaseDate).ToList(),
        });
    }

    private IActionResult Rejected(Error error, SupplierFormModel form)
    {
        logger.LogInformation("Supplier save rejected: {ErrorCode}", error.Code);
        AddErrorToModelState(error);
        return View("Form", form);
    }
}
