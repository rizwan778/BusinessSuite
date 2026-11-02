using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Domain.Catalog;

namespace BusinessSuite.Web.Models.Api.Catalog;

/// <summary>Paging every list request shares (<c>?page=2&amp;pageSize=50</c>).</summary>
public abstract class PagedRequest
{
    /// <summary>1-based page number.</summary>
    [Range(1, PageRequest.MaxPage)]
    public int Page { get; set; } = 1;

    /// <summary>Rows per page, 1 to 100.</summary>
    [Range(1, PageRequest.MaxPageSize)]
    public int PageSize { get; set; } = PageRequest.DefaultPageSize;
}

/// <summary>JSON body that activates or retires a record (<c>PATCH .../status</c>).</summary>
public sealed class ChangeActiveStatusRequest
{
    /// <summary><see langword="true"/> to activate, <see langword="false"/> to retire.</summary>
    [Required(ErrorMessage = "Say whether the record is active.")]
    public bool? IsActive { get; set; }
}

// ---- Categories -----------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/categories</c>.</summary>
public sealed class CategoryListRequest : PagedRequest
{
    /// <summary>Part of the name or code.</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary>Only the direct children of this category.</summary>
    public Guid? ParentCategoryId { get; set; }

    /// <summary>Only top-level categories.</summary>
    public bool RootOnly { get; set; }

    public bool? IsActive { get; set; }

    public CategoryListQuery ToQuery() => new() { Search = Search, ParentCategoryId = ParentCategoryId, RootOnly = RootOnly, IsActive = IsActive, Page = Page, PageSize = PageSize };
}

/// <summary>JSON body of <c>POST /api/v1/categories</c> and <c>PUT /api/v1/categories/{id}</c>. The category belongs to the organization the request acts in.</summary>
public sealed class SaveCategoryRequest
{
    /// <summary>The parent category; leave empty for a top-level category. A different value moves the category.</summary>
    public Guid? ParentCategoryId { get; set; }

    [StringLength(ProductCategory.CodeMaxLength)]
    public string? Code { get; set; }

    [Required(ErrorMessage = "Enter a category name.")]
    [StringLength(ProductCategory.NameMaxLength)]
    public string? Name { get; set; }

    [StringLength(ProductCategory.DescriptionMaxLength)]
    public string? Description { get; set; }

    /// <summary>Position among siblings.</summary>
    [Range(-100000, 100000)]
    public int SortOrder { get; set; }

    public SaveCategoryCommand ToCommand() => new(ParentCategoryId, Code, Name, Description, SortOrder);
}

// ---- Units ----------------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/units</c>.</summary>
public sealed class UnitListRequest : PagedRequest
{
    [StringLength(50)]
    public string? Search { get; set; }

    public bool? IsActive { get; set; }

    public UnitListQuery ToQuery() => new() { Search = Search, IsActive = IsActive, Page = Page, PageSize = PageSize };
}

/// <summary>JSON body of <c>POST /api/v1/units</c> and <c>PUT /api/v1/units/{id}</c>.</summary>
public sealed class SaveUnitRequest
{
    /// <summary>Short code, e.g. <c>KG</c>. Unique within the organization.</summary>
    [Required(ErrorMessage = "Enter a unit code.")]
    [StringLength(UnitOfMeasure.CodeMaxLength)]
    public string? Code { get; set; }

    [Required(ErrorMessage = "Enter a unit name.")]
    [StringLength(UnitOfMeasure.NameMaxLength)]
    public string? Name { get; set; }

    /// <summary>How many decimals a quantity may have: 0 for whole items, 3 for kilograms.</summary>
    [Range(0, UnitOfMeasure.MaxDecimalPlaces)]
    public int DecimalPlaces { get; set; }

    public SaveUnitCommand ToCommand() => new(Code, Name, DecimalPlaces);
}

/// <summary>Query string of <c>GET /api/v1/units/conversions</c>.</summary>
public sealed class ConversionListRequest : PagedRequest
{
    /// <summary>Conversions of this product.</summary>
    public Guid? ProductId { get; set; }

    /// <summary>Only conversions that hold for every product.</summary>
    public bool GeneralOnly { get; set; }

    /// <summary>Conversions from or to this unit.</summary>
    public Guid? UnitId { get; set; }

