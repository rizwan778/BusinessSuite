using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// The inventory dashboard and reports. Version 1: <c>/api/v1/inventory/...</c>. Read-only, needs <c>inventory.reports.view</c> (it shows costs and values), and only
/// ever shows the organization the request acts in. Valuation is quantity on hand times the moving average cost. See docs/API.md §10.
/// </summary>
[Route("api/v1/inventory")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class InventoryReportsController(IInventoryReportService reports) : AppApiController
{
    // GET /api/v1/inventory/dashboard
    /// <summary>
    /// Inventory summary: total products, variants and warehouses, inventory value, low-stock and out-of-stock counts and the top 5 of
    /// each, the 10 latest stock movements, and the batches expiring within 30 days.
    /// </summary>
    [HttpGet("dashboard")]
    [RequirePermission(Permissions.Inventory.ReportsView)]
    [ProducesResponseType<InventoryDashboard>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard(CancellationToken cancellationToken)
    {
        var result = await reports.GetDashboardAsync(cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/inventory/reports/valuation
    /// <summary>Inventory valuation: total quantity and value, and per warehouse (optionally for one warehouse).</summary>
    /// <response code="404">The warehouse does not exist in the organization (<c>Warehouses.NotFound</c>).</response>
    [HttpGet("reports/valuation")]
    [RequirePermission(Permissions.Inventory.ReportsView)]
    [ProducesResponseType<InventoryValuation>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Valuation([FromQuery] ValuationRequest request, CancellationToken cancellationToken)
    {
        var result = await reports.GetValuationAsync(request.WarehouseId, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/inventory/reports/stock-by-warehouse
    /// <summary>Warehouse-wise stock: what each warehouse holds (lines, quantity, reserved) and is worth.</summary>
    [HttpGet("reports/stock-by-warehouse")]
    [RequirePermission(Permissions.Inventory.ReportsView)]
    [ProducesResponseType<IReadOnlyList<WarehouseStockSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StockByWarehouse(CancellationToken cancellationToken)
    {
        var result = await reports.GetStockByWarehouseAsync(cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/inventory/reports/stock-by-product
    /// <summary>Product-wise stock: each product (or variant) with its total on hand, reserved, available and value, with paging.</summary>
    /// <response code="404">The warehouse does not exist in the organization (<c>Warehouses.NotFound</c>).</response>
    [HttpGet("reports/stock-by-product")]
    [RequirePermission(Permissions.Inventory.ReportsView)]
    [ProducesResponseType<PagedResult<StockItemDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> StockByProduct([FromQuery] StockItemListRequest request, CancellationToken cancellationToken)
    {
        var result = await reports.GetStockByProductAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
