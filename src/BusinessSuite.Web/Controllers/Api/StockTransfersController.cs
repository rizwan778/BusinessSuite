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
/// Stock transfers between two warehouses. Version 1: <c>/api/v1/stock-transfers</c>. Reads need <c>inventory.view</c>, changes
/// <c>inventory.stock.transfer</c>. A new transfer is a <b>draft</b> that can be cancelled. <b>Posting</b> reduces the source warehouse,
/// increases the destination, writes both ledger movements per line and completes the transfer in ONE transaction: it succeeds
/// entirely or changes nothing (409 <c>Stock.InsufficientStock</c> when the source lacks stock). The stock keeps the cost it had at
/// the source. See docs/API.md §10.
/// </summary>
[Route("api/v1/stock-transfers")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class StockTransfersController(IStockTransferService transfers) : AppApiController
{
    // GET /api/v1/stock-transfers
    /// <summary>Lists transfers (newest first) with filters and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<StockTransferSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] StockTransferListRequest request, CancellationToken cancellationToken)
    {
        var result = await transfers.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/stock-transfers/{id}
    /// <summary>Gets a transfer with its lines (and, once posted, the ledger movements of each).</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<StockTransferDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await transfers.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/stock-transfers
    /// <summary>Creates a draft transfer. Both warehouses must be active and different; every line is checked now.</summary>
    /// <response code="400">Invalid input (<c>Transfers.Invalid</c>, <c>Stock.ItemInvalid</c>, <c>Warehouses.Inactive</c>).</response>
    /// <response code="404">A warehouse does not exist in the organization (<c>Warehouses.NotFound</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.StockTransfer)]
    [ProducesResponseType<StockTransferDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(CreateStockTransferRequest request, CancellationToken cancellationToken)
    {
        var result = await transfers.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/stock-transfers/{id}/post
    /// <summary>Posts a draft: moves the stock between the two warehouses atomically.</summary>
    /// <response code="409">Not a draft (<c>Transfers.NotDraft</c>), not enough stock at the source (<c>Stock.InsufficientStock</c>) or changed concurrently (<c>Stock.ConcurrentChange</c>).</response>
    [HttpPost("{id:guid}/post")]
    [RequirePermission(Permissions.Inventory.StockTransfer)]
    [ProducesResponseType<StockTransferDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Post(Guid id, CancellationToken cancellationToken)
    {
        var result = await transfers.PostAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/stock-transfers/{id}/cancel
    /// <summary>Cancels a draft transfer.</summary>
    /// <response code="409">Not a draft (<c>Transfers.NotDraft</c>).</response>
    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Inventory.StockTransfer)]
    [ProducesResponseType<StockTransferDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var result = await transfers.CancelAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