    public ConversionListQuery ToQuery() => new() { ProductId = ProductId, GeneralOnly = GeneralOnly, UnitId = UnitId, Page = Page, PageSize = PageSize };
}

/// <summary>JSON body of <c>POST /api/v1/units/conversions</c>: "1 <c>fromUnitId</c> = <c>factor</c> x <c>toUnitId</c>".</summary>
public sealed class CreateConversionRequest
{
    /// <summary>The product this holds for; leave empty for a conversion that holds for every product (1 kg = 1000 g).</summary>
    public Guid? ProductId { get; set; }

    [Required(ErrorMessage = "Choose the unit to convert from.")]
    public Guid? FromUnitId { get; set; }

    [Required(ErrorMessage = "Choose the unit to convert to.")]
    public Guid? ToUnitId { get; set; }

    /// <summary>How many to-units one from-unit is. Greater than zero, up to 6 decimals.</summary>
    [Required(ErrorMessage = "Enter the conversion factor.")]
    [Range(typeof(decimal), "0.000001", "99999999999999")]
    public decimal? Factor { get; set; }

    public SaveConversionCommand ToCommand() => new(ProductId, FromUnitId!.Value, ToUnitId!.Value, Factor!.Value);
}

/// <summary>JSON body of <c>PUT /api/v1/units/conversions/{id}</c>: only the factor can change.</summary>
public sealed class UpdateConversionRequest
{
    [Required(ErrorMessage = "Enter the conversion factor.")]
    [Range(typeof(decimal), "0.000001", "99999999999999")]
    public decimal? Factor { get; set; }
}

/// <summary>Query string of <c>GET /api/v1/units/convert</c>.</summary>
public sealed class ConvertRequest
{
    [Required(ErrorMessage = "Choose the unit to convert from.")]
    public Guid? FromUnitId { get; set; }

    [Required(ErrorMessage = "Choose the unit to convert to.")]
    public Guid? ToUnitId { get; set; }

    [Required(ErrorMessage = "Enter the quantity.")]
    [Range(typeof(decimal), "-99999999999999", "99999999999999")]
    public decimal? Quantity { get; set; }

    /// <summary>Use this product's own conversion when it has one.</summary>
    public Guid? ProductId { get; set; }
}

// ---- Attributes -----------------------------------------------------------------------------------------

/// <summary>Query string of <c>GET /api/v1/product-attributes</c>.</summary>
public sealed class AttributeListRequest : PagedRequest
{
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary><c>Product</c> or <c>Variant</c>.</summary>
    public AttributeScope? Scope { get; set; }

    /// <summary><c>Text</c>, <c>Number</c>, <c>Decimal</c>, <c>Date</c>, <c>Boolean</c>, <c>Dropdown</c> or <c>MultiSelect</c>.</summary>
    public AttributeDataType? DataType { get; set; }

    public bool? IsActive { get; set; }

    public AttributeListQuery ToQuery() => new() { Search = Search, Scope = Scope, DataType = DataType, IsActive = IsActive, Page = Page, PageSize = PageSize };
}

/// <summary>An option in a request: a label, its position and whether it can still be chosen.</summary>
public sealed class SaveOptionRequest
{
    [Required(ErrorMessage = "Enter the option.")]
    [StringLength(ProductAttributeOption.LabelMaxLength)]
    public string? Label { get; set; }

    [Range(-100000, 100000)]
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public SaveOptionCommand ToCommand() => new(Label, SortOrder, IsActive);
}

/// <summary>JSON body of <c>POST /api/v1/product-attributes</c>. Code, data type and scope cannot change afterwards.</summary>
public sealed class CreateAttributeRequest
{
    /// <summary>Stable key: lower-case letters, digits and underscores, starting with a letter (e.g. <c>ram_gb</c>).</summary>
    [Required(ErrorMessage = "Enter an attribute code.")]
    [StringLength(ProductAttributeDefinition.CodeMaxLength)]
    public string? Code { get; set; }

    [Required(ErrorMessage = "Enter an attribute name.")]
    [StringLength(ProductAttributeDefinition.NameMaxLength)]
    public string? Name { get; set; }

