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
/// <see cref="IUnitService"/> on SQL Server. Tenant isolation: every query is filtered by <c>ICurrentUser.OrganizationId</c>; a
/// conversion can only join units (and a product) of the same organization (also enforced by composite foreign keys).
/// Logs name user, organization and record ids only.
/// </summary>
internal sealed class UnitService(AppDbContext db, ICurrentUser currentUser, AuditTrail audit, ILogger<UnitService> logger) : IUnitService
{
    // ---- Units ------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<UnitDetails>>> ListAsync(UnitListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var units = db.UnitsOfMeasure.AsNoTracking().Where(unit => unit.OrganizationId == organizationId);
        if (DatabaseErrors.Clean(query.Search) is { } search)
        {
            units = units.Where(unit => unit.Name.Contains(search) || unit.Code.Contains(search));
        }

        if (query.IsActive is { } isActive)
        {
            units = units.Where(unit => unit.IsActive == isActive);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await units.CountAsync(cancellationToken);
        var rows = await units.OrderBy(unit => unit.Code).ThenBy(unit => unit.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<UnitDetails>(rows.ConvertAll(ToDetails), page, totalCount);
    }

    public async Task<Result<UnitDetails>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var unit = await db.UnitsOfMeasure.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == acting.Value, cancellationToken);
        if (unit is null)
        {
            logger.LogInformation("User {ActorId} asked for unit {UnitId}, which does not exist in organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
            return CatalogErrors.UnitNotFound;
        }

        return ToDetails(unit);
    }

