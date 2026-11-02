using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Domain.Catalog;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Products;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// Attribute management: the organization defines the fields its products are described with (brand, RAM, size...) and, for
/// Dropdown/MultiSelect attributes, their options. Product forms are generated from these definitions.
/// </summary>
[RequirePermission(Permissions.Inventory.View)]
public sealed class AttributesController(IAttributeService attributes, ICategoryService categories, ILogger<AttributesController> logger) : AppController
{
    // GET /Attributes
    [HttpGet]
    public async Task<IActionResult> Index(string? search, CancellationToken cancellationToken)
    {
        var result = await attributes.ListAsync(new AttributeListQuery { Search = search, PageSize = 100 }, cancellationToken);
        ViewData["Search"] = search;
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Attributes/Details/{id}
    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken)
    {
        var result = await attributes.GetAsync(id, cancellationToken);
        return result.IsSuccess ? View(result.Value) : ErrorResult(result.Error);
    }

    // GET /Attributes/Create
    [HttpGet]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        await FormViewAsync(new AttributeFormModel(), cancellationToken);

    // POST /Attributes/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> Create(AttributeFormModel form, CancellationToken cancellationToken)
    {
        var result = await attributes.CreateAsync(form.ToCreateCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return await FailAsync(form, result.Error, cancellationToken);
        }

        NotifySuccess($"Attribute '{result.Value.Name}' created.");
        return RedirectToAction(nameof(Details), new { id = result.Value.Id });
    }

    // GET /Attributes/Edit/{id}
    [HttpGet]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var result = await attributes.GetAsync(id, cancellationToken);
        return result.IsSuccess ? await FormViewAsync(AttributeFormModel.From(result.Value), cancellationToken) : ErrorResult(result.Error);
    }

    // POST /Attributes/Edit/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> Edit(Guid id, AttributeFormModel form, CancellationToken cancellationToken)
    {
        form.Id = id;
        var result = await attributes.UpdateAsync(id, form.ToUpdateCommand(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.Type == ErrorType.NotFound ? ErrorResult(result.Error) : await FailAsync(form, result.Error, cancellationToken);
        }

        NotifySuccess($"Attribute '{result.Value.Name}' saved.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Attributes/SetStatus/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> SetStatus(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        Report(await attributes.SetActiveAsync(id, isActive, cancellationToken), isActive ? "Attribute activated." : "Attribute deactivated.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Attributes/AddOption/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> AddOption(Guid id, string? label, int sortOrder, CancellationToken cancellationToken)
    {
        Report(await attributes.AddOptionAsync(id, new SaveOptionCommand(label, sortOrder), cancellationToken), "Option added.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Attributes/SetOptionStatus/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> SetOptionStatus(Guid id, Guid optionId, string? label, int sortOrder, bool isActive, CancellationToken cancellationToken)
    {
        Report(
            await attributes.UpdateOptionAsync(id, optionId, new SaveOptionCommand(label, sortOrder, isActive), cancellationToken),
            isActive ? "Option activated." : "Option retired.");
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST /Attributes/DeleteOption/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequirePermission(Permissions.Inventory.AttributeManage)]
    public async Task<IActionResult> DeleteOption(Guid id, Guid optionId, CancellationToken cancellationToken)
    {
        Report(await attributes.DeleteOptionAsync(id, optionId, cancellationToken), "Option deleted.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private void Report<T>(Result<T> result, string success)
    {
        if (result.IsFailure)
        {
            NotifyError(result.Error.Description);
        }
        else
        {
            NotifySuccess(success);
        }
    }

    private async Task<IActionResult> FailAsync(AttributeFormModel form, Error error, CancellationToken cancellationToken)
    {
        logger.LogInformation("Attribute save rejected: {ErrorCode}", error.Code);
        AddErrorToModelState(error);
        return await FormViewAsync(form, cancellationToken);
    }

    private async Task<IActionResult> FormViewAsync(AttributeFormModel form, CancellationToken cancellationToken)
    {
        form.CategoryOptions = CategoryOptionBuilder.Build(await categories.GetTreeAsync(includeInactive: false, cancellationToken));
        ViewData.SetPageTitle(form.IsEdit ? "Edit attribute" : "New attribute");
        return View("Form", form);
    }
}
