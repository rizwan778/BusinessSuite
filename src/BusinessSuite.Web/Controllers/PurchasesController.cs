using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Application.Features.Purchasing;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Purchasing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// Purchases: list with filters, create (draft or submit), details, receiving (partial allowed) and returns.
/// Creating or submitting a purchase never changes stock; only receiving (and returning) goods may, through the purchasing
/// service, which must post to inventory. Thin: totals and rules live in the Application layer.
/// </summary>
[RequirePermission(Permissions.Purchasing.View)]
public sealed class PurchasesController(
    IPurchaseService purchases,
    ISupplierService suppliers,
    IWarehouseService warehouses,
    ProductLineResolver resolver,
    ILogger<PurchasesController> logger) : AppController
{
    private const int PageSize = 20;

    // GET /Purchases
    [HttpGet]
    public async Task<IActionResult> Index(
        string? number, Guid? supplierId, DateOnly? from, DateOnly? to, PurchaseStatus? status, PaymentStatus? paymentStatus, int page = 1, CancellationToken cancellationToken = default)
    {
        var query = new PurchaseListQuery { Number = number, SupplierId = supplierId, From = from, To = to, Status = status, PaymentStatus = paymentStatus, Page = page, PageSize = PageSize };
        var result = await purchases.ListAsync(query, cancellationToken);
        return result.IsSuccess
            ? View(new PurchaseListViewModel { Query = query, Purchases = result.Value, Suppliers = await SupplierOptionsAsync(cancellationToken) })
            : ErrorResult(result.Error);
    }

    // GET /Purchases/Details/{id}
    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var result = await purchases.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Purchases/Create
    [HttpGet]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        await CreateViewAsync(new PurchaseFormModel { PurchaseDate = DateOnly.FromDateTime(DateTime.Today) }, cancellationToken);

    // POST /Purchases/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Create(PurchaseFormModel form, CancellationToken cancellationToken)
    {
        var items = new List<PurchaseItemCommand>();
        foreach (var (line, index) in form.Lines.Select((line, index) => (line, index)).Where(pair => !pair.line.IsBlank))
        {
            var (item, error) = await resolver.ResolveAsync(index + 1, line, cancellationToken);
            if (item is not null)
            {
                items.Add(item);
            }
            else
            {
                ModelState.AddModelError(string.Empty, error!);
            }
        }

        if (items.Count == 0 && ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Add at least one item.");
        }

        var warehouse = await warehouses.GetAsync(form.WarehouseId, cancellationToken);
        if (warehouse.IsFailure)
        {
            ModelState.AddModelError(string.Empty, "Select a warehouse.");
        }

        if (form.SupplierId == Guid.Empty)
        {
            ModelState.AddModelError(string.Empty, "Select a supplier.");
        }

        if (!ModelState.IsValid)
        {
            return await CreateViewAsync(form, cancellationToken);
        }

        var orderDiscount = form.OrderDiscount ?? 0;
        if (form.Submission == "calculate")
        {
            form.Totals = PurchaseTotals.Calculate(items.Select(item => (item.Quantity, item.UnitPrice, item.DiscountPercent, item.TaxPercent)), orderDiscount);
            return await CreateViewAsync(form, cancellationToken);
        }

        var result = await purchases.CreateAsync(
            new CreatePurchaseCommand(form.SupplierId, form.WarehouseId, warehouse.Value.Name, form.PurchaseDate ?? DateOnly.FromDateTime(DateTime.Today), form.Notes, orderDiscount, form.Submission == "submit", items),
            cancellationToken);
        if (result.IsFailure)
        {
            logger.LogInformation("Purchase create rejected: {ErrorCode}", result.Error.Code);
            AddErrorToModelState(result.Error);
            return await CreateViewAsync(form, cancellationToken);
        }

        NotifySuccess($"Purchase {result.Value.Number} {(form.Submission == "submit" ? "submitted" : "saved as a draft")}. Stock is unchanged until goods are received.");
        return RedirectToAction(nameof(Details), new { id = result.Value.Id });
    }

    // POST /Purchases/Submit/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Submit(Guid id, CancellationToken cancellationToken)
    {
        Report(await purchases.SubmitAsync(id, cancellationToken), "Purchase submitted.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Purchases/Cancel/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        Report(await purchases.CancelAsync(id, cancellationToken), "Purchase cancelled.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /Purchases/Receive/{id}
    [HttpGet]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Receive(Guid id, CancellationToken cancellationToken) =>
        await QuantityViewAsync("Receive", id, new QuantityFormModel { Date = DateOnly.FromDateTime(DateTime.Today) }, cancellationToken);

    // POST /Purchases/Receive/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Receive(Guid id, QuantityFormModel form, CancellationToken cancellationToken)
    {
        var result = await purchases.ReceiveAsync(id, form.Date ?? DateOnly.FromDateTime(DateTime.Today), form.Notes, form.ToLines(), cancellationToken);
        if (result.IsFailure)
        {
            AddErrorToModelState(result.Error);
            return await QuantityViewAsync("Receive", id, form, cancellationToken);
        }

        NotifySuccess("Goods receipt recorded.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /Purchases/Return/{id}
    [HttpGet]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Return(Guid id, CancellationToken cancellationToken) =>
        await QuantityViewAsync("Return", id, new QuantityFormModel { Date = DateOnly.FromDateTime(DateTime.Today) }, cancellationToken);

    // POST /Purchases/Return/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Purchasing.Manage)]
    public async Task<IActionResult> Return(Guid id, QuantityFormModel form, CancellationToken cancellationToken)
    {
        var result = await purchases.ReturnAsync(id, form.Date ?? DateOnly.FromDateTime(DateTime.Today), form.Notes ?? string.Empty, form.ToLines(), cancellationToken);
        if (result.IsFailure)
        {
            AddErrorToModelState(result.Error);
            return await QuantityViewAsync("Return", id, form, cancellationToken);
        }

        NotifySuccess("Purchase return recorded.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET /Purchases/Returns
    [HttpGet]
    public async Task<IActionResult> Returns(int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await purchases.ListReturnsAsync(page, PageSize, cancellationToken);
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // ---- helpers ----

    private void Report<T>(Application.Common.Results.Result<T> result, string success)
    {
        if (result.IsFailure)
        {
            NotifyError(result.Error.Description);
        }
        else
        {
            NotifySuccess(success);
        }
    }

    private async Task<IReadOnlyList<SelectListItem>> SupplierOptionsAsync(CancellationToken cancellationToken)
    {
        var result = await suppliers.ListActiveAsync(cancellationToken);
        return result.IsFailure ? [] : result.Value.Select(supplier => new SelectListItem(supplier.Name, supplier.Id.ToString())).ToList();
    }

    private async Task<IActionResult> CreateViewAsync(PurchaseFormModel form, CancellationToken cancellationToken)
    {
        form.PadLines();
        form.Suppliers = await SupplierOptionsAsync(cancellationToken);
        var list = await warehouses.ListAsync(new WarehouseListQuery { IsActive = true, PageSize = 100 }, cancellationToken);
        form.Warehouses = list.IsFailure ? [] : list.Value.Items.Select(warehouse => new SelectListItem($"{warehouse.Name} ({warehouse.Code})", warehouse.Id.ToString())).ToList();
        return View("Create", form);
    }

    private async Task<IActionResult> QuantityViewAsync(string view, Guid id, QuantityFormModel form, CancellationToken cancellationToken)
    {
        var purchase = await purchases.GetAsync(id, cancellationToken);
        if (purchase.IsFailure)
        {
            return ErrorResult(purchase.Error);
        }

        form.Purchase = purchase.Value;
        var posted = form.Lines.ToDictionary(line => line.ItemId, line => line.Quantity);
        form.Lines = purchase.Value.Items.Select(item => new QuantityInput { ItemId = item.Id, Quantity = posted.GetValueOrDefault(item.Id) }).ToList();
        return View(view, form);
    }
}
