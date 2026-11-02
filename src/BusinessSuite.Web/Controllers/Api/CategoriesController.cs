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
/// Product categories: a tree per organization. Version 1: <c>/api/v1/categories</c>. Reads need <c>inventory.view</c>, changes
/// <c>inventory.product.manage</c>. Everything happens in the organization the request acts in (<c>X-Organization-Id</c>, or the caller's
/// only organization); another organization's category answers 404 <c>Categories.NotFound</c>. See docs/API.md §10.
/// </summary>
[Route("api/v1/categories")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class CategoriesController(ICategoryService categories) : AppApiController
{
    // GET /api/v1/categories
    /// <summary>Lists categories with search, parent filter and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<CategoryDetails>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] CategoryListRequest request, CancellationToken cancellationToken)
    {
        var result = await categories.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/categories/tree
    /// <summary>The whole category hierarchy (each node with its sub-categories).</summary>
    /// <param name="includeInactive">Include retired categories (default: no).</param>
    [HttpGet("tree")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<IReadOnlyList<CategoryNode>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Tree([FromQuery] bool includeInactive, CancellationToken cancellationToken)
    {
        var result = await categories.GetTreeAsync(includeInactive, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/categories/{id}
    /// <summary>Gets one category with its child and product counts.</summary>
    /// <response code="404">No such category in the organization (<c>Categories.NotFound</c>).</response>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<CategoryDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await categories.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/categories
    /// <summary>Creates a category (under a parent, or at the top level).</summary>
    /// <response code="201">Created; <c>Location</c> points to it.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Categories.Invalid</c>).</response>
    /// <response code="409">The parent already has a category with this name or code (<c>Categories.Duplicate</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<CategoryDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await categories.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/categories/{id}
    /// <summary>Replaces a category's values; a different <c>parentCategoryId</c> moves it (never under itself).</summary>
    /// <response code="404">No such category (<c>Categories.NotFound</c>).</response>
    /// <response code="409">Duplicate name or code (<c>Categories.Duplicate</c>) or changed concurrently (<c>Catalog.ConcurrentChange</c>).</response>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<CategoryDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await categories.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/categories/{id}/status
    /// <summary>Activates or retires a category.</summary>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<CategoryDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeActiveStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await categories.SetActiveAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/categories/{id}
    /// <summary>Deletes a category that has no sub-categories, products or attributes.</summary>
    /// <response code="204">Deleted.</response>
    /// <response code="409">Still in use (<c>Categories.InUse</c>): deactivate it instead.</response>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await categories.DeleteAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }
}
