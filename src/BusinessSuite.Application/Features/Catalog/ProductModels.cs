using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Catalog;

/// <summary>A product in a list.</summary>
public sealed record ProductSummary(
    Guid Id, string Sku, string? Barcode, string Name, Guid? CategoryId, string? CategoryName, string BaseUnitCode, bool HasVariants,
    int VariantCount, bool TracksStock, bool IsActive, decimal? SalePrice, decimal StockOnHand, decimal StockAvailable, DateTimeOffset CreatedAtUtc);

/// <summary>A variant (one size/colour/... combination) with its own SKU and attribute values.</summary>
public sealed record VariantDetails(
    Guid Id, Guid ProductId, string Sku, string? Barcode, string? Name, decimal? ReorderLevel, decimal? CostPrice, decimal? SalePrice,
    bool IsActive, decimal StockOnHand, decimal StockAvailable, IReadOnlyList<AttributeValueDetails> Attributes,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);

/// <summary>A product with everything about it.</summary>
public sealed record ProductDetails(
    Guid Id, Guid? CategoryId, string? CategoryName, string Sku, string? Barcode, string Name, string? Description,
    Guid BaseUnitId, string BaseUnitCode, int BaseUnitDecimalPlaces, bool HasVariants, bool TracksStock, bool TracksBatches,
    bool RequiresExpiry, bool AllowNegativeStock, decimal? ReorderLevel, decimal? CostPrice, decimal? SalePrice, bool IsActive,
    decimal StockOnHand, decimal StockReserved, decimal StockAvailable, IReadOnlyList<AttributeValueDetails> Attributes,
    IReadOnlyList<VariantDetails> Variants, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);

/// <summary>Filters and paging for <see cref="IProductService.ListAsync"/>. Every value is optional.</summary>
public sealed record ProductListQuery
{
    /// <summary>Part of the name, SKU or barcode of the product or one of its variants.</summary>
    public string? Search { get; init; }

    public Guid? CategoryId { get; init; }

    /// <summary>Include the sub-categories of <see cref="CategoryId"/> (default).</summary>
    public bool IncludeSubcategories { get; init; } = true;

    public bool? IsActive { get; init; }

    public bool? HasVariants { get; init; }

    public bool? TracksStock { get; init; }

    /// <summary>Only products (or variants) described by this attribute option, e.g. the brand "Samsung".</summary>
    public Guid? OptionId { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Input for creating or replacing a product (and its product-level attribute values, replaced as a whole).</summary>
public sealed record SaveProductCommand(
    Guid? CategoryId, string? Sku, string? Barcode, string? Name, string? Description, Guid BaseUnitId, bool HasVariants,
    bool TracksStock, bool TracksBatches, bool RequiresExpiry, bool AllowNegativeStock, decimal? ReorderLevel, decimal? CostPrice,
    decimal? SalePrice, IReadOnlyList<AttributeValueCommand>? Attributes);

/// <summary>Input for creating or replacing a variant (and its attribute values, replaced as a whole).</summary>
public sealed record SaveVariantCommand(
    string? Sku, string? Barcode, string? Name, decimal? ReorderLevel, decimal? CostPrice, decimal? SalePrice,
    IReadOnlyList<AttributeValueCommand>? Attributes);

/// <summary>
/// The product catalogue with dynamic attributes and variants. Every method acts as the current user in the organization the
/// request acts in. Controllers require <c>inventory.view</c> for reads and <c>inventory.product.manage</c> for changes.
/// </summary>
/// <remarks>
/// Attribute values are validated against the organization's schema on every create/update
/// (<see cref="AttributeValueValidator"/>). Products and variants with stock history are deactivated, never deleted.
/// </remarks>
public interface IProductService
{
    Task<Result<PagedResult<ProductSummary>>> ListAsync(ProductListQuery query, CancellationToken cancellationToken = default);

    Task<Result<ProductDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<ProductDetails>> CreateAsync(SaveProductCommand command, CancellationToken cancellationToken = default);

    Task<Result<ProductDetails>> UpdateAsync(Guid id, SaveProductCommand command, CancellationToken cancellationToken = default);

    Task<Result<ProductDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Deletes a product (with its variants and attribute values) that no stock, batch or document refers to.</summary>
    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<VariantDetails>>> ListVariantsAsync(Guid productId, CancellationToken cancellationToken = default);

    Task<Result<VariantDetails>> GetVariantAsync(Guid productId, Guid variantId, CancellationToken cancellationToken = default);

    Task<Result<VariantDetails>> CreateVariantAsync(Guid productId, SaveVariantCommand command, CancellationToken cancellationToken = default);

    Task<Result<VariantDetails>> UpdateVariantAsync(Guid productId, Guid variantId, SaveVariantCommand command, CancellationToken cancellationToken = default);

    Task<Result<VariantDetails>> SetVariantActiveAsync(Guid productId, Guid variantId, bool isActive, CancellationToken cancellationToken = default);

    Task<Result> DeleteVariantAsync(Guid productId, Guid variantId, CancellationToken cancellationToken = default);
}
