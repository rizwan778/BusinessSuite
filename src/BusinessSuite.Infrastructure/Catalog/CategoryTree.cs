using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Infrastructure.Catalog;

/// <summary>Walks one organization's category tree (small enough to read whole and walk in memory).</summary>
internal static class CategoryTree
{
    private static async Task<Dictionary<Guid, Guid?>> ParentsAsync(AppDbContext db, Guid organizationId, CancellationToken cancellationToken) =>
        await db.ProductCategories.AsNoTracking().Where(category => category.OrganizationId == organizationId)
            .Select(category => new { category.Id, category.ParentCategoryId })
            .ToDictionaryAsync(category => category.Id, category => category.ParentCategoryId, cancellationToken);

    /// <summary>The category and all its ancestors, or <see langword="null"/> when it does not exist in the organization.</summary>
    public static async Task<HashSet<Guid>?> AncestryAsync(AppDbContext db, Guid organizationId, Guid categoryId, CancellationToken cancellationToken)
    {
        var parents = await ParentsAsync(db, organizationId, cancellationToken);
        if (!parents.ContainsKey(categoryId))
        {
            return null;
        }

        var ancestry = new HashSet<Guid>();
        for (Guid? current = categoryId; current is { } step && ancestry.Add(step); current = parents.GetValueOrDefault(step))
        {
        }

        return ancestry;
    }

    /// <summary>The category and all its descendants (just the category when it has none or does not exist).</summary>
    public static async Task<List<Guid>> SelfAndDescendantsAsync(AppDbContext db, Guid organizationId, Guid categoryId, CancellationToken cancellationToken)
    {
        var parents = await ParentsAsync(db, organizationId, cancellationToken);
        var children = parents.ToLookup(pair => pair.Value, pair => pair.Key);
        var result = new List<Guid> { categoryId };
        for (var index = 0; index < result.Count; index++)
        {
            result.AddRange(children[result[index]].Where(child => !result.Contains(child)));
        }

        return result;
    }
}
