using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Catalog;
using BusinessSuite.Web.Models.Api.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// Batches (lots) with expiry dates, for products that track batches. Version 1: <c>/api/v1/batches</c>. Reads need
/// <c>inventory.view</c>, changes <c>inventory.stock.adjust</c>. Register a batch, then bring stock into it with a stock adjustment that
/// names it. <c>expiringWithinDays</c> lists batches that still hold stock and expire soon (expired ones included), soonest first.
/// See docs/API.md §10.
/// </summary>
[Route("api/v1/batches")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class BatchesController(IBatchService batches) : AppApiController
{
    // GET /api/v1/batches
    /// <summary>Lists batches (soonest expiry first) with stock on hand and expiry status, with filters and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<BatchDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] BatchListRequest request, CancellationToken cancellationToken)
    {
        var result = await batches.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/batches/{id}
    /// <summary>Gets one batch.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<BatchDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await batches.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/batches/{id}/stock
    /// <summary>The batch's stock at each warehouse.</summary>
    [HttpGet("{id:guid}/stock")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<IReadOnlyList<BatchStockDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Stock(Guid id, CancellationToken cancellationToken)
    {
        var result = await batches.GetStockAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/batches
    /// <summary>Registers a batch of a product that tracks batches (an expiry date is required when the product requires one).</summary>
    /// <response code="400">The product does not track batches, the variant does not fit, or the dates are invalid (<c>Batches.Invalid</c>).</response>
    /// <response code="404">No such product in the organization (<c>Products.NotFound</c>).</response>
    /// <response code="409">The product already has a batch with this number (<c>Batches.Duplicate</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    [ProducesResponseType<BatchDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Register(RegisterBatchRequest request, CancellationToken cancellationToken)
    {
        var result = await batches.RegisterAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/batches/{id}/status
    /// <summary>Closes a batch (recalled, finished) or reopens it. A closed batch cannot receive stock.</summary>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Inventory.StockAdjust)]
    [ProducesResponseType<BatchDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeActiveStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await batches.SetActiveAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
