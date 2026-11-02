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
/// Stock levels and the movement history. Version 1: <c>/api/v1/stock</c> and <c>/api/v1/stock-movements</c>. Read-only, needs
/// <c>inventory.view</c>: stock changes only through stock adjustments and transfers (which write the ledger).
/// <b>Quantities:</b> <c>onHand</c> is what is there, <c>reserved</c> what is promised to open orders (0 until sales orders exist),
/// <c>available</c> = on hand - reserved. <b>Low stock</b>: a reorder level is set and on-hand stock is at or below it (out of
/// stock is low too). <b>Out of stock</b>: nothing on hand. Only active, stock-tracking items count. See docs/API.md §10.
/// </summary>
[Route("api/v1")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class StockController(IStockService stock) : AppApiController
{
    // GET /api/v1/stock
    /// <summary>Stock by product (or variant, or batch) and warehouse: on-hand, reserved and available quantities, average cost and value.</summary>
    [HttpGet("stock")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<StockBalanceDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Balances([FromQuery] StockBalanceListRequest request, CancellationToken cancellationToken)
    {
        var result = await stock.ListBalancesAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/stock/low
    /// <summary>Items at or below their reorder level (optionally counting one warehouse only).</summary>
    /// <response code="404">The warehouse does not exist in the organization (<c>Warehouses.NotFound</c>).</response>
    [HttpGet("stock/low")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<StockItemDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> LowStock([FromQuery] StockItemListRequest request, CancellationToken cancellationToken)
    {
        var result = await stock.ListLowStockAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/stock/out-of-stock
    /// <summary>Items with nothing on hand (optionally counting one warehouse only).</summary>
    [HttpGet("stock/out-of-stock")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<StockItemDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> OutOfStock([FromQuery] StockItemListRequest request, CancellationToken cancellationToken)
    {
        var result = await stock.ListOutOfStockAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/stock-movements
    /// <summary>
    /// The stock ledger, newest first, with filters. With <c>productId</c> (and optionally <c>variantId</c>, <c>warehouseId</c>) it is
    /// that product's transaction history, each line showing the quantity on hand after it.
    /// </summary>
    [HttpGet("stock-movements")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<StockMovementDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Movements([FromQuery] StockMovementListRequest request, CancellationToken cancellationToken)
    {
        var result = await stock.ListMovementsAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
