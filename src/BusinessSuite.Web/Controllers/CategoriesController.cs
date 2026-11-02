using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Products;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers;

/// <summary>Product categories and sub-categories (a tree per organization).</summary>
[RequirePermission(Permissions.Inventory.View)]
public sealed class CategoriesController(ICategoryService categories, ILogger<CategoriesController> logger) : AppController
{
    // GET /Categories
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var tree = await categories.GetTreeAsync(includeInactive: true, cancellationToken);
        return tree.IsSuccess ? View(tree.Value) : ErrorResult(tree.Error);
    }

    // GET /Categories/Create?parentId=
    [HttpGet]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Create(Guid? parentId, CancellationToken cancellationToken) =>
        await FormViewAsync(new CategoryFormModel { ParentCategoryId = parentId }, cancellationToken);

    // POST /Categories/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Create(CategoryFormModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return await FormViewAsync(form, cancellationToken);
        }

        var result = await categories.CreateAsync(form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            logger.LogInformation("Category create rejected: {ErrorCode}", result.Error.Code);
            AddErrorToModelState(result.Error);
            return await FormViewAsync(form, cancellationToken);
        }

        NotifySuccess($"Category '{result.Value.Name}' created.");
        return RedirectToAction(nameof(Index));
    }

    // GET /Categories/Edit/{id}
    [HttpGet]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var result = await categories.GetAsync(id, cancellationToken);
        return result.IsSuccess ? await FormViewAsync(CategoryFormModel.From(result.Value), cancellationToken) : ErrorResult(result.Error);
    }

    // POST /Categories/Edit/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> Edit(Guid id, CategoryFormModel form, CancellationToken cancellationToken)
    {
        form.Id = id;
        if (!ModelState.IsValid)
        {
            return await FormViewAsync(form, cancellationToken);
        }

        var result = await categories.UpdateAsync(id, form.ToCommand(), cancellationToken);
        if (result.IsFailure)
        {
            logger.LogInformation("Category update rejected: {ErrorCode}", result.Error.Code);
            AddErrorToModelState(result.Error);
            return await FormViewAsync(form, cancellationToken);
        }

        NotifySuccess($"Category '{result.Value.Name}' saved.");
        return RedirectToAction(nameof(Index));
    }

    // POST /Categories/SetStatus/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.ProductManage)]
    public async Task<IActionResult> SetStatus(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        var result = await categories.SetActiveAsync(id, isActive, cancellationToken);
        if (result.IsFailure)
        {
            NotifyError(result.Error.Description);
        }
        else
        {
            NotifySuccess($"Category '{result.Value.Name}' {(isActive ? "activated" : "deactivated")}.");
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> FormViewAsync(CategoryFormModel form, CancellationToken cancellationToken)
    {
        form.ParentOptions = CategoryOptionBuilder.Build(await categories.GetTreeAsync(includeInactive: false, cancellationToken), exclude: form.Id);
        ViewData.SetPageTitle(form.IsEdit ? "Edit category" : "New category");
        return View("Form", form);
    }
}
