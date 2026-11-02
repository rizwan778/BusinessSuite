using System.Text.RegularExpressions;
using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Catalog;

/// <summary>The kind of value an attribute holds. Stored as a number: do not renumber.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum AttributeDataType
{
    /// <summary>Free text.</summary>
    Text = 1,

    /// <summary>A whole number.</summary>
    Number = 2,

    /// <summary>A number with decimals.</summary>
    Decimal = 3,

    /// <summary>A calendar date.</summary>
    Date = 4,

    /// <summary>Yes or no.</summary>
    Boolean = 5,

    /// <summary>One of the attribute's options.</summary>
    Dropdown = 6,

    /// <summary>Any number of the attribute's options.</summary>
    MultiSelect = 7,
}

/// <summary>What an attribute describes. Stored as a number: do not renumber.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum AttributeScope
{
    /// <summary>The product as a whole (brand, model, fabric).</summary>
    Product = 1,

    /// <summary>Each variant separately (size, colour).</summary>
    Variant = 2,
}

/// <summary>
/// An attribute an organization defined for its products (<c>catalog.ProductAttributeDefinitions</c>), e.g. "RAM", "Size",
/// "Volume". Defining one is a row, not a schema change.
/// </summary>
public sealed partial class ProductAttributeDefinition : TenantEntity
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 50;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="UnitLabel"/>.</summary>
    public const int UnitLabelMaxLength = 20;

    private ProductAttributeDefinition()
    {
    }

    /// <summary>Stable key, lower-case letters, digits and underscores (e.g. <c>ram</c>); unique within the organization.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Label shown to users.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>The kind of value. Fixed after creation: changing it would invalidate stored values.</summary>
    public AttributeDataType DataType { get; private set; }

    /// <summary>Whether it describes the product or each variant. Fixed after creation.</summary>
    public AttributeScope Scope { get; private set; }

    /// <summary>Unit shown next to a number, e.g. "GB" or "ml". Optional.</summary>
    public string? UnitLabel { get; private set; }

    /// <summary>Whether a value must be given.</summary>
    public bool IsRequired { get; private set; }

    /// <summary>Whether lists may be filtered by it.</summary>
    public bool IsFilterable { get; private set; }

    /// <summary>Limits the attribute to products of this category (and its sub-categories); <see langword="null"/> = every product.</summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>Position in forms.</summary>
    public int SortOrder { get; private set; }

    /// <summary><see langword="false"/> once retired: hidden from forms, existing values are kept.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Whether values are chosen from <see cref="ProductAttributeOption"/> rows.</summary>
    public bool UsesOptions => DataType is AttributeDataType.Dropdown or AttributeDataType.MultiSelect;

    /// <summary>Whether a product or variant can hold several values (rows) of this attribute.</summary>
    public bool AllowsSeveralValues => DataType == AttributeDataType.MultiSelect;

    /// <summary>An attribute definition of <paramref name="organizationId"/>.</summary>
    /// <exception cref="DomainException">No organization, an invalid code, name or type, or a unit label on a non-numeric type.</exception>
    public static ProductAttributeDefinition Create(
        Guid organizationId, string? code, string? name, AttributeDataType dataType, AttributeScope scope,
        bool isRequired = false, string? unitLabel = null, Guid? categoryId = null, int sortOrder = 0, bool isFilterable = false)
    {
        DomainGuard.Require(Enum.IsDefined(dataType), "Choose a data type.");
        DomainGuard.Require(Enum.IsDefined(scope), "Choose whether the attribute describes the product or each variant.");

        var trimmedCode = DomainGuard.Text(code, CodeMaxLength, "The attribute code");
        DomainGuard.Require(CodePattern().IsMatch(trimmedCode),
            "The attribute code can only contain lower-case letters, digits and underscores, and must start with a letter.");

        var definition = new ProductAttributeDefinition { Code = trimmedCode, DataType = dataType, Scope = scope, CategoryId = categoryId };
        definition.BelongTo(organizationId);
        definition.Describe(name, isRequired, unitLabel, sortOrder, isFilterable);
        return definition;
    }

    /// <summary>Replaces the changeable settings (not the code, data type or scope).</summary>
    public void Describe(string? name, bool isRequired, string? unitLabel, int sortOrder, bool isFilterable)
    {
        var label = DomainGuard.OptionalText(unitLabel, UnitLabelMaxLength, "The unit label");
        DomainGuard.Require(label is null || DataType is AttributeDataType.Number or AttributeDataType.Decimal,
            "A unit label only makes sense for number and decimal attributes.");

        Name = DomainGuard.Text(name, NameMaxLength, "The attribute name");
        IsRequired = isRequired;
        UnitLabel = label;
        SortOrder = sortOrder;
        IsFilterable = isFilterable;
    }

    /// <summary>Limits the attribute to a category (or lifts the limit with <see langword="null"/>).</summary>
    public void LimitToCategory(Guid? categoryId) => CategoryId = categoryId;

    /// <summary>Activates or retires the attribute.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex CodePattern();
}

