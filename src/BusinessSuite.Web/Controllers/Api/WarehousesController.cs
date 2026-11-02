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
/// Warehouses. Version 1: <c>/api/v1/warehouses</c>. Reads need <c>inventory.view</c>, changes <c>inventory.manage</c>.
/// Everything happens in the organization the request acts in; another organization's warehouse answers 404
/// <c>Warehouses.NotFound</c>. The first warehouse is the organization's <b>default</b>; there is never more than one, and the
/// default must stay active. A warehouse that holds or ever held stock is deactivated, not deleted. See docs/API.md §10.
/// </summary>
[Route("api/v1/warehouses")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class WarehousesController(IWarehouseService warehouses) : AppApiController
{
    // GET /api/v1/warehouses
    /// <summary>Lists warehouses (the default first) with search, filters and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<WarehouseDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] WarehouseListRequest request, CancellationToken cancellationToken)
    {
        var result = await warehouses.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/warehouses/default
    /// <summary>The organization's default warehouse.</summary>
    /// <response code="404">The organization has no warehouse yet (<c>Warehouses.NotFound</c>).</response>
    [HttpGet("default")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<WarehouseDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> GetDefault(CancellationToken cancellationToken)
    {
        var result = await warehouses.GetDefaultAsync(cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/warehouses/{id}
    /// <summary>Gets one warehouse with the number of stock lines it holds and their value.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<WarehouseDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await warehouses.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/warehouses
    /// <summary>Creates a warehouse. The first one becomes the default; later ones only with <c>makeDefault</c>.</summary>
    /// <response code="400">Invalid input, or the branch is not one of your organization's (<c>Warehouses.BranchInvalid</c>).</response>
    /// <response code="409">The code or name is taken (<c>Warehouses.Duplicate</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.Manage)]
    [ProducesResponseType<WarehouseDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(SaveWarehouseRequest request, CancellationToken cancellationToken)
    {
        var result = await warehouses.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/warehouses/{id}
    /// <summary>Replaces a warehouse's values; <c>makeDefault: true</c> makes it the default.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Inventory.Manage)]
    [ProducesResponseType<WarehouseDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, SaveWarehouseRequest request, CancellationToken cancellationToken)
    {
        var result = await warehouses.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/warehouses/{id}/status
    /// <summary>Activates or retires a warehouse (the default cannot be retired while others exist).</summary>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Inventory.Manage)]
    [ProducesResponseType<WarehouseDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeActiveStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await warehouses.SetActiveAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/warehouses/{id}/default
    /// <summary>Makes this warehouse the default; the previous default stops being it, atomically.</summary>
    /// <response code="400">The warehouse is retired (<c>Warehouses.DefaultMustBeActive</c>).</response>
    [HttpPost("{id:guid}/default")]
    [RequirePermission(Permissions.Inventory.Manage)]
    [ProducesResponseType<WarehouseDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> MakeDefault(Guid id, CancellationToken cancellationToken)
    {
        var result = await warehouses.SetDefaultAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/warehouses/{id}
    /// <summary>Deletes a warehouse no stock or document refers to (the default only when it is the last warehouse).</summary>
    /// <response code="409">Stock or documents refer to it (<c>Warehouses.InUse</c>): deactivate it instead.</response>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Inventory.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await warehouses.DeleteAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }
}
