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
/// Products and their variants. Version 1: <c>/api/v1/products</c>. Reads need <c>inventory.view</c>, changes
/// <c>inventory.product.manage</c>. One product model serves every industry: what makes a product a phone or a shirt are its category and
/// its <b>attribute values</b>, validated against the organization's schema (<c>GET /api/v1/product-attributes/schema</c>); an
/// invalid, unknown or missing-required value answers 400 <c>Products.AttributeValuesInvalid</c> listing every problem. A
/// product is <b>simple</b> (own SKU and stock) or has <b>variants</b> (each with its own SKU, barcode, prices, attribute values
/// and stock). Products with stock history are deactivated, not deleted. See docs/API.md §10.
/// </summary>
[Route("api/v1/products")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
public sealed class ProductsController(IProductService products) : AppApiController
{
    // GET /api/v1/products
    /// <summary>Searches products (also by variant SKU/barcode) with category, status, variant, stock-tracking and attribute-option filters and paging.</summary>
    [HttpGet]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<PagedResult<ProductSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] ProductListRequest request, CancellationToken cancellationToken)
    {
        var result = await products.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/products/{id}
    /// <summary>Gets a product with its attribute values, variants (with theirs) and stock totals.</summary>
    /// <response code="404">No such product in the organization (<c>Products.NotFound</c>).</response>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<ProductDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await products.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/products
    /// <summary>Creates a product with its attribute values.</summary>
    /// <response code="201">Created; <c>Location</c> points to it.</response>
    /// <response code="400">Invalid input (<c>Products.Invalid</c>, <c>Products.ReferenceInvalid</c>, <c>Products.AttributeValuesInvalid</c>).</response>
    /// <response code="409">The SKU or barcode is taken by a product or variant (<c>Products.DuplicateSku</c>, <c>Products.DuplicateBarcode</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<ProductDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(SaveProductRequest request, CancellationToken cancellationToken)
    {
        var result = await products.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/products/{id}
    /// <summary>
    /// Replaces a product's values and attribute values. Once stock exists its unit, variants setting and stock/batch tracking are
    /// fixed (409 <c>Products.HasStock</c>).
    /// </summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<ProductDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, SaveProductRequest request, CancellationToken cancellationToken)
    {
        var result = await products.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/products/{id}/status
    /// <summary>Activates or retires a product (retired products can no longer receive stock).</summary>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<ProductDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeActiveStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await products.SetActiveAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/products/{id}
    /// <summary>Deletes a product (with its variants and attribute values) that no stock, batch or document refers to.</summary>
    /// <response code="409">Stock history exists (<c>Products.InUse</c>): deactivate it instead.</response>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await products.DeleteAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }

    // GET /api/v1/products/{productId}/variants
    /// <summary>Lists a product's variants with their attribute values and stock.</summary>
    [HttpGet("{productId:guid}/variants")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<IReadOnlyList<VariantDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> ListVariants(Guid productId, CancellationToken cancellationToken)
    {
        var result = await products.ListVariantsAsync(productId, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/products/{productId}/variants/{variantId}
    /// <summary>Gets one variant.</summary>
    /// <response code="404">No such variant for this product (<c>Variants.NotFound</c>).</response>
    [HttpGet("{productId:guid}/variants/{variantId:guid}")]
    [RequirePermission(Permissions.Inventory.View)]
    [ProducesResponseType<VariantDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> GetVariant(Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var result = await products.GetVariantAsync(productId, variantId, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/products/{productId}/variants
    /// <summary>Creates a variant with its own SKU and variant-level attribute values (size, colour...).</summary>
    /// <response code="400">The product has no variants (<c>Variants.NotAllowed</c>) or a value is invalid.</response>
    /// <response code="409">The SKU or barcode is taken (<c>Products.DuplicateSku</c>, <c>Products.DuplicateBarcode</c>).</response>
    [HttpPost("{productId:guid}/variants")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<VariantDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> CreateVariant(Guid productId, SaveVariantRequest request, CancellationToken cancellationToken)
    {
        var result = await products.CreateVariantAsync(productId, request.ToCommand(), cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetVariant), new { productId, variantId = result.Value.Id }, result.Value)
            : ErrorResult(result.Error);
    }

    // PUT /api/v1/products/{productId}/variants/{variantId}
    /// <summary>Replaces a variant's values and attribute values.</summary>
    [HttpPut("{productId:guid}/variants/{variantId:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<VariantDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> UpdateVariant(Guid productId, Guid variantId, SaveVariantRequest request, CancellationToken cancellationToken)
    {
        var result = await products.UpdateVariantAsync(productId, variantId, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/products/{productId}/variants/{variantId}/status
    /// <summary>Activates or retires a variant.</summary>
    [HttpPatch("{productId:guid}/variants/{variantId:guid}/status")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType<VariantDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeVariantStatus(Guid productId, Guid variantId, ChangeActiveStatusRequest request, CancellationToken cancellationToken)
    {
        var result = await products.SetVariantActiveAsync(productId, variantId, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/products/{productId}/variants/{variantId}
    /// <summary>Deletes a variant that no stock, batch or document refers to.</summary>
    /// <response code="409">Stock history exists (<c>Variants.InUse</c>): deactivate it instead.</response>
    [HttpDelete("{productId:guid}/variants/{variantId:guid}")]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> DeleteVariant(Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var result = await products.DeleteVariantAsync(productId, variantId, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }
}