/// <summary>An allowed value of a Dropdown or MultiSelect attribute, e.g. "XL" or "Samsung" (<c>catalog.ProductAttributeOptions</c>).</summary>
public sealed class ProductAttributeOption : TenantEntity
{
    /// <summary>Maximum length of <see cref="Label"/>.</summary>
    public const int LabelMaxLength = 100;

    private ProductAttributeOption()
    {
    }

    /// <summary>The attribute this is an option of.</summary>
    public Guid AttributeDefinitionId { get; private set; }

    /// <summary>The text shown and stored; unique within the attribute.</summary>
    public string Label { get; private set; } = string.Empty;

    /// <summary>Position in lists (sizes: S, M, L...).</summary>
    public int SortOrder { get; private set; }

    /// <summary><see langword="false"/> once retired: it cannot be chosen again, existing values keep it.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>An option of <paramref name="definition"/>.</summary>
    /// <exception cref="DomainException">The attribute does not use options, or the label is missing or too long.</exception>
    public static ProductAttributeOption Create(ProductAttributeDefinition definition, string? label, int sortOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(definition);
        DomainGuard.Require(definition.UsesOptions, "Only dropdown and multi-select attributes have options.");
        DomainGuard.Present(definition.Id, "Save the attribute before adding options.");

        var option = new ProductAttributeOption { AttributeDefinitionId = definition.Id };
        option.BelongTo(definition.OrganizationId);
        option.Describe(label, sortOrder);
        return option;
    }

    /// <summary>Replaces the label and position.</summary>
    public void Describe(string? label, int sortOrder)
    {
        Label = DomainGuard.Text(label, LabelMaxLength, "The option");
        SortOrder = sortOrder;
    }

    /// <summary>Activates or retires the option.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;
}

/// <summary>A value to store for an attribute: fill the slot that matches the attribute's data type.</summary>
/// <param name="Text">Text.</param>
/// <param name="Number">Number or Decimal.</param>
/// <param name="Date">Date.</param>
/// <param name="Boolean">Boolean.</param>
/// <param name="OptionId">Dropdown or one choice of a MultiSelect.</param>
public sealed record AttributeValueInput(string? Text = null, decimal? Number = null, DateOnly? Date = null, bool? Boolean = null, Guid? OptionId = null);

/// <summary>
/// What a stored attribute value looks like, shared by product and variant values: exactly ONE typed column is filled, the
/// one that matches the attribute's <see cref="AttributeDataType"/>. Typed columns keep values filterable in SQL.
/// </summary>
public abstract class AttributeValueBase : TenantEntity
{
    /// <summary>Maximum length of <see cref="TextValue"/>.</summary>
    public const int TextMaxLength = 500;

    /// <summary>The attribute this is a value of.</summary>
    public Guid AttributeDefinitionId { get; protected set; }

    /// <summary>Text.</summary>
    public string? TextValue { get; private set; }

    /// <summary>Number or Decimal.</summary>
    public decimal? NumberValue { get; private set; }

    /// <summary>Date.</summary>
    public DateOnly? DateValue { get; private set; }

    /// <summary>Boolean.</summary>
    public bool? BooleanValue { get; private set; }

    /// <summary>The chosen option (Dropdown, MultiSelect).</summary>
    public Guid? OptionId { get; private set; }

    /// <summary>
    /// <see langword="true"/> for a MultiSelect value (one row per chosen option); <see langword="false"/> for every other type,
    /// where the (owner, attribute) pair may have only one row. Lets the database enforce "one Dropdown value".
    /// </summary>
    public bool IsMultiValue { get; private set; }