    public async Task<Result<UnitDetails>> CreateAsync(SaveUnitCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        UnitOfMeasure unit;
        try
        {
            unit = UnitOfMeasure.Create(organizationId, command.Code, command.Name, command.DecimalPlaces);
        }
        catch (DomainException exception)
        {
            return CatalogErrors.UnitInvalid(exception.Message);
        }

        if (await IsDuplicateAsync(organizationId, unit, cancellationToken))
        {
            logger.LogInformation("User {ActorId} could not create a unit in organization {OrganizationId}: duplicate.", currentUser.UserId, organizationId);
            return CatalogErrors.UnitDuplicate;
        }

        db.UnitsOfMeasure.Add(unit);
        audit.Record(AuditActions.UnitCreated, AuditActions.EntityTypes.UnitOfMeasure, unit.Id, organizationId,
            new { code = unit.Code, name = unit.Name, decimalPlaces = unit.DecimalPlaces });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "unit", currentUser.UserId, CatalogErrors.UnitDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} created unit {UnitId} in organization {OrganizationId}.", currentUser.UserId, unit.Id, organizationId);
        return ToDetails(unit);
    }

    public async Task<Result<UnitDetails>> UpdateAsync(Guid id, SaveUnitCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var unit = await db.UnitsOfMeasure.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (unit is null)
        {
            return CatalogErrors.UnitNotFound;
        }

        // Finer quantities are safe; coarser ones could orphan quantities already recorded in this unit.
        if (command.DecimalPlaces < unit.DecimalPlaces && await db.Products.AnyAsync(product => product.BaseUnitId == id && product.OrganizationId == organizationId, cancellationToken))
        {
            db.Entry(unit).State = EntityState.Detached;
            return CatalogErrors.UnitDecimalPlacesInUse;
        }

        var before = new Dictionary<string, object?> { ["code"] = unit.Code, ["name"] = unit.Name, ["decimalPlaces"] = unit.DecimalPlaces };
        try
        {
            unit.Describe(command.Code, command.Name, command.DecimalPlaces);
        }
        catch (DomainException exception)
        {
            db.Entry(unit).State = EntityState.Detached;
            return CatalogErrors.UnitInvalid(exception.Message);
        }

        if (await IsDuplicateAsync(organizationId, unit, cancellationToken))
        {
            db.Entry(unit).State = EntityState.Detached;
            return CatalogErrors.UnitDuplicate;
        }

        audit.Record(AuditActions.UnitUpdated, AuditActions.EntityTypes.UnitOfMeasure, id, organizationId,
            new { changes = AuditDiff.Changes(before, new Dictionary<string, object?> { ["code"] = unit.Code, ["name"] = unit.Name, ["decimalPlaces"] = unit.DecimalPlaces }) });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "unit", currentUser.UserId, CatalogErrors.UnitDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} changed unit {UnitId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return ToDetails(unit);
    }

    public async Task<Result<UnitDetails>> SetActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var unit = await db.UnitsOfMeasure.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == acting.Value, cancellationToken);
        if (unit is null)
        {
            return CatalogErrors.UnitNotFound;
        }

        if (unit.IsActive != isActive)
        {
            unit.SetActive(isActive);
            audit.Record(isActive ? AuditActions.UnitActivated : AuditActions.UnitDeactivated, AuditActions.EntityTypes.UnitOfMeasure, id, acting.Value, new { code = unit.Code });
            if (await DatabaseErrors.TrySaveAsync(db, logger, "unit", currentUser.UserId, CatalogErrors.UnitDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
            {
                return error;
            }

            logger.LogInformation("User {ActorId} {Action} unit {UnitId} in organization {OrganizationId}.", currentUser.UserId, isActive ? "activated" : "retired", id, acting.Value);
        }

        return ToDetails(unit);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var unit = await db.UnitsOfMeasure.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == acting.Value, cancellationToken);
        if (unit is null)
        {
            return CatalogErrors.UnitNotFound;
        }

        db.UnitsOfMeasure.Remove(unit);
        audit.Record(AuditActions.UnitDeleted, AuditActions.EntityTypes.UnitOfMeasure, id, acting.Value, new { code = unit.Code, name = unit.Name });
        if (await DatabaseErrors.TryDeleteAsync(db, logger, "unit", id, currentUser.UserId, CatalogErrors.UnitInUse, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} deleted unit {UnitId} of organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
        return Result.Success();
    }

    // ---- Conversions ------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<ConversionDetails>>> ListConversionsAsync(ConversionListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var conversions = db.UnitConversions.AsNoTracking().Where(conversion => conversion.OrganizationId == acting.Value);
        if (query.ProductId is { } productId)
        {
            conversions = conversions.Where(conversion => conversion.ProductId == productId);
        }
        else if (query.GeneralOnly)
        {
            conversions = conversions.Where(conversion => conversion.ProductId == null);
        }

        if (query.UnitId is { } unitId)
        {
            conversions = conversions.Where(conversion => conversion.FromUnitId == unitId || conversion.ToUnitId == unitId);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await conversions.CountAsync(cancellationToken);
        var rows = await ProjectConversions(conversions.OrderBy(conversion => conversion.FromUnitId).ThenBy(conversion => conversion.ToUnitId).ThenBy(conversion => conversion.Id)
                .Skip(page.Skip).Take(page.PageSize))
            .ToListAsync(cancellationToken);
        return new PagedResult<ConversionDetails>(rows, page, totalCount);
    }

    public async Task<Result<ConversionDetails>> GetConversionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadConversionAsync(id, acting.Value, cancellationToken);
        return details is null ? CatalogErrors.ConversionNotFound : details;
    }

    public async Task<Result<ConversionDetails>> CreateConversionAsync(SaveConversionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        if (await CheckConversionReferencesAsync(organizationId, command, cancellationToken) is { } badReference)
        {
            return badReference;
        }

        UnitConversion conversion;
        try
        {
            conversion = UnitConversion.Create(organizationId, command.ProductId, command.FromUnitId, command.ToUnitId, command.Factor);
        }
        catch (DomainException exception)
        {
            return CatalogErrors.UnitInvalid(exception.Message);
        }

        if (await ConversionExistsAsync(organizationId, conversion, cancellationToken))
        {
            return CatalogErrors.ConversionDuplicate;
        }

        db.UnitConversions.Add(conversion);
        audit.Record(AuditActions.UnitConversionCreated, AuditActions.EntityTypes.UnitConversion, conversion.Id, organizationId,
            new { fromUnitId = conversion.FromUnitId, toUnitId = conversion.ToUnitId, productId = conversion.ProductId, factor = conversion.Factor });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "unit conversion", currentUser.UserId, CatalogErrors.ConversionDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} created unit conversion {ConversionId} in organization {OrganizationId}.", currentUser.UserId, conversion.Id, organizationId);
        return (await LoadConversionAsync(conversion.Id, organizationId, cancellationToken))!;
    }

    public async Task<Result<ConversionDetails>> UpdateConversionAsync(Guid id, decimal factor, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var conversion = await db.UnitConversions.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == organizationId, cancellationToken);
        if (conversion is null)
        {
            return CatalogErrors.ConversionNotFound;
        }

        var factorBefore = conversion.Factor;
        try
        {
            conversion.SetFactor(factor);
        }
        catch (DomainException exception)
        {
            db.Entry(conversion).State = EntityState.Detached;
            return CatalogErrors.UnitInvalid(exception.Message);
        }

        audit.Record(AuditActions.UnitConversionUpdated, AuditActions.EntityTypes.UnitConversion, id, organizationId,
            new { fromUnitId = conversion.FromUnitId, toUnitId = conversion.ToUnitId, factor = new { from = factorBefore, to = conversion.Factor } });
        if (await DatabaseErrors.TrySaveAsync(db, logger, "unit conversion", currentUser.UserId, CatalogErrors.ConversionDuplicate, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} changed unit conversion {ConversionId} in organization {OrganizationId}.", currentUser.UserId, id, organizationId);
        return (await LoadConversionAsync(id, organizationId, cancellationToken))!;
    }

    public async Task<Result> DeleteConversionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var conversion = await db.UnitConversions.SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.OrganizationId == acting.Value, cancellationToken);
        if (conversion is null)
        {
            return CatalogErrors.ConversionNotFound;
        }

        db.UnitConversions.Remove(conversion);
        audit.Record(AuditActions.UnitConversionDeleted, AuditActions.EntityTypes.UnitConversion, id, acting.Value,
            new { fromUnitId = conversion.FromUnitId, toUnitId = conversion.ToUnitId, productId = conversion.ProductId });
        if (await DatabaseErrors.TryDeleteAsync(db, logger, "unit conversion", id, currentUser.UserId, CatalogErrors.ConcurrentChange, CatalogErrors.ConcurrentChange, cancellationToken) is { } error)
        {
            return error;
        }

        logger.LogInformation("User {ActorId} deleted unit conversion {ConversionId} of organization {OrganizationId}.", currentUser.UserId, id, acting.Value);
        return Result.Success();
    }

    public async Task<Result<ConversionResult>> ConvertAsync(Guid fromUnitId, Guid toUnitId, decimal quantity, Guid? productId, CancellationToken cancellationToken = default)
    {
        var acting = InventoryAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var units = await db.UnitsOfMeasure.AsNoTracking()
            .Where(unit => unit.OrganizationId == organizationId && (unit.Id == fromUnitId || unit.Id == toUnitId))
            .Select(unit => new { unit.Id, unit.Code })
            .ToListAsync(cancellationToken);
        var from = units.FirstOrDefault(unit => unit.Id == fromUnitId);
        var to = units.FirstOrDefault(unit => unit.Id == toUnitId);
        if (from is null || to is null)
        {
            return CatalogErrors.UnitNotFound;
        }

        var stored = await db.UnitConversions.AsNoTracking()
            .Where(conversion => conversion.OrganizationId == organizationId
                && (conversion.ProductId == null || conversion.ProductId == productId)
                && ((conversion.FromUnitId == fromUnitId && conversion.ToUnitId == toUnitId) || (conversion.FromUnitId == toUnitId && conversion.ToUnitId == fromUnitId)))
            .Select(conversion => new StoredConversion(conversion.ProductId, conversion.FromUnitId, conversion.ToUnitId, conversion.Factor))
            .ToListAsync(cancellationToken);

        if (UnitConversionRules.FindFactor(stored, fromUnitId, toUnitId, productId) is not { } found)
        {
            logger.LogInformation("User {ActorId} asked to convert between units {FromUnitId} and {ToUnitId} in organization {OrganizationId}, but no conversion is defined.",
                currentUser.UserId, fromUnitId, toUnitId, organizationId);
            return CatalogErrors.ConversionUnavailable;
        }

        decimal result;
        try
        {
            result = quantity * found.Factor;
        }
        catch (OverflowException)
        {
            return CatalogErrors.UnitInvalid("The quantity is too large to convert.");
        }

        return new ConversionResult(from.Id, from.Code, to.Id, to.Code, quantity, result, found.Factor, found.Source, found.ProductSpecific);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    private Task<bool> IsDuplicateAsync(Guid organizationId, UnitOfMeasure unit, CancellationToken cancellationToken)
    {
        var (code, name, id) = (unit.Code, unit.Name, unit.Id);
        return db.UnitsOfMeasure.AnyAsync(other => other.OrganizationId == organizationId && other.Id != id && (other.Code == code || other.Name == name), cancellationToken);
    }

    private Task<bool> ConversionExistsAsync(Guid organizationId, UnitConversion conversion, CancellationToken cancellationToken)
    {
        var (productId, from, to) = (conversion.ProductId, conversion.FromUnitId, conversion.ToUnitId);
        return db.UnitConversions.AnyAsync(other => other.OrganizationId == organizationId && other.ProductId == productId && other.FromUnitId == from && other.ToUnitId == to, cancellationToken);
    }

    /// <summary>Both units and the product (if any) must exist in this organization.</summary>
    private async Task<Error?> CheckConversionReferencesAsync(Guid organizationId, SaveConversionCommand command, CancellationToken cancellationToken)
    {
        var unitIds = new[] { command.FromUnitId, command.ToUnitId }.Distinct().ToList();
        var found = await db.UnitsOfMeasure.CountAsync(unit => unit.OrganizationId == organizationId && unitIds.Contains(unit.Id), cancellationToken);
        if (found != unitIds.Count)
        {
            return CatalogErrors.UnitNotFound;
        }

        if (command.ProductId is { } productId && !await db.Products.AnyAsync(product => product.Id == productId && product.OrganizationId == organizationId, cancellationToken))
        {
            return CatalogErrors.ProductNotFound;
        }

        return null;
    }

    private async Task<ConversionDetails?> LoadConversionAsync(Guid id, Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = await ProjectConversions(db.UnitConversions.AsNoTracking().Where(conversion => conversion.Id == id && conversion.OrganizationId == organizationId))
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    private IQueryable<ConversionDetails> ProjectConversions(IQueryable<UnitConversion> conversions) =>
        from conversion in conversions
        join source in db.UnitsOfMeasure on conversion.FromUnitId equals source.Id
        join target in db.UnitsOfMeasure on conversion.ToUnitId equals target.Id
        select new ConversionDetails(conversion.Id, conversion.ProductId, source.Id, source.Code, target.Id, target.Code, conversion.Factor);

    private static UnitDetails ToDetails(UnitOfMeasure unit) =>
        new(unit.Id, unit.Code, unit.Name, unit.DecimalPlaces, unit.IsActive, unit.CreatedAtUtc, unit.UpdatedAtUtc);
}
