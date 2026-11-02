using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Inventory;

/// <summary>
/// <see cref="IWarehouseService"/> on SQL Server. Tenant isolation: every query is filtered by <c>ICurrentUser.OrganizationId</c>
/// and a warehouse's branch must be in the same organization (also enforced by a composite foreign key). There is at most one
/// default warehouse per organization, guaranteed by a unique filtered index; changing it is one transaction. Logs name user,
/// organization and warehouse ids only.
/// </summary>
internal sealed class WarehouseService(AppDbContext db, ICurrentUser currentUser, AuditTrail audit, ILogger<WarehouseService> logger) : IWarehouseService
{
    public async Task<Result<PagedResult<WarehouseDetails>>> ListAsync(WarehouseListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var warehouses = db.Warehouses.AsNoTracking().Where(warehouse => warehouse.OrganizationId == acting.Value);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            warehouses = warehouses.Where(warehouse => warehouse.Name.Contains(search) || warehouse.Code.Contains(search));
        }

        if (query.IsActive is { } isActive)
        {
            warehouses = warehouses.Where(warehouse => warehouse.IsActive == isActive);
        }

        if (query.BranchId is { } branchId)
        {
            warehouses = warehouses.Where(warehouse => warehouse.BranchId == branchId);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await warehouses.CountAsync(cancellationToken);
        var items = await Project(warehouses.OrderByDescending(warehouse => warehouse.IsDefault).ThenBy(warehouse => warehouse.Name).ThenBy(warehouse => warehouse.Id)
            .Skip(page.Skip).Take(page.PageSize)).ToListAsync(cancellationToken);
        return new PagedResult<WarehouseDetails>(items, page, totalCount);
    }

    public async Task<Result<WarehouseDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadAsync(id, acting.Value, cancellationToken);
        if (details is null)
        {
            logger.LogInformation("User {ActorId} asked for warehouse {WarehouseId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return InventoryErrors.WarehouseNotFound;
        }

        return details;
    }

