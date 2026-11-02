using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Catalog;

// ---- Categories -----------------------------------------------------------------------------------------

/// <summary>A category as returned by the API.</summary>
public sealed record CategoryDetails(
    Guid Id, Guid? ParentCategoryId, string? ParentName, string? Code, string Name, string? Description, int SortOrder,
    bool IsActive, int ChildCount, int ProductCount, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);

/// <summary>A category with its sub-categories (the hierarchy).</summary>
public sealed record CategoryNode(Guid Id, string? Code, string Name, int SortOrder, bool IsActive, IReadOnlyList<CategoryNode> Children);

/// <summary>Filters and paging for <see cref="ICategoryService.ListAsync"/>.</summary>
public sealed record CategoryListQuery
{
    public string? Search { get; init; }

    /// <summary>Only the direct children of this category.</summary>
    public Guid? ParentCategoryId { get; init; }

    /// <summary>Only top-level categories.</summary>
    public bool RootOnly { get; init; }

    public bool? IsActive { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Input for creating or replacing a category (a different parent moves it).</summary>
public sealed record SaveCategoryCommand(Guid? ParentCategoryId, string? Code, string? Name, string? Description, int SortOrder);

/// <summary>
/// Product categories, a tree per organization. Every method acts as the current user in the organization the request acts
/// in. Controllers require <c>inventory.view</c> for reads and <c>inventory.product.manage</c> for changes.
/// </summary>
public interface ICategoryService
{
    Task<Result<PagedResult<CategoryDetails>>> ListAsync(CategoryListQuery query, CancellationToken cancellationToken = default);

    /// <summary>The whole hierarchy, sorted by position then name.</summary>
    Task<Result<IReadOnlyList<CategoryNode>>> GetTreeAsync(bool includeInactive, CancellationToken cancellationToken = default);

    Task<Result<CategoryDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<CategoryDetails>> CreateAsync(SaveCategoryCommand command, CancellationToken cancellationToken = default);

    Task<Result<CategoryDetails>> UpdateAsync(Guid id, SaveCategoryCommand command, CancellationToken cancellationToken = default);

    Task<Result<CategoryDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Deletes a category nothing refers to (no sub-categories, products or attributes).</summary>
    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

// ---- Units and conversions ------------------------------------------------------------------------------

/// <summary>A unit of measure as returned by the API.</summary>
public sealed record UnitDetails(Guid Id, string Code, string Name, int DecimalPlaces, bool IsActive, DateTimeOffset CreatedAtUtc, DateTimeOffset? UpdatedAtUtc);

public sealed record UnitListQuery
{
    public string? Search { get; init; }

    public bool? IsActive { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Input for creating or replacing a unit.</summary>
public sealed record SaveUnitCommand(string? Code, string? Name, int DecimalPlaces);

/// <summary>"1 <c>FromUnit</c> = <c>Factor</c> x <c>ToUnit</c>", general or for one product.</summary>
public sealed record ConversionDetails(Guid Id, Guid? ProductId, Guid FromUnitId, string FromUnitCode, Guid ToUnitId, string ToUnitCode, decimal Factor);

public sealed record ConversionListQuery
{
    public Guid? ProductId { get; init; }

    /// <summary>Only general conversions (not tied to a product).</summary>
    public bool GeneralOnly { get; init; }

    public Guid? UnitId { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = PageRequest.DefaultPageSize;
}

/// <summary>Input for creating or replacing a conversion.</summary>
public sealed record SaveConversionCommand(Guid? ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor);

/// <summary>A quantity converted between two units.</summary>
/// <param name="Quantity">The quantity given, in the from-unit.</param>
/// <param name="Result">The quantity in the to-unit.</param>
/// <param name="Factor">How many to-units one from-unit is.</param>
/// <param name="Source"><c>direct</c> (a stored conversion) or <c>inverse</c> (the stored conversion the other way round).</param>
/// <param name="ProductSpecific">Whether a conversion for the given product was used rather than a general one.</param>
public sealed record ConversionResult(Guid FromUnitId, string FromUnitCode, Guid ToUnitId, string ToUnitCode, decimal Quantity, decimal Result, decimal Factor, string Source, bool ProductSpecific);

/// <summary>A stored conversion, as the pure lookup sees it.</summary>
public sealed record StoredConversion(Guid? ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor);

/// <summary>Pure unit-conversion lookup, unit-tested.</summary>
public static class UnitConversionRules
{
    /// <summary>
    /// The factor to turn one <paramref name="from"/> into <paramref name="to"/>: a stored conversion (the product's own
    /// first, then a general one), or the inverse of one stored the other way round. No chaining through a third unit.
    /// </summary>
    /// <returns><see langword="null"/> when none is defined.</returns>
    public static (decimal Factor, string Source, bool ProductSpecific)? FindFactor(IEnumerable<StoredConversion> conversions, Guid from, Guid to, Guid? productId)
    {
        ArgumentNullException.ThrowIfNull(conversions);

        if (from == to)
        {
            return (1m, "direct", false);
        }

        var all = conversions.ToList();
        foreach (var specific in productId is null ? new[] { false } : new[] { true, false })
        {
            bool Applies(StoredConversion conversion) => specific ? conversion.ProductId == productId : conversion.ProductId is null;

            if (all.FirstOrDefault(conversion => Applies(conversion) && conversion.FromUnitId == from && conversion.ToUnitId == to) is { } direct)
            {
                return (direct.Factor, "direct", specific);
            }

            if (all.FirstOrDefault(conversion => Applies(conversion) && conversion.FromUnitId == to && conversion.ToUnitId == from) is { Factor: > 0 } inverse)
            {
                return (Math.Round(1m / inverse.Factor, 6, MidpointRounding.AwayFromZero), "inverse", specific);
            }
        }

        return null;
    }
}

/// <summary>
/// Units of measure and their conversions. Controllers require <c>inventory.view</c> / <c>inventory.product.manage</c>. Units are
/// deactivated when used; unused ones can be deleted.
/// </summary>
public interface IUnitService
{
    Task<Result<PagedResult<UnitDetails>>> ListAsync(UnitListQuery query, CancellationToken cancellationToken = default);

    Task<Result<UnitDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<UnitDetails>> CreateAsync(SaveUnitCommand command, CancellationToken cancellationToken = default);

    Task<Result<UnitDetails>> UpdateAsync(Guid id, SaveUnitCommand command, CancellationToken cancellationToken = default);

    Task<Result<UnitDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<PagedResult<ConversionDetails>>> ListConversionsAsync(ConversionListQuery query, CancellationToken cancellationToken = default);

    Task<Result<ConversionDetails>> GetConversionAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Result<ConversionDetails>> CreateConversionAsync(SaveConversionCommand command, CancellationToken cancellationToken = default);

    /// <summary>Changes a conversion's factor. Its units and product are what it is, so they cannot change: delete it and create another.</summary>
    Task<Result<ConversionDetails>> UpdateConversionAsync(Guid id, decimal factor, CancellationToken cancellationToken = default);

    Task<Result> DeleteConversionAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Converts a quantity between two units (a product's own conversion wins over a general one).</summary>
    Task<Result<ConversionResult>> ConvertAsync(Guid fromUnitId, Guid toUnitId, decimal quantity, Guid? productId, CancellationToken cancellationToken = default);
}
