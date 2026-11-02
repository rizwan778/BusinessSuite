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
/// The organization's own product attributes (brand, RAM, size, colour, volume, warranty...). Version 1:
/// <c>/api/v1/product-attributes</c>. Reads need <c>inventory.view</c>, changes <c>inventory.attribute.manage</c>. Defining an attribute is
/// data, not a schema change: pick a data type (Text, Number, Decimal, Date, Boolean, Dropdown, MultiSelect) and a scope
/// (the product, or each variant). <c>GET .../schema</c> returns what a product form should ask for; products and variants send
/// their values by attribute <c>code</c> and the server validates them against this schema. See docs/API.md §10.
/// </summary>
[Route("api/v1/product-attributes")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class ProductAttributesController(IAttributeService attributes) : AppApiController
{
    // GET /api/v1/product-attributes
    /// <summary>Lists attribute definitions (with their options) with filters and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<AttributeDefinitionDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] AttributeListRequest request, CancellationToken cancellationToken)
    {
        var result = await attributes.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/product-attributes/schema
    /// <summary>
    /// The organization's attribute schema: the active attributes that apply to a product (with <c>categoryId</c>: of that
    /// category and its ancestors), split into product-level and variant-level, each with its options and rules.
    /// </summary>
    /// <response code="404">No such category (<c>Categories.NotFound</c>).</response>
    [HttpGet("schema")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<AttributeSchema>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Schema([FromQuery] AttributeSchemaRequest request, CancellationToken cancellationToken)
    {
        var result = await attributes.GetSchemaAsync(request.CategoryId, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/product-attributes/{id}
    /// <summary>Gets one attribute definition with its options.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<AttributeDefinitionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await attributes.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/product-attributes
    /// <summary>Defines an attribute (optionally with its first options). Code, data type and scope are fixed afterwards.</summary>
    /// <response code="409">The code is taken (<c>Attributes.Duplicate</c>) or two options share a label (<c>Attributes.OptionDuplicate</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    [ProducesResponseType<AttributeDefinitionDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(CreateAttributeRequest request, CancellationToken cancellationToken)
    {
        var result = await attributes.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/product-attributes/{id}
    /// <summary>Changes name, required flag, unit label, category limit, position and filterable flag.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    [ProducesResponseType<AttributeDefinitionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, UpdateAttributeRequest request, CancellationToken cancellationToken)
    {
        var result = await attributes.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/product-attributes/{id}/status
    /// <summary>Activates or retires an attribute (retired ones leave forms but keep their values).</summary>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    [ProducesResponseType<AttributeDefinitionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeActiveStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await attributes.SetActiveAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/product-attributes/{id}
    /// <summary>Deletes an attribute (and its options) that no product or variant has a value for.</summary>
    /// <response code="409">Values exist (<c>Attributes.InUse</c>): deactivate it instead.</response>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await attributes.DeleteAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }

    // POST /api/v1/product-attributes/{id}/options
    /// <summary>Adds an option to a Dropdown or MultiSelect attribute. Returns the attribute with all its options.</summary>
    /// <response code="409">The attribute already has this option (<c>Attributes.OptionDuplicate</c>).</response>
    [HttpPost("{id:guid}/options")]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    [ProducesResponseType<AttributeDefinitionDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> AddOption(Guid id, SaveOptionRequest request, CancellationToken cancellationToken)
    {
        var result = await attributes.AddOptionAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id }, result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/product-attributes/{id}/options/{optionId}
    /// <summary>Changes an option's label, position and active flag. Returns the attribute with all its options.</summary>
    [HttpPut("{id:guid}/options/{optionId:guid}")]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    [ProducesResponseType<AttributeDefinitionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> UpdateOption(Guid id, Guid optionId, SaveOptionRequest request, CancellationToken cancellationToken)
    {
        var result = await attributes.UpdateOptionAsync(id, optionId, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/product-attributes/{id}/options/{optionId}
    /// <summary>Deletes an option nothing uses. Returns the attribute with its remaining options.</summary>
    /// <response code="409">Products or variants use it (<c>Attributes.OptionInUse</c>): retire it instead.</response>
    [HttpDelete("{id:guid}/options/{optionId:guid}")]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    [ProducesResponseType<AttributeDefinitionDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> DeleteOption(Guid id, Guid optionId, CancellationToken cancellationToken)
    {
        var result = await attributes.DeleteOptionAsync(id, optionId, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
