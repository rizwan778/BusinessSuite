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
/// Stock adjustments: counts, write-offs, found goods, and the way opening stock is entered. Version 1:
/// <c>/api/v1/stock-adjustments</c>. Reads need <c>inventory.view</c>, changes <c>inventory.stock.adjust</c>.
/// A new adjustment is a <b>draft</b> that can be cancelled. <b>Posting</b> writes one ledger movement per line and updates the
/// balances in ONE transaction: every line applies or none does (409 <c>Stock.InsufficientStock</c> when stock would go
/// negative). A posted adjustment is final; correct it with a new one. See docs/API.md §10.
/// </summary>
[Route("api/v1/stock-adjustments")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class StockAdjustmentsController(IStockAdjustmentService adjustments) : AppApiController
{
    // GET /api/v1/stock-adjustments
    /// <summary>Lists adjustments (newest first) with filters and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<StockAdjustmentSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] StockAdjustmentListRequest request, CancellationToken cancellationToken)
    {
        var result = await adjustments.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/stock-adjustments/{id}
    /// <summary>Gets an adjustment with its lines (and, once posted, the ledger movement of each).</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<StockAdjustmentDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await adjustments.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/stock-adjustments
    /// <summary>Creates a draft adjustment. Every line is checked now: product, variant and batch must fit, quantities must match the unit.</summary>
    /// <response code="201">Created as a draft; <c>Location</c> points to it.</response>
    /// <response code="400">Invalid input (<c>Adjustments.Invalid</c>, <c>Stock.ItemInvalid</c>, <c>Warehouses.Inactive</c>).</response>
    /// <response code="404">The warehouse does not exist in the organization (<c>Warehouses.NotFound</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    [ProducesResponseType<StockAdjustmentDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(CreateStockAdjustmentRequest request, CancellationToken cancellationToken)
    {
        var result = await adjustments.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/stock-adjustments/{id}/post
    /// <summary>Posts a draft: writes the ledger movements and updates the balances, all lines or none.</summary>
    /// <response code="409">Not a draft (<c>Adjustments.NotDraft</c>), not enough stock (<c>Stock.InsufficientStock</c>) or changed concurrently (<c>Stock.ConcurrentChange</c>).</response>
    [HttpPost("{id:guid}/post")]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    [ProducesResponseType<StockAdjustmentDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Post(Guid id, CancellationToken cancellationToken)
    {
        var result = await adjustments.PostAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/stock-adjustments/{id}/cancel
    /// <summary>Cancels a draft. A posted adjustment cannot be cancelled: correct it with a new adjustment.</summary>
    /// <response code="409">Not a draft (<c>Adjustments.NotDraft</c>).</response>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    [ProducesResponseType<StockAdjustmentDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var result = await adjustments.CancelAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
