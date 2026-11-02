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
/// <see cref="IAttributeService"/> on SQL Server. Tenant isolation: every query is filtered by
/// <c>ICurrentUser.OrganizationId</c>; options belong to their attribute and an attribute's category to the same organization
/// (also enforced by composite foreign keys). Logs name user, organization and attribute ids only.
/// </summary>
internal sealed class AttributeService(AppDbContext db, ICurrentUser currentUser, AuditTrail audit, ILogger<AttributeService> logger) : IAttributeService
{
    public async Task<Result<PagedResult<AttributeDefinitionDetails>>> ListAsync(AttributeListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var definitions = db.ProductAttributeDefinitions.AsNoTracking().Where(definition => definition.OrganizationId == acting.Value);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            definitions = definitions.Where(definition => definition.Name.Contains(search) || definition.Code.Contains(search));
        }

        if (query.Scope is { } scope)
        {
            definitions = definitions.Where(definition => definition.Scope == scope);
        }

        if (query.DataType is { } dataType)
        {
            definitions = definitions.Where(definition => definition.DataType == dataType);
        }

        if (query.IsActive is { } isActive)
        {
            definitions = definitions.Where(definition => definition.IsActive == isActive);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await definitions.CountAsync(cancellationToken);
        var items = await LoadAsync(definitions.OrderBy(definition => definition.Scope).ThenBy(definition => definition.SortOrder).ThenBy(definition => definition.Name)
            .ThenBy(definition => definition.Id).Skip(page.Skip).Take(page.PageSize), cancellationToken);
        return new PagedResult<AttributeDefinitionDetails>(items, page, totalCount);
    }

    public async Task<Result<AttributeSchema>> GetSchemaAsync(Guid? categoryId, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var definitions = db.ProductAttributeDefinitions.AsNoTracking().Where(definition => definition.OrganizationId == organizationId && definition.IsActive);
        if (categoryId is { } category)
        {
            var ancestry = await CategoryTree.AncestryAsync(db, organizationId, category, cancellationToken);
            if (ancestry is null)
            {
                return CatalogErrors.CategoryNotFound;
            }

            definitions = definitions.Where(definition => definition.CategoryId == null || ancestry.Contains(definition.CategoryId.Value));
        }

        var all = await LoadAsync(definitions.OrderBy(definition => definition.SortOrder).ThenBy(definition => definition.Name).ThenBy(definition => definition.Id), cancellationToken);
        return new AttributeSchema(
            categoryId,
            all.Where(definition => definition.Scope == AttributeScope.Product).ToList(),
            all.Where(definition => definition.Scope == AttributeScope.Variant).ToList());
    }

    public async Task<Result<AttributeDefinitionDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadOneAsync(id, acting.Value, cancellationToken);
        if (details is null)
        {
            logger.LogInformation("User {ActorId} asked for attribute {AttributeId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return CatalogErrors.AttributeNotFound;
        }

        return details;
    }

    public async Task<Result<AttributeDefinitionDetails>> CreateAsync(CreateAttributeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (command.CategoryId is { } categoryId && !await CategoryExistsAsync(organizationId, categoryId, cancellationToken))
        {
            return CatalogErrors.AttributeInvalid("Choose an existing category of your organization.");
        }

        ProductAttributeDefinition definition;
        try
        {
            definition = ProductAttributeDefinition.Create(organizationId, command.Code, command.Name, command.DataType, command.Scope,
                command.IsRequired, command.UnitLabel, command.CategoryId, command.SortOrder, command.IsFilterable);
        }
        catch (DomainException exception)
        {
            return CatalogErrors.AttributeInvalid(exception.Message);
        }

        if (await db.ProductAttributeDefinitions.AnyAsync(other => other.OrganizationId == organizationId && other.Code == definition.Code, cancellationToken))
        {
            logger.LogInformation("User {ActorId} could not create an attribute in organization {OrganizationId}: the code is taken.", currentUser.UserId, organizationId);
            return CatalogErrors.AttributeDuplicate;
        }

        db.ProductAttributeDefinitions.Add(definition); // gives the definition its id, which its options point at
        try
        {
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in command.Options ?? [])
            {
                var created = ProductAttributeOption.Create(definition, option.Label, option.SortOrder);
                if (!labels.Add(created.Label))
                {
                    db.Entry(definition).State = EntityState.Detached;
                    return CatalogErrors.OptionDuplicate;
                }

                db.ProductAttributeOptions.Add(created);
            }
        }
        catch (DomainException exception)
        {
            db.ChangeTracker.Clear();
            return CatalogErrors.AttributeInvalid(exception.Message);
        }