    public async Task<Result<WarehouseDetails>> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var rows = await Project(db.Warehouses.AsNoTracking().Where(warehouse => warehouse.OrganizationId == acting.Value && warehouse.IsDefault)).ToListAsync(cancellationToken);
        return rows.Count == 0 ? InventoryErrors.WarehouseNotFound : rows[0];
    }

    public async Task<Result<WarehouseDetails>> CreateAsync(SaveWarehouseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Guid id = Guid.Empty;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                if (command.BranchId is { } branchId && !await db.Branches.AnyAsync(branch => branch.Id == branchId && branch.OrganizationId == organizationId && branch.IsActive, cancellationToken))
                {
                    return InventoryErrors.BranchInvalid;
                }

                Warehouse warehouse;
                try
                {
                    warehouse = Warehouse.Create(organizationId, command.BranchId, command.Code, command.Name, command.Address);
                }
                catch (DomainException exception)
                {
                    return InventoryErrors.WarehouseInvalid(exception.Message);
                }

                if (await IsDuplicateAsync(organizationId, warehouse, cancellationToken))
                {
                    return InventoryErrors.WarehouseDuplicate;
                }

                // The first warehouse is the default; later ones only when asked, taking the role from the current default.
                var hasDefault = await db.Warehouses.AnyAsync(other => other.OrganizationId == organizationId && other.IsDefault, cancellationToken);
                if (hasDefault && command.MakeDefault)
                {
                    await DemoteDefaultAsync(organizationId, cancellationToken);
                }

                warehouse.SetDefault(!hasDefault || command.MakeDefault);
                db.Warehouses.Add(warehouse);
                id = warehouse.Id;
                audit.Record(AuditActions.WarehouseCreated, AuditActions.EntityTypes.Warehouse, warehouse.Id, organizationId,
                    new { code = warehouse.Code, name = warehouse.Name, branchId = warehouse.BranchId, isDefault = warehouse.IsDefault });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Creation of a warehouse by user {ActorId} in organization {OrganizationId} lost a race with a concurrent request.", currentUser.UserId, organizationId);
            return InventoryErrors.WarehouseDuplicate;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not create a warehouse in organization {OrganizationId} ({Code}).", currentUser.UserId, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} created warehouse {WarehouseId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<WarehouseDetails>> UpdateAsync(Guid id, SaveWarehouseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var warehouse = await db.Warehouses.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
                if (warehouse is null)
                {
                    return InventoryErrors.WarehouseNotFound;
                }

                if (command.BranchId is { } branchId && branchId != warehouse.BranchId
                    && !await db.Branches.AnyAsync(branch => branch.Id == branchId && branch.OrganizationId == organizationId && branch.IsActive, cancellationToken))
                {
                    return InventoryErrors.BranchInvalid;
                }

                var before = Snapshot(warehouse);
                try
                {
                    warehouse.Describe(command.BranchId, command.Code, command.Name, command.Address);
                }
                catch (DomainException exception)
                {
                    return InventoryErrors.WarehouseInvalid(exception.Message);
                }

                if (await IsDuplicateAsync(organizationId, warehouse, cancellationToken))
                {
                    return InventoryErrors.WarehouseDuplicate;
                }

                if (command.MakeDefault && !warehouse.IsDefault)
                {
                    if (!warehouse.IsActive)
                    {
                        return InventoryErrors.DefaultWarehouseMustBeActive;
                    }

                    await DemoteDefaultAsync(organizationId, cancellationToken);
                    warehouse.SetDefault(true);
                }

                audit.Record(AuditActions.WarehouseUpdated, AuditActions.EntityTypes.Warehouse, id, organizationId,
                    new { changes = AuditDiff.Changes(before, Snapshot(warehouse)) });
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Change of warehouse {WarehouseId} by user {ActorId} lost a race with a concurrent request.", id, currentUser.UserId);
            return InventoryErrors.WarehouseDuplicate;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} could not change warehouse {WarehouseId} in organization {OrganizationId} ({Code}).", currentUser.UserId, id, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} changed warehouse {WarehouseId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<WarehouseDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var warehouse = await db.Warehouses.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (warehouse is null)
        {
            return InventoryErrors.WarehouseNotFound;
        }

        if (!isActive && warehouse.IsDefault && await db.Warehouses.AnyAsync(other => other.OrganizationId == organizationId && other.Id != id, cancellationToken))
        {
            db.Entry(warehouse).State = EntityState.Detached;
            return InventoryErrors.WarehouseInvalid("The default warehouse cannot be retired while other warehouses exist. Choose another default first.");
        }

        if (warehouse.IsActive != isActive)
        {
            warehouse.SetActive(isActive);
            audit.Record(isActive ? AuditActions.WarehouseActivated : AuditActions.WarehouseDeactivated, AuditActions.EntityTypes.Warehouse, id, organizationId,
                new { code = warehouse.Code });
            if (await DatabaseErrors.TrySaveAsync(db, logger, "warehouse", currentUser.UserId, InventoryErrors.WarehouseDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
            {
                return error;
            }

            logger.LogInformation("User {ActorId} {Action} warehouse {WarehouseId} in organization {OrganizationId}.", currentUser.UserId, isActive ? "activated" : "retired", id, organizationId);
        }

        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result<WarehouseDetails>> SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Result outcome;
        try
        {
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var warehouse = await db.Warehouses.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
                if (warehouse is null)
                {
                    return InventoryErrors.WarehouseNotFound;
                }

                if (!warehouse.IsActive)
                {
                    return InventoryErrors.DefaultWarehouseMustBeActive;
                }

                if (!warehouse.IsDefault)
                {
                    await DemoteDefaultAsync(organizationId, cancellationToken);
                    warehouse.SetDefault(true);
                    audit.Record(AuditActions.WarehouseDefaultChanged, AuditActions.EntityTypes.Warehouse, id, organizationId, new { code = warehouse.Code });
                    await db.SaveChangesAsync(cancellationToken);
                }

                return Result.Success();
            }, cancellationToken);
        }
        catch (Exception exception) when (DatabaseErrors.IsRace(exception))
        {
            logger.LogWarning("Change of the default warehouse by user {ActorId} in organization {OrganizationId} lost a race with a concurrent request.", currentUser.UserId, organizationId);
            return CatalogErrors.ConcurrentChange;
        }

        if (outcome.IsFailure)
        {
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} made warehouse {WarehouseId} the default of organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var warehouse = await db.Warehouses.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (warehouse is null)
        {
            logger.LogInformation("User {ActorId} tried to delete warehouse {WarehouseId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
            return InventoryErrors.WarehouseNotFound;
        }

        if (warehouse.IsDefault && await db.Warehouses.AnyAsync(other => other.OrganizationId == organizationId && other.Id != id, cancellationToken))
        {
            db.Entry(warehouse).State = EntityState.Detached;
            return InventoryErrors.WarehouseInvalid("The default warehouse cannot be deleted while other warehouses exist. Choose another default first.");
        }

        db.Warehouses.Remove(warehouse);
        audit.Record(AuditActions.WarehouseDeleted, AuditActions.EntityTypes.Warehouse, id, organizationId, new { code = warehouse.Code, name = warehouse.Name });
        if (await DatabaseErrors.TryDeleteAsync(db, logger, "warehouse", id, currentUser.UserId, InventoryErrors.WarehouseInUse, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} deleted warehouse {WarehouseId} of organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return Result.Success();
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>The audited fields of a warehouse.</summary>
    private static Dictionary<string, object?> Snapshot(Warehouse warehouse) => new()
    {
        ["code"] = warehouse.Code,
        ["name"] = warehouse.Name,
        ["address"] = warehouse.Address,
        ["branchId"] = warehouse.BranchId,
        ["isDefault"] = warehouse.IsDefault,
    };

    /// <summary>Clears the current default (saved on its own first, so the one-default index never sees two).</summary>
    private async Task DemoteDefaultAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var current = await db.Warehouses.Where(other => other.OrganizationId == organizationId && other.IsDefault).ToListAsync(cancellationToken);
        if (current.Count > 0)
        {
            current.ForEach(other => other.SetDefault(false));
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private Task<bool> IsDuplicateAsync(Guid organizationId, Warehouse warehouse, CancellationToken cancellationToken)
    {
        var (code, name, id) = (warehouse.Code, warehouse.Name, warehouse.Id);
        return db.Warehouses.AnyAsync(other => other.OrganizationId == organizationId && other.Id != id && (other.Code == code || other.Name == name), cancellationToken);
    }

    private async Task<WarehouseDetails?> LoadAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await Project(db.Warehouses.AsNoTracking().Where(warehouse => warehouse.Id == id && warehouse.OrganizationId == organizationId)).ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    private IQueryable<WarehouseDetails> Project(IQueryable<Warehouse> warehouses) =>
        warehouses.Select(warehouse => new WarehouseDetails(
            warehouse.Id, warehouse.BranchId, warehouse.Code, warehouse.Name, warehouse.Address, warehouse.IsActive, warehouse.IsDefault,
            db.InventoryBalances.Count(balance => balance.WarehouseId == warehouse.Id && balance.QuantityOnHand > 0),
            db.InventoryBalances.Where(balance => balance.WarehouseId == warehouse.Id && balance.QuantityOnHand > 0)
                .Sum(balance => (decimal?)(balance.QuantityOnHand * balance.AverageCost)) ?? 0,
            warehouse.CreatedAtUtc,
            warehouse.UpdatedAtUtc));
}
