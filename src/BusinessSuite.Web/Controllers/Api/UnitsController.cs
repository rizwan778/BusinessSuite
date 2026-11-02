using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// Units of measure and unit conversions. Version 1: <c>/api/v1/units</c>. Reads need <c>inventory.view</c>, changes
/// <c>inventory.product.manage</c>. A unit says how fine a quantity may be (<c>decimalPlaces</c>); a conversion says how many of one unit
/// make another, generally (1 kg = 1000 g) or for one product (1 carton = 24 pcs). See docs/API.md §10.
/// </summary>
[Route("api/v1/units")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class UnitsController(IUnitService units) : AppApiController
{
    // GET /api/v1/units
    /// <summary>Lists units with search and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<UnitDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] UnitListRequest request, CancellationToken cancellationToken)
    {
        var result = await units.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/units/{id}
    /// <summary>Gets one unit.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<UnitDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await units.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/units
    /// <summary>Creates a unit.</summary>
    /// <response code="409">The code or name is taken (<c>Units.Duplicate</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<UnitDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(SaveUnitRequest request, CancellationToken cancellationToken)
    {
        var result = await units.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/units/{id}
    /// <summary>Replaces a unit's values. Decimal places cannot be reduced once products use the unit (<c>Units.DecimalPlacesInUse</c>).</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<UnitDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, SaveUnitRequest request, CancellationToken cancellationToken)
    {
        var result = await units.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/units/{id}/status
    /// <summary>Activates or retires a unit.</summary>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<UnitDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeActiveStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await units.SetActiveAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/units/{id}
    /// <summary>Deletes a unit that no product or conversion uses.</summary>
    /// <response code="409">Still in use (<c>Units.InUse</c>): deactivate it instead.</response>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await units.DeleteAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }

    // GET /api/v1/units/conversions
    /// <summary>Lists conversions, general or of one product.</summary>
    [HttpGet("conversions")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<ConversionDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListConversions([FromQuery] ConversionListRequest request, CancellationToken cancellationToken)
    {
        var result = await units.ListConversionsAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/units/conversions/{id}
    /// <summary>Gets one conversion.</summary>
    [HttpGet("conversions/{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<ConversionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> GetConversion(Guid id, CancellationToken cancellationToken)
    {
        var result = await units.GetConversionAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/units/conversions
    /// <summary>Creates a conversion ("1 from-unit = factor x to-unit").</summary>
    /// <response code="404">A unit or the product does not exist in the organization.</response>
    /// <response code="409">The conversion already exists (<c>Units.ConversionDuplicate</c>).</response>
    [HttpPost("conversions")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<ConversionDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> CreateConversion(CreateConversionRequest request, CancellationToken cancellationToken)
    {
        var result = await units.CreateConversionAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(GetConversion), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/units/conversions/{id}
    /// <summary>Changes a conversion's factor. Its units and product are what it is: delete it and create another to change them.</summary>
    [HttpPut("conversions/{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<ConversionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> UpdateConversion(Guid id, UpdateConversionRequest request, CancellationToken cancellationToken)
    {
        var result = await units.UpdateConversionAsync(id, request.Factor!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/units/conversions/{id}
    /// <summary>Deletes a conversion.</summary>
    [HttpDelete("conversions/{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> DeleteConversion(Guid id, CancellationToken cancellationToken)
    {
        var result = await units.DeleteConversionAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }

    // GET /api/v1/units/convert
    /// <summary>
    /// Converts a quantity between two units using the stored conversions (a product's own conversion first, then a general one;
    /// the inverse of a stored conversion also works; no chaining through a third unit).
    /// </summary>
    /// <response code="404">A unit does not exist, or no conversion is defined between the two (<c>Units.ConversionUnavailable</c>).</response>
    [HttpGet("convert")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<ConversionResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Convert([FromQuery] ConvertRequest request, CancellationToken cancellationToken)
    {
        var result = await units.ConvertAsync(request.FromUnitId!.Value, request.ToUnitId!.Value, request.Quantity!.Value, request.ProductId, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