    /// <summary>
    /// Stores <paramref name="input"/> for an attribute of <paramref name="dataType"/>: the matching slot must be filled and
    /// no other.
    /// </summary>
    /// <exception cref="DomainException">The wrong slot is filled, or the value is invalid (whole number expected, text too long...).</exception>
    protected void Store(AttributeDataType dataType, AttributeValueInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var filled = (input.Text is not null ? 1 : 0) + (input.Number is not null ? 1 : 0) + (input.Date is not null ? 1 : 0)
            + (input.Boolean is not null ? 1 : 0) + (input.OptionId is not null ? 1 : 0);
        DomainGuard.Require(filled == 1, "Give exactly one value.");

        TextValue = null;
        NumberValue = null;
        DateValue = null;
        BooleanValue = null;
        OptionId = null;
        IsMultiValue = dataType == AttributeDataType.MultiSelect;

        switch (dataType)
        {
            case AttributeDataType.Text:
                TextValue = DomainGuard.Text(input.Text, TextMaxLength, "The value");
                break;
            case AttributeDataType.Number:
                DomainGuard.Require(input.Number is { } whole && whole == decimal.Truncate(whole), "Enter a whole number.");
                NumberValue = CheckNumber(input.Number!.Value);
                break;
            case AttributeDataType.Decimal:
                DomainGuard.Require(input.Number is not null, "Enter a number.");
                NumberValue = CheckNumber(input.Number!.Value);
                break;
            case AttributeDataType.Date:
                DomainGuard.Require(input.Date is not null, "Enter a date.");
                DateValue = input.Date;
                break;
            case AttributeDataType.Boolean:
                DomainGuard.Require(input.Boolean is not null, "Choose yes or no.");
                BooleanValue = input.Boolean;
                break;
            case AttributeDataType.Dropdown:
            case AttributeDataType.MultiSelect:
                DomainGuard.Require(input.OptionId is { } option && option != Guid.Empty, "Choose an option.");
                OptionId = input.OptionId;
                break;
            default:
                throw new DomainException("Unknown attribute data type.");
        }
    }

    private static decimal CheckNumber(decimal value)
    {
        DomainGuard.Require(Math.Abs(value) <= DomainGuard.MaxAmount, "This number is too large.");
        return value;
    }
}

/// <summary>A product's value for a product-scope attribute (<c>catalog.ProductAttributeValues</c>).</summary>
public sealed class ProductAttributeValue : AttributeValueBase
{
    private ProductAttributeValue()
    {
    }

    /// <summary>The product described.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>A value of <paramref name="definition"/> for <paramref name="productId"/>.</summary>
    /// <exception cref="DomainException">The attribute describes variants, or the value does not fit its data type.</exception>
    public static ProductAttributeValue Create(Guid productId, ProductAttributeDefinition definition, AttributeValueInput input)
    {
        ArgumentNullException.ThrowIfNull(definition);
        DomainGuard.Present(productId, "A value needs a product.");
        DomainGuard.Require(definition.Scope == AttributeScope.Product, "This attribute describes variants, not the product.");

        var value = new ProductAttributeValue { ProductId = productId, AttributeDefinitionId = definition.Id };
        value.BelongTo(definition.OrganizationId);
        value.Store(definition.DataType, input);
        return value;
    }

    /// <summary>Replaces the value (not allowed to change which option a MultiSelect row stands for; remove and add instead).</summary>
    public void Change(ProductAttributeDefinition definition, AttributeValueInput input)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Store(definition.DataType, input);
    }
}

/// <summary>A variant's value for a variant-scope attribute (<c>catalog.ProductVariantAttributeValues</c>).</summary>
public sealed class ProductVariantAttributeValue : AttributeValueBase
{
    private ProductVariantAttributeValue()
    {
    }

    /// <summary>The variant described.</summary>
    public Guid ProductVariantId { get; private set; }

    /// <summary>A value of <paramref name="definition"/> for <paramref name="variantId"/>.</summary>
    /// <exception cref="DomainException">The attribute describes the product, or the value does not fit its data type.</exception>
    public static ProductVariantAttributeValue Create(Guid variantId, ProductAttributeDefinition definition, AttributeValueInput input)
    {
        ArgumentNullException.ThrowIfNull(definition);
        DomainGuard.Present(variantId, "A value needs a variant.");
        DomainGuard.Require(definition.Scope == AttributeScope.Variant, "This attribute describes the product, not each variant.");

        var value = new ProductVariantAttributeValue { ProductVariantId = variantId, AttributeDefinitionId = definition.Id };
        value.BelongTo(definition.OrganizationId);
        value.Store(definition.DataType, input);
        return value;
    }

    /// <summary>Replaces the value.</summary>
    public void Change(ProductAttributeDefinition definition, AttributeValueInput input)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Store(definition.DataType, input);
    }
}