        audit.Record(AuditActions.AttributeCreated, AuditActions.EntityTypes.ProductAttribute, definition.Id, organizationId,
            new { code = definition.Code, name = definition.Name, dataType = definition.DataType.ToString(), scope = definition.Scope.ToString(), isRequired = definition.IsRequired, categoryId = definition.CategoryId, optionCount = (command.Options ?? []).Count });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "attribute", currentUser.UserId, CatalogErrors.AttributeDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} created attribute {AttributeId} ({DataType}, {Scope}) in organization {OrganizationId}.",
            currentUser.UserId, definition.Id, definition.DataType, definition.Scope, organizationId);
        return (await LoadOneAsync(definition.Id, organizationId, cancellationToken))!;
    }

    public async Task<Result<AttributeDefinitionDetails>> UpdateAsync(Guid id, UpdateAttributeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var definition = await db.ProductAttributeDefinitions.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (definition is null)
        {
            logger.LogInformation("User {ActorId} tried to change attribute {AttributeId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
            return CatalogErrors.AttributeNotFound;
        }

        if (command.CategoryId is { } categoryId && !await CategoryExistsAsync(organizationId, categoryId, cancellationToken))
        {
            db.Entry(definition).State = EntityState.Detached;
            return CatalogErrors.AttributeInvalid("Choose an existing category of your organization.");
        }

        var before = Snapshot(definition);
        try
        {
            definition.Describe(command.Name, command.IsRequired, command.UnitLabel, command.SortOrder, command.IsFilterable);
            definition.LimitToCategory(command.CategoryId);
        }
        catch (DomainException exception)
        {
            db.Entry(definition).State = EntityState.Detached;
            return CatalogErrors.AttributeInvalid(exception.Message);
        }

        audit.Record(AuditActions.AttributeUpdated, AuditActions.EntityTypes.ProductAttribute, id, organizationId,
            new { code = definition.Code, changes = AuditDiff.Changes(before, Snapshot(definition)) });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "attribute", currentUser.UserId, CatalogErrors.AttributeDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} changed attribute {AttributeId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadOneAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<AttributeDefinitionDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var definition = await db.ProductAttributeDefinitions.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (definition is null)
        {
            return CatalogErrors.AttributeNotFound;
        }

        if (definition.IsActive != isActive)
        {
            definition.SetActive(isActive);
            audit.Record(isActive ? AuditActions.AttributeActivated : AuditActions.AttributeDeactivated, AuditActions.EntityTypes.ProductAttribute, id, organizationId,
                new { code = definition.Code });
            if (await DatabaseErrors.TrySaveAsync(db, logger, "attribute", currentUser.UserId, CatalogErrors.AttributeDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
            {
                return error;
            }

            logger.LogInformation("User {ActorId} {Action} attribute {AttributeId} in organization {OrganizationId}.", currentUser.UserId, isActive ? "activated" : "retired", id, organizationId);
        }

        return (await LoadOneAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var definition = await db.ProductAttributeDefinitions.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (definition is null)
        {
            return CatalogErrors.AttributeNotFound;
        }

        // Its options go with it; if any product or variant holds a value the database refuses and nothing is removed.
        var options = await db.ProductAttributeOptions.Where(option => option.AttributeDefinitionId == id && option.OrganizationId == organizationId).ToListAsync(cancellationToken);
        db.ProductAttributeOptions.RemoveRange(options);
        db.ProductAttributeDefinitions.Remove(definition);
        audit.Record(AuditActions.AttributeDeleted, AuditActions.EntityTypes.ProductAttribute, id, organizationId, new { code = definition.Code, name = definition.Name, optionCount = options.Count });
        if (await DatabaseErrors.TryDeleteAsync(db, logger, "attribute", id, currentUser.UserId, CatalogErrors.AttributeInUse, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} deleted attribute {AttributeId} of organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return Result.Success();
    }

    public async Task<Result<AttributeDefinitionDetails>> AddOptionAsync(Guid id, SaveOptionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var definition = await db.ProductAttributeDefinitions.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (definition is null)
        {
            return CatalogErrors.AttributeNotFound;
        }

        ProductAttributeOption option;
        try
        {
            option = ProductAttributeOption.Create(definition, command.Label, command.SortOrder);
            if (!command.IsActive)
            {
                option.SetActive(false);
            }
        }
        catch (DomainException exception)
        {
            db.Entry(definition).State = EntityState.Detached;
            return CatalogErrors.AttributeInvalid(exception.Message);
        }

        var label = option.Label;
        if (await db.ProductAttributeOptions.AnyAsync(other => other.AttributeDefinitionId == id && other.OrganizationId == organizationId && other.Label == label, cancellationToken))
        {
            return CatalogErrors.OptionDuplicate;
        }

        db.ProductAttributeOptions.Add(option);
        audit.Record(AuditActions.AttributeOptionAdded, AuditActions.EntityTypes.ProductAttribute, id, organizationId,
            new { code = definition.Code, optionId = option.Id, label = option.Label });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "attribute option", currentUser.UserId, CatalogErrors.OptionDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} added option {OptionId} to attribute {AttributeId} in organization {OrganizationId}.", currentUser.UserId, option.Id, id, organizationId);
        return (await LoadOneAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<AttributeDefinitionDetails>> UpdateOptionAsync(Guid id, Guid optionId, SaveOptionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var option = await db.ProductAttributeOptions.SingleOrDefaultAsync(
            candidate => candidate.Id == optionId && candidate.AttributeDefinitionId == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (option is null)
        {
            return await OptionMissingAsync(id, organizationId, cancellationToken);
        }

        var optionBefore = Snapshot(option);
        try
        {
            option.Describe(command.Label, command.SortOrder);
            option.SetActive(command.IsActive);
        }
        catch (DomainException exception)
        {
            db.Entry(option).State = EntityState.Detached;
            return CatalogErrors.AttributeInvalid(exception.Message);
        }

        var label = option.Label;
        if (await db.ProductAttributeOptions.AnyAsync(other => other.AttributeDefinitionId == id && other.OrganizationId == organizationId && other.Label == label && other.Id != optionId, cancellationToken))
        {
            db.Entry(option).State = EntityState.Detached;
            return CatalogErrors.OptionDuplicate;
        }

        audit.Record(AuditActions.AttributeOptionUpdated, AuditActions.EntityTypes.ProductAttribute, id, organizationId,
            new { optionId, changes = AuditDiff.Changes(optionBefore, Snapshot(option)) });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "attribute option", currentUser.UserId, CatalogErrors.OptionDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} changed option {OptionId} of attribute {AttributeId} in organization {OrganizationId}.", currentUser.UserId, optionId, id, organizationId);
        return (await LoadOneAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<AttributeDefinitionDetails>> DeleteOptionAsync(Guid id, Guid optionId, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var option = await db.ProductAttributeOptions.SingleOrDefaultAsync(
            candidate => candidate.Id == optionId && candidate.AttributeDefinitionId == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (option is null)
        {
            return await OptionMissingAsync(id, organizationId, cancellationToken);
        }

        db.ProductAttributeOptions.Remove(option);
        audit.Record(AuditActions.AttributeOptionDeleted, AuditActions.EntityTypes.ProductAttribute, id, organizationId, new { optionId, label = option.Label });
        if (await DatabaseErrors.TryDeleteAsync(db, logger, "attribute option", optionId, currentUser.UserId, CatalogErrors.OptionInUse, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} deleted option {OptionId} of attribute {AttributeId} in organization {OrganizationId}.", currentUser.UserId, optionId, id, organizationId);
        return (await LoadOneAsync(id, organizationId, cancellationToken))!;
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>The audited, changeable fields of an attribute definition (code, data type and scope never change).</summary>
    private static Dictionary<string, object?> Snapshot(ProductAttributeDefinition definition) => new()
    {
        ["name"] = definition.Name,
        ["isRequired"] = definition.IsRequired,
        ["unitLabel"] = definition.UnitLabel,
        ["sortOrder"] = definition.SortOrder,
        ["isFilterable"] = definition.IsFilterable,
        ["categoryId"] = definition.CategoryId,
    };

    private static Dictionary<string, object?> Snapshot(ProductAttributeOption option) => new()
    {
        ["label"] = option.Label,
        ["sortOrder"] = option.SortOrder,
        ["isActive"] = option.IsActive,
    };

    /// <summary>Unknown attribute or unknown option: say which.</summary>
    private async Task<Error> OptionMissingAsync(Guid attributeId, Guid organizationId, CancellationToken cancellationToken) =>
        await db.ProductAttributeDefinitions.AnyAsync(definition => definition.Id == attributeId && definition.OrganizationId == organizationId, cancellationToken)
            ? CatalogErrors.OptionNotFound
            : CatalogErrors.AttributeNotFound;

    private Task<bool> CategoryExistsAsync(Guid organizationId, Guid categoryId, CancellationToken cancellationToken) =>
        db.ProductCategories.AnyAsync(category => category.Id == categoryId && category.OrganizationId == organizationId, cancellationToken);

    private async Task<AttributeDefinitionDetails?> LoadOneAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await LoadAsync(db.ProductAttributeDefinitions.AsNoTracking().Where(definition => definition.Id == id && definition.OrganizationId == organizationId), cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>Definitions in the order of <paramref name="definitions"/>, each with its options (by position, then label).</summary>
    private async Task<List<AttributeDefinitionDetails>> LoadAsync(IQueryable<ProductAttributeDefinition> definitions, CancellationToken cancellationToken)
    {
        var rows = await definitions
            .Select(definition => new
            {
                Definition = definition,
                CategoryName = db.ProductCategories.Where(category => category.Id == definition.CategoryId).Select(category => category.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(row => row.Definition.Id).ToList();
        var options = (await db.ProductAttributeOptions.AsNoTracking().Where(option => ids.Contains(option.AttributeDefinitionId)).ToListAsync(cancellationToken))
            .OrderBy(option => option.SortOrder).ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToLookup(option => option.AttributeDefinitionId);

        return rows.ConvertAll(row => new AttributeDefinitionDetails(
            row.Definition.Id, row.Definition.Code, row.Definition.Name, row.Definition.DataType, row.Definition.Scope, row.Definition.UnitLabel,
            row.Definition.IsRequired, row.Definition.IsFilterable, row.Definition.CategoryId, row.CategoryName, row.Definition.SortOrder, row.Definition.IsActive,
            options[row.Definition.Id].Select(option => new AttributeOptionDetails(option.Id, option.Label, option.SortOrder, option.IsActive)).ToList()));
    }
}
