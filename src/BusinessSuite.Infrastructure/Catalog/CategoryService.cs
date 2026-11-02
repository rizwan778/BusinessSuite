using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Common;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Catalog;

/// <summary>
/// <see cref="ICategoryService"/> on SQL Server. Tenant isolation: every query is filtered by <c>ICurrentUser.OrganizationId</c>
/// and a category's parent must be in the same organization (also enforced by the database's composite foreign key). Logs name
/// user, organization and category ids only.
/// </summary>
internal sealed class CategoryService(AppDbContext db, ICurrentUser currentUser, AuditTrail audit, ILogger<CategoryService> logger) : ICategoryService
{
    public async Task<Result<PagedResult<CategoryDetails>>> ListAsync(CategoryListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var categories = db.ProductCategories.AsNoTracking().Where(category => category.OrganizationId == organizationId);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            categories = categories.Where(category => category.Name.Contains(search) || (category.Code != null && category.Code.Contains(search)));
        }

        if (query.ParentCategoryId is { } parentId)
        {
            categories = categories.Where(category => category.ParentCategoryId == parentId);
        }
        else if (query.RootOnly)
        {
            categories = categories.Where(category => category.ParentCategoryId == null);
        }

        if (query.IsActive is { } isActive)
        {
            categories = categories.Where(category => category.IsActive == isActive);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await categories.CountAsync(cancellationToken);
        var items = await Project(categories
                .OrderBy(category => category.SortOrder).ThenBy(category => category.Name).ThenBy(category => category.Id)
                .Skip(page.Skip).Take(page.PageSize))
            .ToListAsync(cancellationToken);

        return new PagedResult<CategoryDetails>(items, page, totalCount);
    }

    public async Task<Result<IReadOnlyList<CategoryNode>>> GetTreeAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var rows = await db.ProductCategories.AsNoTracking()
            .Where(category => category.OrganizationId == organizationId && (includeInactive || category.IsActive))
            .Select(category => new { category.Id, category.ParentCategoryId, category.Code, category.Name, category.SortOrder, category.IsActive })
            .ToListAsync(cancellationToken);

        var byParent = rows.ToLookup(row => row.ParentCategoryId);
        IReadOnlyList<CategoryNode> Build(Guid? parentId) => byParent[parentId]
            .OrderBy(row => row.SortOrder).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(row => new CategoryNode(row.Id, row.Code, row.Name, row.SortOrder, row.IsActive, Build(row.Id)))
            .ToList();

