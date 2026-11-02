using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Inventory;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// The Inventory page and its sub-pages (shown as tabs): stock overview, warehouses, adjustments, transfers, movements,
/// batches and reports. Thin: stock rules live in the Application services; this only maps forms to commands.
/// </summary>
[RequirePermission(Permissions.Inventory.View)]
public sealed class InventoryController(
    IStockService stock,
    IWarehouseService warehouses,
    IStockAdjustmentService adjustments,
    IStockTransferService transfers,
    IBatchService batches,
    IInventoryReportService reports,
    IProductService products,
    ILogger<InventoryController> logger) : AppController
{
    private const int PageSize = 20;

    // ======================= Stock overview =======================

    // GET /Inventory
    [HttpGet]
    public async Task<IActionResult> Index(string? search, Guid? warehouseId, bool hideEmpty, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await stock.ListBalancesAsync(
            new StockBalanceQuery { Search = search, WarehouseId = warehouseId, HideEmpty = hideEmpty, Page = page, PageSize = PageSize }, cancellationToken);
        if (result.IsFailure)
        {
            return ErrorResult(result.Error);
        }

        // Category and reorder level come from the catalogue (one lookup per distinct product on this page).
        var catalogue = new Dictionary<Guid, ProductDetails?>();
        foreach (var productId in result.Value.Items.Select(balance => balance.ProductId).Distinct())
        {
            var product = await products.GetAsync(productId, cancellationToken);
            catalogue[productId] = product.IsSuccess ? product.Value : null;
        }

        var rows = result.Value.Items.Select(balance =>
        {
            catalogue.TryGetValue(balance.ProductId, out var product);
            var reorder = balance.VariantId is { } variantId
                ? product?.Variants.FirstOrDefault(variant => variant.Id == variantId)?.ReorderLevel ?? product?.ReorderLevel
                : product?.ReorderLevel;
            var status = balance.OnHand <= 0 ? "Out of stock" : reorder is { } level && balance.OnHand <= level ? "Low stock" : "In stock";
            return new StockRow(balance, product?.CategoryName, status);
        }).ToList();

        ViewData.SetPageTitle("Inventory");
        return View(new StockOverviewViewModel
        {
            Search = search,
            WarehouseId = warehouseId,
            HideEmpty = hideEmpty,
            Page = result.Value,
            Rows = rows,
            Warehouses = await WarehouseOptionsAsync(cancellationToken),
        });
    }

    // ======================= Warehouses =======================

    // GET /Inventory/Warehouses
    [HttpGet]
    public async Task<IActionResult> Warehouses(string? search, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await warehouses.ListAsync(new WarehouseListQuery { Search = search, Page = page, PageSize = PageSize }, cancellationToken);
        ViewData["Search"] = search;
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Inventory/WarehouseDetails/{id}
    [HttpGet]
    public async Task<IActionResult> WarehouseDetails(Guid id, int page = 1, CancellationToken cancellationToken = default)
    {
        var warehouse = await warehouses.GetAsync(id, cancellationToken);
        if (warehouse.IsFailure)
        {
            return ErrorResult(warehouse.Error);
        }

        var balances = await stock.ListBalancesAsync(new StockBalanceQuery { WarehouseId = id, HideEmpty = true, Page = page, PageSize = PageSize }, cancellationToken);
        return balances.IsSuccess
            ? View(new WarehouseDetailsViewModel { Warehouse = warehouse.Value, Stock = balances.Value })
            : ErrorResult(balances.Error);
    }

    // GET /Inventory/CreateWarehouse
    [HttpGet]
    [RequirePermission(Permissions.Inventory.Manage)]
    public IActionResult CreateWarehouse() => View("WarehouseForm", new WarehouseFormModel());

    // POST /Inventory/CreateWarehouse
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.Manage)]
    public async Task<IActionResult> CreateWarehouse(WarehouseFormModel form, CancellationToken cancellationToken)
    {
        var result = await warehouses.CreateAsync(form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return Rejected("Warehouse create", result.Error, "WarehouseForm", form);
        }

        NotifySuccess($"Warehouse '{result.Value.Name}' created.");
        return RedirectToAction(nameof(WarehouseDetails), new { id = result.Value.Id });
    }

    // GET /Inventory/EditWarehouse/{id}
    [HttpGet]
    [RequirePermission(Permissions.Inventory.Manage)]
    public async Task<IActionResult> EditWarehouse(Guid id, CancellationToken cancellationToken)
    {
        var result = await warehouses.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View("WarehouseForm", WarehouseFormModel.From(result.Value)) : ErrorResult(result.Error);
    }

    // POST /Inventory/EditWarehouse/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.Manage)]
    public async Task<IActionResult> EditWarehouse(Guid id, WarehouseFormModel form, CancellationToken cancellationToken)
    {
        form.Id = id;
        var result = await warehouses.UpdateAsync(id, form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Type == ErrorType.NotFound ? ErrorResult(result.Error) : Rejected("Warehouse update", result.Error, "WarehouseForm", form);
        }

        NotifySuccess($"Warehouse '{result.Value.Name}' saved.");
        return RedirectToAction(nameof(WarehouseDetails), new { id });
    }

    // POST /Inventory/SetWarehouseStatus/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.Manage)]
    public async Task<IActionResult> SetWarehouseStatus(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        Report(await warehouses.SetActiveAsync(id, isActive, cancellationToken), isActive ? "Warehouse activated." : "Warehouse deactivated.");
        return RedirectToAction(nameof(WarehouseDetails), new { id });
    }

    // POST /Inventory/SetDefaultWarehouse/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.Manage)]
    public async Task<IActionResult> SetDefaultWarehouse(Guid id, CancellationToken cancellationToken)
    {
        Report(await warehouses.SetDefaultAsync(id, cancellationToken), "Default warehouse changed.");
        return RedirectToAction(nameof(WarehouseDetails), new { id });
    }

    // ======================= Adjustments =======================

    // GET /Inventory/Adjustments
    [HttpGet]
    public async Task<IActionResult> Adjustments(string? search, Guid? warehouseId, StockAdjustmentStatus? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await adjustments.ListAsync(
            new StockAdjustmentQuery { Search = search, WarehouseId = warehouseId, Status = status, Page = page, PageSize = PageSize }, cancellationToken);
        if (result.IsFailure)
        {
            return ErrorResult(result.Error);
        }

        ViewData["Search"] = search;
        ViewData["WarehouseId"] = warehouseId;
        ViewData["Status"] = status;
        ViewData["Warehouses"] = await WarehouseOptionsAsync(cancellationToken);
        return View(result.Value);
    }

    // GET /Inventory/AdjustmentDetails/{id}
    [HttpGet]
    public async Task<IActionResult> AdjustmentDetails(Guid id, CancellationToken cancellationToken)
    {
        var result = await adjustments.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Inventory/CreateAdjustment
    [HttpGet]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    public async Task<IActionResult> CreateAdjustment(CancellationToken cancellationToken) =>
        await AdjustmentFormAsync(new AdjustmentFormModel(), cancellationToken);

    // POST /Inventory/CreateAdjustment
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    public async Task<IActionResult> CreateAdjustment(AdjustmentFormModel form, CancellationToken cancellationToken)
    {
        var items = new List<StockAdjustmentItemCommand>();
        foreach (var (line, index) in form.Lines.Select((line, index) => (line, index)).Where(pair => !pair.line.IsBlank))
        {
            if (line.Quantity is not > 0)
            {
                ModelState.AddModelError(string.Empty, $"Line {index + 1}: enter a quantity greater than zero.");
                continue;
            }

            if (await ResolveAsync(line, index + 1, cancellationToken) is not { } item)
            {
                continue;
            }

            items.Add(new StockAdjustmentItemCommand(
                item.ProductId, item.VariantId, item.BatchId, line.Direction == "out" ? -line.Quantity.Value : line.Quantity.Value, line.UnitCost, line.Notes));
        }

        if (items.Count == 0 && ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Add at least one line.");
        }

        if (!ModelState.IsValid)
        {
            return await AdjustmentFormAsync(form, cancellationToken);
        }

        var created = await adjustments.CreateAsync(new CreateStockAdjustmentCommand(form.WarehouseId, form.AdjustmentDate, form.Reason, form.Notes, items), cancellationToken);
        if (created.IsFailure)
        {
            logger.LogInformation("Stock adjustment rejected: {ErrorCode}", created.Error.Code);
            AddErrorToModelState(created.Error);
            return await AdjustmentFormAsync(form, cancellationToken);
        }

        if (form.PostNow)
        {
            var posted = await adjustments.PostAsync(created.Value.Id, cancellationToken);
            if (posted.IsFailure)
            {
                NotifyWarning($"Adjustment {created.Value.Number} was saved as a draft but could not be posted: {posted.Error.Description}");
                return RedirectToAction(nameof(AdjustmentDetails), new { id = created.Value.Id });
            }
        }

        NotifySuccess($"Adjustment {created.Value.Number} {(form.PostNow ? "posted" : "saved as draft")}.");
        return RedirectToAction(nameof(AdjustmentDetails), new { id = created.Value.Id });
    }

    // POST /Inventory/PostAdjustment/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    public async Task<IActionResult> PostAdjustment(Guid id, CancellationToken cancellationToken)
    {
        Report(await adjustments.PostAsync(id, cancellationToken), "Adjustment posted: stock updated.");
        return RedirectToAction(nameof(AdjustmentDetails), new { id });
    }

    // POST /Inventory/CancelAdjustment/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    public async Task<IActionResult> CancelAdjustment(Guid id, CancellationToken cancellationToken)
    {
        Report(await adjustments.CancelAsync(id, cancellationToken), "Adjustment cancelled.");
        return RedirectToAction(nameof(AdjustmentDetails), new { id });
    }

    // ======================= Transfers =======================

    // GET /Inventory/Transfers
    [HttpGet]
    public async Task<IActionResult> Transfers(string? search, Guid? warehouseId, StockTransferStatus? status, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await transfers.ListAsync(
            new StockTransferQuery { Search = search, WarehouseId = warehouseId, Status = status, Page = page, PageSize = PageSize }, cancellationToken);
        if (result.IsFailure)
        {
            return ErrorResult(result.Error);
        }

        ViewData["Search"] = search;
        ViewData["WarehouseId"] = warehouseId;
        ViewData["Status"] = status;
        ViewData["Warehouses"] = await WarehouseOptionsAsync(cancellationToken);
        return View(result.Value);
    }

    // GET /Inventory/TransferDetails/{id}
    [HttpGet]
    public async Task<IActionResult> TransferDetails(Guid id, CancellationToken cancellationToken)
    {
        var result = await transfers.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Inventory/CreateTransfer
    [HttpGet]
    [RequirePermission(Permissions.Inventory.StockTransfer)]
    public async Task<IActionResult> CreateTransfer(CancellationToken cancellationToken) =>
        await TransferFormAsync(new TransferFormModel(), cancellationToken);

    // POST /Inventory/CreateTransfer
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockTransfer)]
    public async Task<IActionResult> CreateTransfer(TransferFormModel form, CancellationToken cancellationToken)
    {
        var items = new List<StockTransferItemCommand>();
        foreach (var (line, index) in form.Lines.Select((line, index) => (line, index)).Where(pair => !pair.line.IsBlank))
        {
            if (line.Quantity is not > 0)
            {
                ModelState.AddModelError(string.Empty, $"Line {index + 1}: enter a quantity greater than zero.");
                continue;
            }

            if (await ResolveAsync(line, index + 1, cancellationToken) is { } item)
            {
                items.Add(new StockTransferItemCommand(item.ProductId, item.VariantId, item.BatchId, line.Quantity.Value));
            }
        }

        if (items.Count == 0 && ModelState.IsValid)
        {
            ModelState.AddModelError(string.Empty, "Add at least one line.");
        }

        if (!ModelState.IsValid)
        {
            return await TransferFormAsync(form, cancellationToken);
        }

        var created = await transfers.CreateAsync(new CreateStockTransferCommand(form.FromWarehouseId, form.ToWarehouseId, form.TransferDate, form.Notes, items), cancellationToken);
        if (created.IsFailure)
        {
            logger.LogInformation("Stock transfer rejected: {ErrorCode}", created.Error.Code);
            AddErrorToModelState(created.Error);
            return await TransferFormAsync(form, cancellationToken);
        }

        if (form.PostNow)
        {
            var posted = await transfers.PostAsync(created.Value.Id, cancellationToken);
            if (posted.IsFailure)
            {
                NotifyWarning($"Transfer {created.Value.Number} was saved as a draft but could not be posted: {posted.Error.Description}");
                return RedirectToAction(nameof(TransferDetails), new { id = created.Value.Id });
            }
        }

        NotifySuccess($"Transfer {created.Value.Number} {(form.PostNow ? "posted" : "saved as draft")}.");
        return RedirectToAction(nameof(TransferDetails), new { id = created.Value.Id });
    }

    // POST /Inventory/PostTransfer/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockTransfer)]
    public async Task<IActionResult> PostTransfer(Guid id, CancellationToken cancellationToken)
    {
        Report(await transfers.PostAsync(id, cancellationToken), "Transfer posted: stock moved.");
        return RedirectToAction(nameof(TransferDetails), new { id });
    }

    // POST /Inventory/CancelTransfer/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockTransfer)]
    public async Task<IActionResult> CancelTransfer(Guid id, CancellationToken cancellationToken)
    {
        Report(await transfers.CancelAsync(id, cancellationToken), "Transfer cancelled.");
        return RedirectToAction(nameof(TransferDetails), new { id });
    }

    // ======================= Movements =======================

    // GET /Inventory/Movements
    [HttpGet]
    public async Task<IActionResult> Movements(string? sku, Guid? warehouseId, StockMovementType? type, DateOnly? from, DateOnly? to, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await LoadMovementsAsync(sku, warehouseId, type, from, to, page, cancellationToken);
        ViewData["Sku"] = sku;
        ViewData["WarehouseId"] = warehouseId;
        ViewData["Type"] = type;
        ViewData["From"] = from;
        ViewData["To"] = to;
        ViewData["Warehouses"] = await WarehouseOptionsAsync(cancellationToken);
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // ======================= Batches =======================

    // GET /Inventory/Batches
    [HttpGet]
    public async Task<IActionResult> Batches(string? search, string? view, int days = 30, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await batches.ListAsync(BatchQuery(search, view, days, page), cancellationToken);
        ViewData["Search"] = search;
        ViewData["View"] = view;
        ViewData["Days"] = days;
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Inventory/BatchDetails/{id}
    [HttpGet]
    public async Task<IActionResult> BatchDetails(Guid id, CancellationToken cancellationToken)
    {
        var batch = await batches.GetAsync(id, cancellationToken);
        if (batch.IsFailure)
        {
            return ErrorResult(batch.Error);
        }

        var batchStock = await batches.GetStockAsync(id, cancellationToken);
        return batchStock.IsSuccess
            ? View(new BatchDetailsViewModel { Batch = batch.Value, Stock = batchStock.Value })
            : ErrorResult(batchStock.Error);
    }

    // GET /Inventory/RegisterBatch
    [HttpGet]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    public IActionResult RegisterBatch() => View(new BatchFormModel());

    // POST /Inventory/RegisterBatch
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    public async Task<IActionResult> RegisterBatch(BatchFormModel form, CancellationToken cancellationToken)
    {
        var line = new StockLineInput { Sku = form.Sku };
        if (string.IsNullOrWhiteSpace(form.Sku))
        {
            ModelState.AddModelError(string.Empty, "Enter the SKU of the product or variant.");
            return View(form);
        }

        if (await ResolveAsync(line, 1, cancellationToken) is not { } item)
        {
            return View(form);
        }

        var result = await batches.RegisterAsync(new RegisterBatchCommand(item.ProductId, item.VariantId, form.BatchNumber, form.ManufactureDate, form.ExpiryDate), cancellationToken);
        if (result.IsFailure)
        {
            return Rejected("Batch register", result.Error, nameof(RegisterBatch), form);
        }

        NotifySuccess($"Batch '{result.Value.BatchNumber}' registered.");
        return RedirectToAction(nameof(BatchDetails), new { id = result.Value.Id });
    }

    // POST /Inventory/SetBatchStatus/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    public async Task<IActionResult> SetBatchStatus(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        Report(await batches.SetActiveAsync(id, isActive, cancellationToken), isActive ? "Batch activated." : "Batch deactivated.");
        return RedirectToAction(nameof(BatchDetails), new { id });
    }

    // ======================= Reports =======================

    // GET /Inventory/Reports?type=summary|warehouse|product|low|out|movements|expiry|valuation
    [HttpGet]
    [RequirePermission(Permissions.Inventory.ReportsView)]
    public async Task<IActionResult> Reports(
        string? type, string? search, Guid? warehouseId, int days = 30, DateOnly? from = null, DateOnly? to = null, int page = 1, CancellationToken cancellationToken = default)
    {
        type = ReportTypes.All.Any(report => report.Key == type) ? type! : ReportTypes.Summary;
        var itemQuery = new StockItemQuery { Search = search, WarehouseId = warehouseId, Page = page, PageSize = PageSize };

        InventoryDashboard? dashboard = null;
        InventoryValuation? valuation = null;
        IReadOnlyList<WarehouseStockSummary>? byWarehouse = null;
        Application.Common.Paging.PagedResult<StockItemDetails>? items = null;
        Application.Common.Paging.PagedResult<StockMovementDetails>? movements = null;
        Application.Common.Paging.PagedResult<BatchDetails>? expiring = null;
        Error? error = null;

        switch (type)
        {
            case ReportTypes.Summary:
                var summary = await reports.GetDashboardAsync(cancellationToken);
                (dashboard, error) = (summary.IsSuccess ? summary.Value : null, summary.IsFailure ? summary.Error : null);
                break;
            case ReportTypes.Warehouse:
                var perWarehouse = await reports.GetStockByWarehouseAsync(cancellationToken);
                (byWarehouse, error) = (perWarehouse.IsSuccess ? perWarehouse.Value : null, perWarehouse.IsFailure ? perWarehouse.Error : null);
                break;
            case ReportTypes.Valuation:
                var value = await reports.GetValuationAsync(warehouseId, cancellationToken);
                (valuation, error) = (value.IsSuccess ? value.Value : null, value.IsFailure ? value.Error : null);
                break;
            case ReportTypes.Product:
                var perProduct = await reports.GetStockByProductAsync(itemQuery, cancellationToken);
                (items, error) = (perProduct.IsSuccess ? perProduct.Value : null, perProduct.IsFailure ? perProduct.Error : null);
                break;
            case ReportTypes.LowStock:
                var low = await stock.ListLowStockAsync(itemQuery, cancellationToken);
                (items, error) = (low.IsSuccess ? low.Value : null, low.IsFailure ? low.Error : null);
                break;
            case ReportTypes.OutOfStock:
                var out_ = await stock.ListOutOfStockAsync(itemQuery, cancellationToken);
                (items, error) = (out_.IsSuccess ? out_.Value : null, out_.IsFailure ? out_.Error : null);
                break;
            case ReportTypes.Movements:
                var ledger = await LoadMovementsAsync(search, warehouseId, null, from, to, page, cancellationToken);
                (movements, error) = (ledger.IsSuccess ? ledger.Value : null, ledger.IsFailure ? ledger.Error : null);
                break;
            case ReportTypes.Expiry:
                var batchResult = await batches.ListAsync(new BatchListQuery { ExpiringWithinDays = days, IsActive = true, Page = page, PageSize = PageSize }, cancellationToken);
                (expiring, error) = (batchResult.IsSuccess ? batchResult.Value : null, batchResult.IsFailure ? batchResult.Error : null);
                break;
        }

        if (error is not null)
        {
            return ErrorResult(error);
        }

        return View(new ReportViewModel
        {
            Type = type,
            Search = search,
            WarehouseId = warehouseId,
            Days = days,
            From = from,
            To = to,
            Warehouses = await WarehouseOptionsAsync(cancellationToken),
            Dashboard = dashboard,
            Valuation = valuation,
            ByWarehouse = byWarehouse,
            Items = items,
            Movements = movements,
            Batches = expiring,
        });
    }

    // ======================= helpers =======================

    private static BatchListQuery BatchQuery(string? search, string? view, int days, int page) => new()
    {
        Search = search,
        ExpiredOnly = view == "expired",
        ExpiringWithinDays = view == "near" ? days : null,
        Page = page,
        PageSize = PageSize,
    };

    private async Task<Result<Application.Common.Paging.PagedResult<StockMovementDetails>>> LoadMovementsAsync(
        string? sku, Guid? warehouseId, StockMovementType? type, DateOnly? from, DateOnly? to, int page, CancellationToken cancellationToken)
    {
        Guid? productId = null;
        Guid? variantId = null;
        if (!string.IsNullOrWhiteSpace(sku))
        {
            if (await ResolveAsync(new StockLineInput { Sku = sku }, 1, cancellationToken) is { } item)
            {
                (productId, variantId) = (item.ProductId, item.VariantId);
            }
        }

        return await stock.ListMovementsAsync(
            new StockMovementQuery
            {
                ProductId = productId,
                VariantId = variantId,
                WarehouseId = warehouseId,
                Type = type,
                FromUtc = from is { } start ? new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
                ToUtc = to is { } end ? new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
                Page = page,
                PageSize = PageSize,
            },
            cancellationToken);
    }

    /// <summary>Finds the product or variant (by SKU) and batch (by number) a typed line means; adds a form error when it cannot.</summary>
    private async Task<(Guid ProductId, Guid? VariantId, Guid? BatchId)?> ResolveAsync(StockLineInput line, int number, CancellationToken cancellationToken)
    {
        var sku = line.Sku?.Trim();
        if (string.IsNullOrEmpty(sku))
        {
            ModelState.AddModelError(string.Empty, $"Line {number}: enter a SKU.");
            return null;
        }

        var found = await products.ListAsync(new ProductListQuery { Search = sku, PageSize = 25 }, cancellationToken);
        if (found.IsFailure)
        {
            ModelState.AddModelError(string.Empty, found.Error.Description);
            return null;
        }

        Guid? productId = null;
        Guid? variantId = null;
        var simple = found.Value.Items.FirstOrDefault(product => string.Equals(product.Sku, sku, StringComparison.OrdinalIgnoreCase));
        if (simple is { HasVariants: true })
        {
            ModelState.AddModelError(string.Empty, $"Line {number}: '{sku}' has variants; enter a variant SKU.");
            return null;
        }

        if (simple is not null)
        {
            productId = simple.Id;
        }
        else
        {
            foreach (var candidate in found.Value.Items.Where(product => product.HasVariants))
            {
                var variants = await products.ListVariantsAsync(candidate.Id, cancellationToken);
                if (variants.IsSuccess && variants.Value.FirstOrDefault(variant => string.Equals(variant.Sku, sku, StringComparison.OrdinalIgnoreCase)) is { } variant)
                {
                    (productId, variantId) = (candidate.Id, variant.Id);
                    break;
                }
            }
        }

        if (productId is null)
        {
            ModelState.AddModelError(string.Empty, $"Line {number}: no product or variant with SKU '{sku}'.");
            return null;
        }

        Guid? batchId = null;
        if (!string.IsNullOrWhiteSpace(line.BatchNumber))
        {
            var batchList = await batches.ListAsync(
                new BatchListQuery { Search = line.BatchNumber.Trim(), ProductId = productId, VariantId = variantId, PageSize = 25 }, cancellationToken);
            var batch = batchList.IsSuccess
                ? batchList.Value.Items.FirstOrDefault(candidate => string.Equals(candidate.BatchNumber, line.BatchNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                : null;
            if (batch is null)
            {
                ModelState.AddModelError(string.Empty, $"Line {number}: batch '{line.BatchNumber.Trim()}' does not exist for '{sku}'. Register it first.");
                return null;
            }

            batchId = batch.Id;
        }

        return (productId.Value, variantId, batchId);
    }

    private IActionResult Rejected(string what, Error error, string view, object form)
    {
        logger.LogInformation("{Operation} rejected: {ErrorCode}", what, error.Code);
        AddErrorToModelState(error);
        return View(view, form);
    }

    private void Report<T>(Result<T> result, string success)
    {
        if (result.IsFailure)
        {
            logger.LogInformation("Inventory action rejected: {ErrorCode}", result.Error.Code);
            NotifyError(result.Error.Description);
        }
        else
        {
            NotifySuccess(success);
        }
    }

    private async Task<IReadOnlyList<SelectListItem>> WarehouseOptionsAsync(CancellationToken cancellationToken)
    {
        var result = await warehouses.ListAsync(new WarehouseListQuery { PageSize = 100 }, cancellationToken);
        return result.IsFailure
            ? []
            : result.Value.Items.Select(warehouse => new SelectListItem($"{warehouse.Name} ({warehouse.Code}){(warehouse.IsActive ? string.Empty : " - inactive")}", warehouse.Id.ToString())).ToList();
    }

    private async Task<IActionResult> AdjustmentFormAsync(AdjustmentFormModel form, CancellationToken cancellationToken)
    {
        form.PadLines();
        form.Warehouses = await WarehouseOptionsAsync(cancellationToken);
        return View("AdjustmentForm", form);
    }

    private async Task<IActionResult> TransferFormAsync(TransferFormModel form, CancellationToken cancellationToken)
    {
        form.PadLines();
        form.Warehouses = await WarehouseOptionsAsync(cancellationToken);
        return View("TransferForm", form);
    }
}