    /// <summary><c>Text</c>, <c>Number</c>, <c>Decimal</c>, <c>Date</c>, <c>Boolean</c>, <c>Dropdown</c> or <c>MultiSelect</c>.</summary>
    [Required(ErrorMessage = "Choose a data type.")]
    public AttributeDataType? DataType { get; set; }

    /// <summary><c>Product</c> (describes the product, e.g. brand) or <c>Variant</c> (describes each variant, e.g. size).</summary>
    [Required(ErrorMessage = "Choose whether the attribute describes the product or each variant.")]
    public AttributeScope? Scope { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>Unit shown next to a number, e.g. <c>GB</c> or <c>ml</c>.</summary>
    [StringLength(ProductAttributeDefinition.UnitLabelMaxLength)]
    public string? UnitLabel { get; set; }

    /// <summary>Limit the attribute to products of this category (and its sub-categories).</summary>
    public Guid? CategoryId { get; set; }

    [Range(-100000, 100000)]
    public int SortOrder { get; set; }

    public bool IsFilterable { get; set; }

    /// <summary>Initial options of a Dropdown or MultiSelect attribute.</summary>
    [MaxLength(200)]
    public List<SaveOptionRequest>? Options { get; set; }

    public CreateAttributeCommand ToCommand() => new(
        Code, Name, DataType!.Value, Scope!.Value, IsRequired, UnitLabel, CategoryId, SortOrder, IsFilterable, Options?.ConvertAll(option => option.ToCommand()));
}

/// <summary>JSON body of <c>PUT /api/v1/product-attributes/{id}</c>: everything that may still change.</summary>
public sealed class UpdateAttributeRequest
{
    [Required(ErrorMessage = "Enter an attribute name.")]
    [StringLength(ProductAttributeDefinition.NameMaxLength)]
    public string? Name { get; set; }

    public bool IsRequired { get; set; }

    [StringLength(ProductAttributeDefinition.UnitLabelMaxLength)]
    public string? UnitLabel { get; set; }

    public Guid? CategoryId { get; set; }

    [Range(-100000, 100000)]
    public int SortOrder { get; set; }

    public bool IsFilterable { get; set; }

    public UpdateAttributeCommand ToCommand() => new(Name, IsRequired, UnitLabel, CategoryId, SortOrder, IsFilterable);
}

/// <summary>Query string of <c>GET /api/v1/product-attributes/schema</c>.</summary>
public sealed class AttributeSchemaRequest
{
    /// <summary>Only the attributes that apply to a product of this category (its own and its ancestors'). Leave empty for every active attribute.</summary>
    public Guid? CategoryId { get; set; }
}

// ---- Products and variants ------------------------------------------------------------------------------

/// <summary>
/// A value for one attribute, identified by its code. Fill only the field matching the attribute's data type: <c>text</c>,
/// <c>number</c> (Number, Decimal), <c>date</c>, <c>boolean</c>, <c>optionId</c> (Dropdown) or <c>optionIds</c> (MultiSelect).
/// </summary>
public sealed class AttributeValueRequest
{
    [Required(ErrorMessage = "Give the attribute's code.")]
    [StringLength(ProductAttributeDefinition.CodeMaxLength)]
    public string? Code { get; set; }

    public string? Text { get; set; }

    public decimal? Number { get; set; }

    public DateOnly? Date { get; set; }

    public bool? Boolean { get; set; }

    public Guid? OptionId { get; set; }

    [MaxLength(200)]
    public List<Guid>? OptionIds { get; set; }

    public AttributeValueCommand ToCommand() => new(Code, Text, Number, Date, Boolean, OptionId, OptionIds);
}

/// <summary>Query string of <c>GET /api/v1/products</c>.</summary>
public sealed class ProductListRequest : PagedRequest
{
    /// <summary>Part of the name, SKU or barcode of the product or one of its variants.</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    public Guid? CategoryId { get; set; }

    /// <summary>Include the sub-categories of <c>categoryId</c> (default <c>true</c>).</summary>
    public bool IncludeSubcategories { get; set; } = true;

    public bool? IsActive { get; set; }

    public bool? HasVariants { get; set; }

    public bool? TracksStock { get; set; }

    /// <summary>Only products (or variants) described by this attribute option, e.g. the brand "Samsung".</summary>
    public Guid? OptionId { get; set; }

