using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Catalog;

/// <summary>The editable facts of a product; one value for create and update.</summary>
/// <param name="CategoryId">Optional category.</param>
/// <param name="Sku">Stock keeping unit (the sellable SKU of a simple product, the style code of a product with variants).</param>
/// <param name="Barcode">Optional barcode.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Optional free text.</param>
/// <param name="BaseUnitId">The unit stock is counted in.</param>
/// <param name="HasVariants"><see langword="true"/> when each size/colour/... is its own <see cref="ProductVariant"/>.</param>
/// <param name="TracksStock"><see langword="false"/> for services and other items without stock.</param>
/// <param name="TracksBatches">Stock is held per batch (lot), e.g. for expiry.</param>
/// <param name="RequiresExpiry">Every batch needs an expiry date.</param>
/// <param name="AllowNegativeStock">Stock may go below zero. Off by default.</param>
/// <param name="ReorderLevel">Stock at or below this is "low" (simple products; variants have their own).</param>
/// <param name="CostPrice">Default purchase price per base unit.</param>
/// <param name="SalePrice">Default selling price per base unit.</param>
public sealed record ProductSpec(
    Guid? CategoryId,
    string? Sku,
    string? Barcode,
    string? Name,
    string? Description,
    Guid BaseUnitId,
    bool HasVariants,
    bool TracksStock = true,
    bool TracksBatches = false,
    bool RequiresExpiry = false,
    bool AllowNegativeStock = false,
    decimal? ReorderLevel = null,
    decimal? CostPrice = null,
    decimal? SalePrice = null);

/// <summary>
/// Something the organization buys, stores or sells (<c>catalog.Products</c>). One entity for every industry: what makes a
/// product a phone or a shirt is its category and its attribute values, which the organization configures.
/// </summary>
/// <remarks>
/// A <b>simple product</b> (<see cref="HasVariants"/> false) has its own stock. A product <b>with variants</b> is a template:
/// stock, SKUs, barcodes and prices live on its <see cref="ProductVariant"/> rows. Products are deactivated, never deleted,
/// once used. Changing <see cref="BaseUnitId"/>, <see cref="HasVariants"/> or <see cref="TracksStock"/> after stock exists
/// would reinterpret quantities, so the Application layer refuses it.
/// </remarks>
public sealed class Product : TenantEntity
{
    /// <summary>Maximum length of <see cref="Sku"/>.</summary>
    public const int SkuMaxLength = 50;

    /// <summary>Maximum length of <see cref="Barcode"/>.</summary>
    public const int BarcodeMaxLength = 50;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 1000;

    private Product()
    {
    }

    /// <summary>Optional category.</summary>
    public Guid? CategoryId { get; private set; }

    /// <summary>Stock keeping unit, unique within the organization.</summary>
    public string Sku { get; private set; } = string.Empty;

    /// <summary>Optional barcode, unique within the organization.</summary>
    public string? Barcode { get; private set; }

    /// <summary>Display name.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; private set; }

    /// <summary>The unit stock is counted in.</summary>
    public Guid BaseUnitId { get; private set; }

    /// <summary>Whether the product is a template for <see cref="ProductVariant"/> rows.</summary>
    public bool HasVariants { get; private set; }

    /// <summary>Whether stock levels are kept.</summary>
    public bool TracksStock { get; private set; } = true;

    /// <summary>Whether stock is held per batch (lot).</summary>
    public bool TracksBatches { get; private set; }

    /// <summary>Whether every batch must have an expiry date.</summary>
    public bool RequiresExpiry { get; private set; }

    /// <summary>Whether stock may go below zero. <see langword="false"/> unless the organization chooses otherwise.</summary>
    public bool AllowNegativeStock { get; private set; }

    /// <summary>Low-stock threshold, or <see langword="null"/>.</summary>
    public decimal? ReorderLevel { get; private set; }

    /// <summary>Default purchase price per base unit.</summary>
    public decimal? CostPrice { get; private set; }

    /// <summary>Default selling price per base unit.</summary>
    public decimal? SalePrice { get; private set; }

    /// <summary><see langword="false"/> once retired.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>A product of <paramref name="organizationId"/>.</summary>
    /// <exception cref="DomainException">No organization, or an invalid value.</exception>
    public static Product Create(Guid organizationId, ProductSpec spec)
    {
        var product = new Product();
        product.BelongTo(organizationId);
        product.Apply(spec);
        return product;
    }

