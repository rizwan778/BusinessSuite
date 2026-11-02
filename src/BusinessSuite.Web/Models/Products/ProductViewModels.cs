using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Domain.Catalog;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BusinessSuite.Web.Models.Products;

/// <summary>Filters of the product list (query string).</summary>
public sealed class ProductListFilter
{
    public string? Search { get; set; }

    public Guid? CategoryId { get; set; }

    /// <summary><c>active</c>, <c>inactive</c> or empty for both.</summary>
    public string? Status { get; set; }

    public int Page { get; set; } = 1;

    public ProductListQuery ToQuery() => new()
    {
        Search = Search,
        CategoryId = CategoryId,
        IsActive = Status switch { "active" => true, "inactive" => false, _ => null },
        Page = Page,
        PageSize = 20,
    };
}

public sealed class ProductListViewModel
{
    public required ProductListFilter Filter { get; init; }

    public required PagedResult<ProductSummary> Products { get; init; }

    public IReadOnlyList<SelectListItem> CategoryOptions { get; init; } = [];
}

/// <summary>
/// One dynamic attribute field. The organization's schema decides which exist; only <see cref="Code"/> and the value fields are posted,
/// the rest is rebuilt from the schema on every request so a client cannot invent attributes or options.
/// </summary>
public sealed class AttributeInput
{
    public string? Code { get; set; }

    public string? Text { get; set; }

    public decimal? Number { get; set; }

    public DateOnly? Date { get; set; }

    public bool? Boolean { get; set; }

    public Guid? OptionId { get; set; }

    public List<Guid> OptionIds { get; set; } = [];

    [BindNever]
    public string? Name { get; set; }

    [BindNever]
    public AttributeDataType DataType { get; set; }

    [BindNever]
    public string? UnitLabel { get; set; }

    [BindNever]
    public bool IsRequired { get; set; }

    [BindNever]
    public IReadOnlyList<AttributeOptionDetails> Options { get; set; } = [];
}

/// <summary>Builds attribute fields from a schema and turns them into API commands. Pure.</summary>
public static class AttributeInputs
{
    /// <summary>One field per attribute of the schema, keeping the values posted for the same code.</summary>
    public static List<AttributeInput> Build(IEnumerable<AttributeDefinitionDetails> schema, IEnumerable<AttributeInput>? posted = null)
    {
        var byCode = (posted ?? []).Where(input => input.Code is not null)
            .GroupBy(input => input.Code!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return schema.Select(definition =>
        {
            byCode.TryGetValue(definition.Code, out var value);
            return new AttributeInput
            {
                Code = definition.Code,
                Name = definition.Name,
                DataType = definition.DataType,
                UnitLabel = definition.UnitLabel,
                IsRequired = definition.IsRequired,
                Options = definition.Options.Where(option => option.IsActive).OrderBy(option => option.SortOrder).ToList(),
                Text = value?.Text,
                Number = value?.Number,
                Date = value?.Date,
                Boolean = value?.Boolean,
                OptionId = value?.OptionId,
                OptionIds = value?.OptionIds ?? [],
            };
        }).ToList();
    }

    /// <summary>Like <see cref="Build(IEnumerable{AttributeDefinitionDetails}, IEnumerable{AttributeInput})"/>, filled with the values stored on a product or variant.</summary>
    public static List<AttributeInput> BuildFromStored(IEnumerable<AttributeDefinitionDetails> schema, IReadOnlyList<AttributeValueDetails> stored)
    {
        var posted = stored.Select(value => new AttributeInput
        {
            Code = value.Code,
            Text = value.Text,
            Number = value.Number,
            Date = value.Date,
            Boolean = value.Boolean,
            OptionId = value.Options.Count > 0 ? value.Options[0].Id : null,
            OptionIds = value.Options.Select(option => option.Id).ToList(),
        });

        return Build(schema, posted);
    }

    /// <summary>The commands for the fields that were filled in; the server validates them against the schema.</summary>
    public static List<AttributeValueCommand> ToCommands(IEnumerable<AttributeInput> inputs)
    {
        var commands = new List<AttributeValueCommand>();
        foreach (var input in inputs)
        {
            switch (input.DataType)
            {
                case AttributeDataType.Text when !string.IsNullOrWhiteSpace(input.Text):
                    commands.Add(new(input.Code, input.Text, null, null, null, null, null));
                    break;
                case AttributeDataType.Number or AttributeDataType.Decimal when input.Number is not null:
                    commands.Add(new(input.Code, null, input.Number, null, null, null, null));
                    break;
                case AttributeDataType.Date when input.Date is not null:
                    commands.Add(new(input.Code, null, null, input.Date, null, null, null));
                    break;
                case AttributeDataType.Boolean when input.Boolean is not null:
                    commands.Add(new(input.Code, null, null, null, input.Boolean, null, null));
                    break;
                case AttributeDataType.Dropdown when input.OptionId is not null:
                    commands.Add(new(input.Code, null, null, null, null, input.OptionId, null));
                    break;
                case AttributeDataType.MultiSelect when input.OptionIds.Count > 0:
                    commands.Add(new(input.Code, null, null, null, null, null, input.OptionIds));
                    break;
            }
        }

        return commands;
    }
}

/// <summary>Create/edit form of a product. Changing the category re-renders it with that category's attributes.</summary>
public sealed class ProductFormModel
{
    public Guid? Id { get; set; }

    public Guid? CategoryId { get; set; }

    public string? Sku { get; set; }