    public ProductListQuery ToQuery() => new()
    {
        Search = Search, CategoryId = CategoryId, IncludeSubcategories = IncludeSubcategories, IsActive = IsActive, HasVariants = HasVariants,
        TracksStock = TracksStock, OptionId = OptionId, Page = Page, PageSize = PageSize,
    };
}

/// <summary>
/// JSON body of <c>POST /api/v1/products</c> and <c>PUT /api/v1/products/{id}</c>. The product belongs to the organization the
/// request acts in. <c>attributes</c> are validated against the organization's schema and replaced as a whole on update.
/// </summary>
public sealed class SaveProductRequest
{
    public Guid? CategoryId { get; set; }

    /// <summary>The sellable SKU of a simple product, or the style code of a product with variants. Unique across products and variants.</summary>
    [Required(ErrorMessage = "Enter a SKU.")]
    [StringLength(Product.SkuMaxLength)]
    public string? Sku { get; set; }

    [StringLength(Product.BarcodeMaxLength)]
    public string? Barcode { get; set; }

    [Required(ErrorMessage = "Enter a product name.")]
    [StringLength(Product.NameMaxLength)]
    public string? Name { get; set; }

    [StringLength(Product.DescriptionMaxLength)]
    public string? Description { get; set; }

    /// <summary>The unit stock is counted in.</summary>
    [Required(ErrorMessage = "Choose a unit of measure.")]
    public Guid? BaseUnitId { get; set; }

    /// <summary><c>true</c> when each size/colour/... is its own variant with its own SKU and stock.</summary>
    public bool HasVariants { get; set; }

    /// <summary><c>false</c> for services and other items without stock (default <c>true</c>).</summary>
    public bool TracksStock { get; set; } = true;

    /// <summary>Hold stock per batch (lot), e.g. for expiry dates.</summary>
    public bool TracksBatches { get; set; }

    /// <summary>Every batch needs an expiry date. Requires <c>tracksBatches</c>.</summary>
    public bool RequiresExpiry { get; set; }

    /// <summary>Allow stock to go below zero. Off by default.</summary>
    public bool AllowNegativeStock { get; set; }

    [Range(typeof(decimal), "0", "99999999999999")]
    public decimal? ReorderLevel { get; set; }

    [Range(typeof(decimal), "0", "99999999999999")]
    public decimal? CostPrice { get; set; }

    [Range(typeof(decimal), "0", "99999999999999")]
    public decimal? SalePrice { get; set; }

    /// <summary>Values of the product-level attributes (see <c>GET /api/v1/product-attributes/schema</c>).</summary>
    [MaxLength(200)]
    public List<AttributeValueRequest>? Attributes { get; set; }

    public SaveProductCommand ToCommand() => new(
        CategoryId, Sku, Barcode, Name, Description, BaseUnitId!.Value, HasVariants, TracksStock, TracksBatches, RequiresExpiry,
        AllowNegativeStock, ReorderLevel, CostPrice, SalePrice, Attributes?.ConvertAll(value => value.ToCommand()));
}

/// <summary>
/// JSON body of <c>POST /api/v1/products/{productId}/variants</c> and <c>PUT .../variants/{variantId}</c>. <c>attributes</c> are the
/// variant-level attribute values (size, colour...), replaced as a whole on update.
/// </summary>
public sealed class SaveVariantRequest
{
    [Required(ErrorMessage = "Enter a SKU.")]
    [StringLength(Product.SkuMaxLength)]
    public string? Sku { get; set; }

    [StringLength(Product.BarcodeMaxLength)]
    public string? Barcode { get; set; }

    [StringLength(ProductVariant.NameMaxLength)]
    public string? Name { get; set; }

    [Range(typeof(decimal), "0", "99999999999999")]
    public decimal? ReorderLevel { get; set; }

    [Range(typeof(decimal), "0", "99999999999999")]
    public decimal? CostPrice { get; set; }

    [Range(typeof(decimal), "0", "99999999999999")]
    public decimal? SalePrice { get; set; }

    [MaxLength(200)]
    public List<AttributeValueRequest>? Attributes { get; set; }

    public SaveVariantCommand ToCommand() => new(Sku, Barcode, Name, ReorderLevel, CostPrice, SalePrice, Attributes?.ConvertAll(value => value.ToCommand()));
}
