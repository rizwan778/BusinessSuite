using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Catalog;

/// <summary>
/// Which organization's catalogue and inventory data a caller works with. Pure and deterministic, so it is unit-tested.
/// </summary>
/// <remarks>
/// Everything in the catalogue and inventory modules belongs to the organization the request acts in
/// (<c>ICurrentUser.OrganizationId</c>, resolved and verified on the server). No request carries an organization id, so
/// another organization's record can only be "not found". Whether the caller may view or change data is decided by the
/// controllers' permission attributes (<c>inventory.*</c>).
/// </remarks>
public static class InventoryAccessRules
{
    /// <summary>The organization whose data the caller works with.</summary>
    public static Result<Guid> ActingOrganization(ICurrentUser actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.OrganizationId is { } organizationId
            ? organizationId
            : CatalogErrors.OrganizationRequired;
    }
}

/// <summary>Errors of the catalogue module (categories, units, attributes, products, variants). Codes are part of the API contract: do not rename them.</summary>
public static class CatalogErrors
{
    /// <summary>The request acts in no organization.</summary>
    public static readonly Error OrganizationRequired =
        Error.Validation("Inventory.OrganizationRequired", "Select the organization to act in with the X-Organization-Id header.");

    // ---- Categories -------------------------------------------------------------------------------------

    public static readonly Error CategoryNotFound = Error.NotFound("Categories.NotFound", "No category with this id exists in your organization.");
    public static readonly Error CategoryDuplicate = Error.Conflict("Categories.Duplicate", "A category with this name or code already exists under the same parent.");
    public static readonly Error CategoryInUse = Error.Conflict("Categories.InUse", "This category has sub-categories, products or attributes. Move or remove them first, or deactivate the category.");
    public static Error CategoryInvalid(string description) => Error.Validation("Categories.Invalid", description);

    // ---- Units ------------------------------------------------------------------------------------------

    public static readonly Error UnitNotFound = Error.NotFound("Units.NotFound", "No unit with this id exists in your organization.");
    public static readonly Error UnitDuplicate = Error.Conflict("Units.Duplicate", "A unit with this code or name already exists.");
    public static readonly Error UnitInUse = Error.Conflict("Units.InUse", "Products or conversions use this unit. Deactivate it instead.");
    public static readonly Error UnitDecimalPlacesInUse = Error.Conflict("Units.DecimalPlacesInUse", "Products already use this unit, so its decimal places cannot be reduced.");
    public static Error UnitInvalid(string description) => Error.Validation("Units.Invalid", description);

    public static readonly Error ConversionNotFound = Error.NotFound("Units.ConversionNotFound", "No such unit conversion exists in your organization.");
    public static readonly Error ConversionDuplicate = Error.Conflict("Units.ConversionDuplicate", "This conversion already exists. Change it instead.");
    public static readonly Error ConversionUnavailable = Error.NotFound("Units.ConversionUnavailable", "No conversion between these two units is defined.");

    // ---- Attributes -------------------------------------------------------------------------------------

    public static readonly Error AttributeNotFound = Error.NotFound("Attributes.NotFound", "No attribute with this id exists in your organization.");
    public static readonly Error AttributeDuplicate = Error.Conflict("Attributes.Duplicate", "An attribute with this code already exists.");
    public static readonly Error AttributeInUse = Error.Conflict("Attributes.InUse", "Products or variants have values for this attribute. Deactivate it instead.");
    public static Error AttributeInvalid(string description) => Error.Validation("Attributes.Invalid", description);

    public static readonly Error OptionNotFound = Error.NotFound("Attributes.OptionNotFound", "No such option exists for this attribute.");
    public static readonly Error OptionDuplicate = Error.Conflict("Attributes.OptionDuplicate", "This attribute already has an option with this label.");
    public static readonly Error OptionInUse = Error.Conflict("Attributes.OptionInUse", "Products or variants use this option. Deactivate it instead.");

    // ---- Products and variants --------------------------------------------------------------------------

    public static readonly Error ProductNotFound = Error.NotFound("Products.NotFound", "No product with this id exists in your organization.");
    public static readonly Error ProductDuplicateSku = Error.Conflict("Products.DuplicateSku", "A product or variant with this SKU already exists.");
    public static readonly Error ProductDuplicateBarcode = Error.Conflict("Products.DuplicateBarcode", "A product or variant with this barcode already exists.");
    public static readonly Error ProductInUse = Error.Conflict("Products.InUse", "Stock, batches or documents refer to this product, so it cannot be deleted. Deactivate it instead.");
    public static readonly Error ProductHasStock = Error.Conflict("Products.HasStock", "Stock has been recorded for this product, so its unit, variants and stock tracking can no longer change.");
    public static readonly Error ProductHasVariants = Error.Conflict("Products.HasVariants", "This product still has variants. Delete them before turning variants off.");
    public static Error ProductInvalid(string description) => Error.Validation("Products.Invalid", description);
    public static Error ProductReferenceInvalid(string what) => Error.Validation("Products.ReferenceInvalid", $"Choose an existing, active {what}.");
    public static Error AttributeValuesInvalid(IEnumerable<string> problems) => Error.Validation("Products.AttributeValuesInvalid", string.Join(" ", problems));

    public static readonly Error VariantNotFound = Error.NotFound("Variants.NotFound", "No variant with this id exists for this product.");
    public static readonly Error VariantsNotAllowed = Error.Validation("Variants.NotAllowed", "This product does not have variants. Turn variants on for the product first.");
    public static readonly Error VariantInUse = Error.Conflict("Variants.InUse", "Stock, batches or documents refer to this variant, so it cannot be deleted. Deactivate it instead.");

    /// <summary>Someone else changed the record at the same moment.</summary>
    public static readonly Error ConcurrentChange = Error.Conflict("Catalog.ConcurrentChange", "The record was changed by someone else at the same time. Reload and try again.");
}