    public string? Barcode { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public Guid BaseUnitId { get; set; }

    public bool HasVariants { get; set; }

    public bool TracksStock { get; set; } = true;

    public bool TracksBatches { get; set; }

    public bool RequiresExpiry { get; set; }

    public bool AllowNegativeStock { get; set; }

    public decimal? ReorderLevel { get; set; }

    public decimal? CostPrice { get; set; }

    public decimal? SalePrice { get; set; }

    public List<AttributeInput> Attributes { get; set; } = [];

    /// <summary>Set by the category dropdown: re-render with the new category's attributes instead of saving.</summary>
    public bool ReloadSchema { get; set; }

    [BindNever]
    public IReadOnlyList<SelectListItem> CategoryOptions { get; set; } = [];

    [BindNever]
    public IReadOnlyList<SelectListItem> UnitOptions { get; set; } = [];

    [BindNever]
    public bool IsEdit => Id is not null;

    public SaveProductCommand ToCommand() => new(
        CategoryId, Sku, Barcode, Name, Description, BaseUnitId, HasVariants, TracksStock, TracksBatches, RequiresExpiry,
        AllowNegativeStock, ReorderLevel, CostPrice, SalePrice, AttributeInputs.ToCommands(Attributes));

    public static ProductFormModel From(ProductDetails product) => new()
    {
        Id = product.Id,
        CategoryId = product.CategoryId,
        Sku = product.Sku,
        Barcode = product.Barcode,
        Name = product.Name,
        Description = product.Description,
        BaseUnitId = product.BaseUnitId,
        HasVariants = product.HasVariants,
        TracksStock = product.TracksStock,
        TracksBatches = product.TracksBatches,
        RequiresExpiry = product.RequiresExpiry,
        AllowNegativeStock = product.AllowNegativeStock,
        ReorderLevel = product.ReorderLevel,
        CostPrice = product.CostPrice,
        SalePrice = product.SalePrice,
    };
}

/// <summary>Create/edit form of a variant.</summary>
public sealed class VariantFormModel
{
    public Guid ProductId { get; set; }

    public Guid? VariantId { get; set; }

    public string? Sku { get; set; }

    public string? Barcode { get; set; }

    public string? Name { get; set; }

    public decimal? ReorderLevel { get; set; }

    public decimal? CostPrice { get; set; }

    public decimal? SalePrice { get; set; }

    public List<AttributeInput> Attributes { get; set; } = [];

    [BindNever]
    public string? ProductName { get; set; }

    [BindNever]
    public bool IsEdit => VariantId is not null;

    public SaveVariantCommand ToCommand() => new(Sku, Barcode, Name, ReorderLevel, CostPrice, SalePrice, AttributeInputs.ToCommands(Attributes));

    public static VariantFormModel From(VariantDetails variant) => new()
    {
        ProductId = variant.ProductId,
        VariantId = variant.Id,
        Sku = variant.Sku,
        Barcode = variant.Barcode,
        Name = variant.Name,
        ReorderLevel = variant.ReorderLevel,
        CostPrice = variant.CostPrice,
        SalePrice = variant.SalePrice,
    };
}

/// <summary>Attribute definition form (create: all fields; edit: only what may still change).</summary>
public sealed class AttributeFormModel
{
    public Guid? Id { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }

    public AttributeDataType DataType { get; set; } = AttributeDataType.Text;

    public AttributeScope Scope { get; set; } = AttributeScope.Product;

    public bool IsRequired { get; set; }

    public bool IsFilterable { get; set; }

    public string? UnitLabel { get; set; }

    public Guid? CategoryId { get; set; }

    public int SortOrder { get; set; }

    /// <summary>One option label per line (create only; more can be added afterwards).</summary>
    public string? OptionLabels { get; set; }

    [BindNever]
    public IReadOnlyList<SelectListItem> CategoryOptions { get; set; } = [];

    [BindNever]
    public bool IsEdit => Id is not null;

    public CreateAttributeCommand ToCreateCommand() => new(
        Code, Name, DataType, Scope, IsRequired, UnitLabel, CategoryId, SortOrder, IsFilterable,
        (OptionLabels ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((label, index) => new SaveOptionCommand(label, index)).ToList());

    public UpdateAttributeCommand ToUpdateCommand() => new(Name, IsRequired, UnitLabel, CategoryId, SortOrder, IsFilterable);

    public static AttributeFormModel From(AttributeDefinitionDetails attribute) => new()
    {
        Id = attribute.Id,
        Code = attribute.Code,
        Name = attribute.Name,
        DataType = attribute.DataType,
        Scope = attribute.Scope,
        IsRequired = attribute.IsRequired,
        IsFilterable = attribute.IsFilterable,
        UnitLabel = attribute.UnitLabel,
        CategoryId = attribute.CategoryId,
        SortOrder = attribute.SortOrder,
    };
}

/// <summary>Category form.</summary>
public sealed class CategoryFormModel
{
    public Guid? Id { get; set; }

    public Guid? ParentCategoryId { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    [BindNever]
    public IReadOnlyList<SelectListItem> ParentOptions { get; set; } = [];

    [BindNever]
    public bool IsEdit => Id is not null;

    public SaveCategoryCommand ToCommand() => new(ParentCategoryId, Code, Name, Description, SortOrder);

    public static CategoryFormModel From(CategoryDetails category) => new()
    {
        Id = category.Id,
        ParentCategoryId = category.ParentCategoryId,
        Code = category.Code,
        Name = category.Name,
        Description = category.Description,
        SortOrder = category.SortOrder,
    };
}
