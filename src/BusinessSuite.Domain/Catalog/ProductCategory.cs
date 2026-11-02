using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Catalog;

/// <summary>
/// A node of the organization's category tree, e.g. "Beverages > Juices", "Phones", "Menswear" (<c>catalog.ProductCategories</c>).
/// The tree is the organization's own data: no category is built in.
/// </summary>
public sealed class ProductCategory : TenantEntity
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 20;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 500;

    private ProductCategory()
    {
    }

    /// <summary>The parent category, or <see langword="null"/> for a top-level one.</summary>
    public Guid? ParentCategoryId { get; private set; }

    /// <summary>Optional short code, unique within the organization when present.</summary>
    public string? Code { get; private set; }

    /// <summary>Display name, unique among the siblings.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>What belongs here. Optional.</summary>
    public string? Description { get; private set; }

    /// <summary>Position among the siblings.</summary>
    public int SortOrder { get; private set; }

    /// <summary><see langword="false"/> once retired: it can no longer be chosen.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>A category of <paramref name="organizationId"/> under <paramref name="parentCategoryId"/>.</summary>
    /// <exception cref="DomainException">No organization, or a missing or too long value.</exception>
    public static ProductCategory Create(Guid organizationId, Guid? parentCategoryId, string? code, string? name, string? description, int sortOrder = 0)
    {
        var category = new ProductCategory { ParentCategoryId = parentCategoryId };
        category.BelongTo(organizationId);
        category.Describe(code, name, description, sortOrder);
        return category;
    }

    /// <summary>Replaces the code, name, description and position.</summary>
    public void Describe(string? code, string? name, string? description, int sortOrder = 0)
    {
        Code = DomainGuard.OptionalText(code, CodeMaxLength, "The category code");
        Name = DomainGuard.Text(name, NameMaxLength, "The category name");
        Description = DomainGuard.OptionalText(description, DescriptionMaxLength, "The description");
        SortOrder = sortOrder;
    }

    /// <summary>Moves the category under another parent (<see langword="null"/> = top level).</summary>
    /// <exception cref="DomainException">A category cannot be its own parent. Longer cycles are checked by the Application layer.</exception>
    public void MoveTo(Guid? parentCategoryId)
    {
        DomainGuard.Require(parentCategoryId != Id || Id == Guid.Empty, "A category cannot be its own parent.");
        ParentCategoryId = parentCategoryId;
    }

    /// <summary>Activates or retires the category.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;
}
