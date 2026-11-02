using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Products;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// Product catalogue pages: list, details, create/edit with a form generated from the organization's attribute schema, and
/// variants. Thin: every rule (including attribute validation) lives in <see cref="IProductService"/>.
/// </summary>
[RequirePermission(Permissions.Inventory.View)]
public sealed class ProductsController(
    IProductService products,
    IAttributeService attributes,
    ICategoryService categories,
    IUnitService units,
    ILogger<ProductsController> logger) : AppController
{
    // GET /Products
    [HttpGet]
    public async Task<IActionResult> Index(ProductListFilter filter, CancellationToken cancellationToken)
    {
        var result = await products.ListAsync(filter.ToQuery(), cancellationToken);
        if (result.IsFailure)
        {
            return ErrorResult(result.Error);
        }

        return View(new ProductListViewModel
        {
            Filter = filter,
            Products = result.Value,
            CategoryOptions = await CategoryOptionsAsync(cancellationToken),
        });
    }

    // GET /Products/Details/{id}
    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var result = await products.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Products/Create
    [HttpGet]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Create(Guid? categoryId, CancellationToken cancellationToken)
    {
        var form = new ProductFormModel { CategoryId = categoryId };
        return await FormViewAsync(form, null, cancellationToken);
    }

    // POST /Products/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Create(ProductFormModel form, CancellationToken cancellationToken)
    {
        if (form.ReloadSchema || !ModelState.IsValid)
        {
            return await FormViewAsync(form, null, cancellationToken, clearModelState: form.ReloadSchema);
        }

        var result = await products.CreateAsync(form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(form, result.Error, cancellationToken);
        }

        NotifySuccess($"Product '{result.Value.Name}' created.");
        return RedirectToAction(nameof(Details), new { id = result.Value.Id });
    }

    // GET /Products/Edit/{id}
    [HttpGet]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var result = await products.GetAsync(id, cancellationToken);
        if (result.IsFailure)
        {
            return ErrorResult(result.Error);
        }

        return await FormViewAsync(ProductFormModel.From(result.Value), result.Value.Attributes, cancellationToken);
    }

    // POST /Products/Edit/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Edit(Guid id, ProductFormModel form, CancellationToken cancellationToken)
    {
        form.Id = id;
        if (form.ReloadSchema || !ModelState.IsValid)
        {
            return await FormViewAsync(form, null, cancellationToken, clearModelState: form.ReloadSchema);
        }

        var result = await products.UpdateAsync(id, form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Type == ErrorType.NotFound ? ErrorResult(result.Error) : await FailAsync(form, result.Error, cancellationToken);
        }

        NotifySuccess($"Product '{result.Value.Name}' saved.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Products/SetStatus/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> SetStatus(Guid id, bool isActive, string? returnUrl, CancellationToken cancellationToken)
    {
        var result = await products.SetActiveAsync(id, isActive, cancellationToken);
        if (result.IsFailure)
        {
            NotifyError(result.Error.Description);
        }
        else
        {
            NotifySuccess($"Product '{result.Value.Name}' {(isActive ? "activated" : "deactivated")}.");
        }

        return string.IsNullOrEmpty(returnUrl) ? RedirectToAction(nameof(Details), new { id }) : RedirectToLocal(returnUrl);
    }

    // GET /Products/CreateVariant?productId=
    [HttpGet]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> CreateVariant(Guid productId, CancellationToken cancellationToken) =>
        await VariantFormViewAsync(new VariantFormModel { ProductId = productId }, null, cancellationToken);

    // POST /Products/CreateVariant
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> CreateVariant(VariantFormModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return await VariantFormViewAsync(form, null, cancellationToken);
        }

        var result = await products.CreateVariantAsync(form.ProductId, form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Type == ErrorType.NotFound ? ErrorResult(result.Error) : await FailVariantAsync(form, result.Error, cancellationToken);
        }

        NotifySuccess($"Variant '{result.Value.Sku}' added.");
        return RedirectToAction(nameof(Details), new { id = form.ProductId });
    }

    // GET /Products/EditVariant?productId=&variantId=
    [HttpGet]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> EditVariant(Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var result = await products.GetVariantAsync(productId, variantId, cancellationToken);
        return result.IsSuccess
            ? await VariantFormViewAsync(VariantFormModel.From(result.Value), result.Value.Attributes, cancellationToken)
            : ErrorResult(result.Error);
    }

    // POST /Products/EditVariant
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> EditVariant(VariantFormModel form, CancellationToken cancellationToken)
    {
        if (form.VariantId is not { } variantId)
        {
            return BadRequest();
        }

        if (!ModelState.IsValid)
        {
            return await VariantFormViewAsync(form, null, cancellationToken);
        }

        var result = await products.UpdateVariantAsync(form.ProductId, variantId, form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Type == ErrorType.NotFound ? ErrorResult(result.Error) : await FailVariantAsync(form, result.Error, cancellationToken);
        }

        NotifySuccess($"Variant '{result.Value.Sku}' saved.");
        return RedirectToAction(nameof(Details), new { id = form.ProductId });
    }

    // POST /Products/SetVariantStatus
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> SetVariantStatus(Guid productId, Guid variantId, bool isActive, CancellationToken cancellationToken)
    {
        var result = await products.SetVariantActiveAsync(productId, variantId, isActive, cancellationToken);
        if (result.IsFailure)
        {
            NotifyError(result.Error.Description);
        }
        else
        {
            NotifySuccess($"Variant '{result.Value.Sku}' {(isActive ? "activated" : "deactivated")}.");
        }

        return RedirectToAction(nameof(Details), new { id = productId });
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<IActionResult> FailAsync(ProductFormModel form, Error error, CancellationToken cancellationToken)
    {
        logger.LogInformation("Product save rejected: {ErrorCode}", error.Code);
        AddErrorToModelState(error);
        return await FormViewAsync(form, null, cancellationToken);
    }

    private async Task<IActionResult> FailVariantAsync(VariantFormModel form, Error error, CancellationToken cancellationToken)
    {
        logger.LogInformation("Variant save rejected: {ErrorCode}", error.Code);
        AddErrorToModelState(error);
        return await VariantFormViewAsync(form, null, cancellationToken);
    }

    /// <summary>Renders the product form with the attribute fields of the chosen category (values kept by code).</summary>
    private async Task<IActionResult> FormViewAsync(
        ProductFormModel form, IReadOnlyList<AttributeValueDetails>? stored, CancellationToken cancellationToken, bool clearModelState = false)
    {
        var schema = await attributes.GetSchemaAsync(form.CategoryId, cancellationToken);
        if (schema.IsFailure)
        {
            return ErrorResult(schema.Error);
        }

        form.Attributes = stored is null
            ? AttributeInputs.Build(schema.Value.ProductAttributes, form.Attributes)
            : AttributeInputs.BuildFromStored(schema.Value.ProductAttributes, stored);
        form.CategoryOptions = await CategoryOptionsAsync(cancellationToken);
        form.UnitOptions = await UnitOptionsAsync(cancellationToken);

        if (clearModelState)
        {
            ModelState.Clear(); // a category switch is not a save attempt: no errors, and the fields show the values just entered
        }

        ViewData.SetPageTitle(form.IsEdit ? "Edit product" : "New product");
        return View("Form", form);
    }

    private async Task<IActionResult> VariantFormViewAsync(
        VariantFormModel form, IReadOnlyList<AttributeValueDetails>? stored, CancellationToken cancellationToken)
    {
        var product = await products.GetAsync(form.ProductId, cancellationToken);
        if (product.IsFailure)
        {
            return ErrorResult(product.Error);
        }

        if (!product.Value.HasVariants)
        {
            NotifyWarning("This product does not have variants. Turn on 'Has variants' first.");
            return RedirectToAction(nameof(Details), new { id = form.ProductId });
        }

        var schema = await attributes.GetSchemaAsync(product.Value.CategoryId, cancellationToken);
        if (schema.IsFailure)
        {
            return ErrorResult(schema.Error);
        }

        form.ProductName = product.Value.Name;
        form.Attributes = stored is null
            ? AttributeInputs.Build(schema.Value.VariantAttributes, form.Attributes)
            : AttributeInputs.BuildFromStored(schema.Value.VariantAttributes, stored);

        ViewData.SetPageTitle(form.IsEdit ? "Edit variant" : "New variant");
        return View("VariantForm", form);
    }

    private async Task<IReadOnlyList<SelectListItem>> CategoryOptionsAsync(CancellationToken cancellationToken) =>
        CategoryOptionBuilder.Build(await categories.GetTreeAsync(includeInactive: true, cancellationToken));

    private async Task<IReadOnlyList<SelectListItem>> UnitOptionsAsync(CancellationToken cancellationToken)
    {
        var result = await units.ListAsync(new UnitListQuery { IsActive = true, PageSize = 100 }, cancellationToken);
        return result.IsFailure
            ? []
            : result.Value.Items.Select(unit => new SelectListItem($"{unit.Name} ({unit.Code})", unit.Id.ToString())).ToList();
    }
}

/// <summary>Flattens the category tree into an indented drop-down list.</summary>
public static class CategoryOptionBuilder
{
    public static IReadOnlyList<SelectListItem> Build(Result<IReadOnlyList<CategoryNode>> tree, Guid? exclude = null)
    {
        var items = new List<SelectListItem>();
        if (tree.IsSuccess)
        {
            Add(tree.Value, 0, items, exclude);
        }

        return items;
    }

    private static void Add(IReadOnlyList<CategoryNode> nodes, int depth, List<SelectListItem> items, Guid? exclude)
    {
        foreach (var node in nodes.Where(node => node.Id != exclude))
        {
            var label = new string(' ', depth * 4) + (depth > 0 ? "↳ " : string.Empty) + node.Name + (node.IsActive ? string.Empty : " (inactive)");
            items.Add(new SelectListItem(label, node.Id.ToString()));
            Add(node.Children, depth + 1, items, exclude);
        }
    }
}