        // Roots are the categories whose parent is missing from the result too (e.g. a retired parent hidden from the tree).
        var known = rows.Select(row => row.Id).ToHashSet();
        var roots = rows.Where(row => row.ParentCategoryId is null || !known.Contains(row.ParentCategoryId.Value))
            .OrderBy(row => row.SortOrder).ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(row => new CategoryNode(row.Id, row.Code, row.Name, row.SortOrder, row.IsActive, Build(row.Id)))
            .ToList();
        return roots;
    }

    public async Task<Result<CategoryDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadAsync(id, acting.Value, cancellationToken);
        if (details is null)
        {
            logger.LogInformation("User {ActorId} asked for category {CategoryId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return CatalogErrors.CategoryNotFound;
        }

        return details;
    }

    public async Task<Result<CategoryDetails>> CreateAsync(SaveCategoryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (command.ParentCategoryId is { } parentId
            && !await db.ProductCategories.AnyAsync(category => category.Id == parentId && category.OrganizationId == organizationId && category.IsActive, cancellationToken))
        {
            return CatalogErrors.CategoryInvalid("Choose an existing, active parent category.");
        }

        ProductCategory category;
        try
        {
            category = ProductCategory.Create(organizationId, command.ParentCategoryId, command.Code, command.Name, command.Description, command.SortOrder);
        }
        catch (DomainException exception)
        {
            return CatalogErrors.CategoryInvalid(exception.Message);
        }

        if (await IsDuplicateAsync(organizationId, category, cancellationToken))
        {
            logger.LogInformation("User {ActorId} could not create a category in organization {OrganizationId}: duplicate.", currentUser.UserId, organizationId);
            return CatalogErrors.CategoryDuplicate;
        }

        db.ProductCategories.Add(category);
        audit.Record(AuditActions.CategoryCreated, AuditActions.EntityTypes.ProductCategory, category.Id, organizationId,
            new { code = category.Code, name = category.Name, parentCategoryId = category.ParentCategoryId });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "category", currentUser.UserId, CatalogErrors.CategoryDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} created category {CategoryId} in organization {OrganizationId}.", currentUser.UserId, category.Id, organizationId);
        return await ReloadAsync(category.Id, organizationId, cancellationToken);
    }

    public async Task<Result<CategoryDetails>> UpdateAsync(Guid id, SaveCategoryCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var category = await db.ProductCategories.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (category is null)
        {
            logger.LogInformation("User {ActorId} tried to change category {CategoryId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
            return CatalogErrors.CategoryNotFound;
        }

        if (command.ParentCategoryId != category.ParentCategoryId && command.ParentCategoryId is { } newParent)
        {
            if (!await db.ProductCategories.AnyAsync(candidate => candidate.Id == newParent && candidate.OrganizationId == organizationId && candidate.IsActive, cancellationToken))
            {
                return CatalogErrors.CategoryInvalid("Choose an existing, active parent category.");
            }

            if (await WouldCreateCycleAsync(organizationId, id, newParent, cancellationToken))
            {
                logger.LogInformation("User {ActorId} tried to move category {CategoryId} under its own descendant in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
                return CatalogErrors.CategoryInvalid("A category cannot be moved under itself or one of its own sub-categories.");
            }
        }

        var before = Snapshot(category);
        try
        {
            category.Describe(command.Code, command.Name, command.Description, command.SortOrder);
            category.MoveTo(command.ParentCategoryId);
        }
        catch (DomainException exception)
        {
            db.Entry(category).State = EntityState.Detached;
            return CatalogErrors.CategoryInvalid(exception.Message);
        }

        if (await IsDuplicateAsync(organizationId, category, cancellationToken))
        {
            db.Entry(category).State = EntityState.Detached;
            return CatalogErrors.CategoryDuplicate;
        }

        audit.Record(AuditActions.CategoryUpdated, AuditActions.EntityTypes.ProductCategory, id, organizationId,
            new { changes = AuditDiff.Changes(before, Snapshot(category)) });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "category", currentUser.UserId, CatalogErrors.CategoryDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} changed category {CategoryId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return await ReloadAsync(id, organizationId, cancellationToken);
    }

    public async Task<Result<CategoryDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var category = await db.ProductCategories.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (category is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        if (category.IsActive != isActive)
        {
            category.SetActive(isActive);
            audit.Record(isActive ? AuditActions.CategoryActivated : AuditActions.CategoryDeactivated, AuditActions.EntityTypes.ProductCategory, id, organizationId,
                new { name = category.Name });
            if (await DatabaseErrors.TrySaveAsync(db, logger, "category", currentUser.UserId, CatalogErrors.CategoryDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
            {
                return error;
            }

            logger.LogInformation("User {ActorId} {Action} category {CategoryId} in organization {OrganizationId}.", currentUser.UserId, isActive ? "activated" : "retired", id, organizationId);
        }

        return await ReloadAsync(id, organizationId, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var category = await db.ProductCategories.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (category is null)
        {
            logger.LogInformation("User {ActorId} tried to delete category {CategoryId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
            return CatalogErrors.CategoryNotFound;
        }

        db.ProductCategories.Remove(category);
        audit.Record(AuditActions.CategoryDeleted, AuditActions.EntityTypes.ProductCategory, id, organizationId, new { code = category.Code, name = category.Name });
        if (await DatabaseErrors.TryDeleteAsync(db, logger, "category", id, currentUser.UserId, CatalogErrors.CategoryInUse, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} deleted category {CategoryId} of organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return Result.Success();
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>The audited fields of a category.</summary>
    private static Dictionary<string, object?> Snapshot(ProductCategory category) => new()
    {
        ["code"] = category.Code,
        ["name"] = category.Name,
        ["sortOrder"] = category.SortOrder,
        ["parentCategoryId"] = category.ParentCategoryId,
    };

    private Task<bool> IsDuplicateAsync(Guid organizationId, ProductCategory category, CancellationToken cancellationToken)
    {
        var (parentId, name, code, id) = (category.ParentCategoryId, category.Name, category.Code, category.Id);
        return db.ProductCategories.AnyAsync(other => other.OrganizationId == organizationId && other.Id != id
            && ((other.ParentCategoryId == parentId && other.Name == name) || (code != null && other.Code == code)), cancellationToken);
    }

    /// <summary>Whether <paramref name="newParent"/> is <paramref name="id"/> itself or one of its descendants (the tree is small: walked in memory).</summary>
    private async Task<bool> WouldCreateCycleAsync(Guid organizationId, Guid id, Guid newParent, CancellationToken cancellationToken)
    {
        var parents = await db.ProductCategories.AsNoTracking().Where(category => category.OrganizationId == organizationId)
            .Select(category => new { category.Id, category.ParentCategoryId })
            .ToDictionaryAsync(category => category.Id, category => category.ParentCategoryId, cancellationToken);

        for (Guid? current = newParent; current is { } step; current = parents.GetValueOrDefault(step))
        {
            if (step == id)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Result<CategoryDetails>> ReloadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var details = await LoadAsync(id, organizationId, cancellationToken);
        return details is null ? CatalogErrors.CategoryNotFound : details;
    }

    private async Task<CategoryDetails?> LoadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await Project(db.ProductCategories.AsNoTracking().Where(category => category.Id == id && category.OrganizationId == organizationId))
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    private IQueryable<CategoryDetails> Project(IQueryable<ProductCategory> categories) =>
        categories.Select(category => new CategoryDetails(
            category.Id,
            category.ParentCategoryId,
            db.ProductCategories.Where(parent => parent.Id == category.ParentCategoryId).Select(parent => parent.Name).FirstOrDefault(),
            category.Code,
            category.Name,
            category.Description,
            category.SortOrder,
            category.IsActive,
            db.ProductCategories.Count(child => child.ParentCategoryId == category.Id),
            db.Products.Count(product => product.CategoryId == category.Id),
            category.CreatedAtUtc,
            category.UpdatedAtUtc));
}