    /// <summary>Replaces every editable value.</summary>
    /// <exception cref="DomainException">An invalid value or a contradiction (batches without stock, expiry without batches).</exception>
    public void Apply(ProductSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        DomainGuard.Present(spec.BaseUnitId, "A product needs a unit of measure.");
        DomainGuard.Require(!spec.TracksBatches || spec.TracksStock, "Batches can only be tracked for a product that tracks stock.");
        DomainGuard.Require(!spec.RequiresExpiry || spec.TracksBatches, "An expiry date can only be required for a product that tracks batches.");

        var sku = DomainGuard.Text(spec.Sku, SkuMaxLength, "The SKU");
        var name = DomainGuard.Text(spec.Name, NameMaxLength, "The product name");
        var barcode = DomainGuard.OptionalText(spec.Barcode, BarcodeMaxLength, "The barcode");
        var description = DomainGuard.OptionalText(spec.Description, DescriptionMaxLength, "The description");
        var reorderLevel = DomainGuard.OptionalAmount(spec.ReorderLevel, "The reorder level");
        var costPrice = DomainGuard.OptionalAmount(spec.CostPrice, "The cost price");
        var salePrice = DomainGuard.OptionalAmount(spec.SalePrice, "The sale price");

        CategoryId = spec.CategoryId;
        Sku = sku;
        Barcode = barcode;
        Name = name;
        Description = description;
        BaseUnitId = spec.BaseUnitId;
        HasVariants = spec.HasVariants;
        TracksStock = spec.TracksStock;
        TracksBatches = spec.TracksBatches;
        RequiresExpiry = spec.RequiresExpiry;
        AllowNegativeStock = spec.AllowNegativeStock;
        ReorderLevel = reorderLevel;
        CostPrice = costPrice;
        SalePrice = salePrice;
    }

    /// <summary>Activates or retires the product.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;
}

/// <summary>The editable facts of a variant.</summary>
/// <param name="Sku">The variant's own SKU.</param>
/// <param name="Barcode">Optional barcode.</param>
/// <param name="Name">Optional display name; usually derived from the variant's attribute values (e.g. "Blue / L").</param>
/// <param name="ReorderLevel">Stock at or below this is "low".</param>
/// <param name="CostPrice">Default purchase price per base unit.</param>
/// <param name="SalePrice">Default selling price per base unit.</param>
public sealed record ProductVariantSpec(
    string? Sku,
    string? Barcode,
    string? Name,
    decimal? ReorderLevel = null,
    decimal? CostPrice = null,
    decimal? SalePrice = null);

/// <summary>
/// One combination of a product's variant attributes, e.g. Blue / L (<c>catalog.ProductVariants</c>), with its own SKU,
/// barcode, prices and stock. The values that define it are <see cref="ProductVariantAttributeValue"/> rows.
/// </summary>
public sealed class ProductVariant : TenantEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    private ProductVariant()
    {
    }

    /// <summary>The product this is a variant of.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>The variant's SKU, unique within the organization.</summary>
    public string Sku { get; private set; } = string.Empty;

    /// <summary>Optional barcode, unique within the organization.</summary>
    public string? Barcode { get; private set; }

    /// <summary>Optional display name.</summary>
    public string? Name { get; private set; }

    /// <summary>Low-stock threshold, or <see langword="null"/>.</summary>
    public decimal? ReorderLevel { get; private set; }

    /// <summary>Default purchase price per base unit.</summary>
    public decimal? CostPrice { get; private set; }

    /// <summary>Default selling price per base unit.</summary>
    public decimal? SalePrice { get; private set; }

    /// <summary><see langword="false"/> once retired.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>A variant of <paramref name="productId"/>.</summary>
    /// <exception cref="DomainException">No organization or product, or an invalid value.</exception>
    public static ProductVariant Create(Guid organizationId, Guid productId, ProductVariantSpec spec)
    {
        DomainGuard.Present(productId, "A variant needs a product.");
        var variant = new ProductVariant { ProductId = productId };
        variant.BelongTo(organizationId);
        variant.Apply(spec);
        return variant;
    }

    /// <summary>Replaces every editable value.</summary>
    public void Apply(ProductVariantSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var sku = DomainGuard.Text(spec.Sku, Product.SkuMaxLength, "The SKU");
        var barcode = DomainGuard.OptionalText(spec.Barcode, Product.BarcodeMaxLength, "The barcode");
        var name = DomainGuard.OptionalText(spec.Name, NameMaxLength, "The variant name");

        ReorderLevel = DomainGuard.OptionalAmount(spec.ReorderLevel, "The reorder level");
        CostPrice = DomainGuard.OptionalAmount(spec.CostPrice, "The cost price");
        SalePrice = DomainGuard.OptionalAmount(spec.SalePrice, "The sale price");
        Sku = sku;
        Barcode = barcode;
        Name = name;
    }

    /// <summary>Activates or retires the variant.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;
}
