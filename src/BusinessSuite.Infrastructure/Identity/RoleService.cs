using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// <see cref="IRoleService"/> on SQL Server. Who may do what is decided by <see cref="RoleAccessRules"/>; this class
/// loads the facts, applies the change and records it in the log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> organization users only reach roles available to <c>ICurrentUser.OrganizationId</c>;
/// anything else is <see cref="RoleErrors.NotFound"/>.</item>
/// <item>Role changes apply to every holder on their next request: permissions are read per request
/// (<see cref="UserAccessService"/>), never stored in tokens.</item>
/// <item>Expected failures are <see cref="Result"/> values. Database races (two roles given the same name, a role
/// assigned while being deleted) are caught by the unique index / foreign key and reported as conflicts; anything
/// else is unexpected and left to the API exception handler (logged, 500).</item>
/// <item>Changes to shared roles affect every organization and are logged as warnings.</item>
/// <item><b>Audit:</b> every successful change adds an <see cref="AuditTrail"/> entry saved by the same <c>SaveChanges</c>.
/// Custom roles' entries belong to their organization; shared (platform and system) roles' entries are platform-wide.</item>
/// </list>
/// </remarks>
internal sealed class RoleService(
    AppDbContext db,
    ICurrentUser currentUser,
    RoleCatalog roleCatalog,
    AuditTrail audit,
    ILogger<RoleService> logger) : IRoleService
{
    // SQL Server: unique index / unique constraint violated; foreign key (or check) constraint violated.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int ConstraintViolation = 547;

    public IReadOnlyList<PermissionGroup> ListPermissions() =>
        currentUser.IsSuperAdmin ? PermissionCatalogue.Groups : PermissionCatalogue.OrganizationGroups;

    // ---- Reads ------------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<RoleDetails>>> ListAsync(RoleListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scope = RoleAccessRules.ListScope(currentUser, query.OrganizationId);
        if (scope.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused a role list ({Code}).", currentUser.UserId, scope.Error.Code);
            return scope.Error;
        }

        var roles = scope.Value is { } organizationId
            ? roleCatalog.AvailableTo(organizationId)
            : db.RoleDefinitions.AsNoTracking();

        if (Clean(query.Search) is { } search)
        {
            roles = roles.Where(role => role.Name.Contains(search));
        }

        var loaded = await roles.ToListAsync(cancellationToken);
        var ordered = loaded
            .OrderBy(role => role.IsSystem ? SystemOrder(role) : int.MaxValue)
            .ThenBy(role => role.OrganizationId.HasValue)
            .ThenBy(role => role.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Result.Success(await ToDetailsAsync(ordered, scope.Value, cancellationToken));
    }

    public async Task<Result<RoleDetails>> GetAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var found = await FindVisibleAsync(roleId, tracked: false, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        return (await ToDetailsAsync([found.Value], MemberScope(), cancellationToken))[0];
    }

    public async Task<Result<RolePermissionsDetails>> GetPermissionsAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var found = await FindVisibleAsync(roleId, tracked: false, cancellationToken);
        return found.IsSuccess ? ToPermissionsDetails(found.Value) : found.Error;
    }

    // ---- Changes ----------------------------------------------------------------------------------------

    public async Task<Result<RoleDetails>> CreateAsync(CreateRoleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var allowed = RoleAccessRules.CheckCreate(currentUser, command.Scope);
        if (allowed.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused creating a {Scope} role ({Code}).", currentUser.UserId, command.Scope, allowed.Error.Code);
            return allowed.Error;
        }

        var permissions = command.Permissions ?? [];
        var grantable = RoleAccessRules.CheckPermissions(currentUser, permissions);
        if (grantable.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused creating a role with the requested permissions ({Code}).", currentUser.UserId, grantable.Error.Code);
            return grantable.Error;
        }

        var organizationId = command.Scope == RoleScope.Organization ? currentUser.OrganizationId : null;

        // Domain rules first (reserved system names, lengths), then uniqueness.
        RoleDefinition role;
        try
        {
            role = organizationId is { } owner
                ? RoleDefinition.CreateForOrganization(owner, command.Name, command.Description)
                : RoleDefinition.CreateForPlatform(command.Name, command.Description);
            role.SetPermissions(permissions);
        }
        catch (DomainException exception)
        {
            return RoleErrors.Invalid(exception.Message);
        }
        catch (ArgumentException)
        {
            return RoleErrors.Invalid("Enter a role name.");
        }

        if (await IsNameTakenAsync(organizationId, role.Name, exceptRoleId: null, cancellationToken))
        {
            return RoleErrors.DuplicateName;
        }

        db.RoleDefinitions.Add(role);
        audit.Record(AuditActions.RoleCreated, AuditActions.EntityTypes.Role, role.Id, role.OrganizationId,
            new { name = role.Name, scope = command.Scope.ToString(), permissions = role.Permissions });
        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (role.IsPlatformRole)
        {
            logger.LogWarning("Super Admin {ActorId} created platform role {RoleId} with {PermissionCount} permission(s); no organization can use it yet.",
                currentUser.UserId, role.Id, role.Permissions.Count);
        }
        else
        {
            logger.LogInformation("User {ActorId} created role {RoleId} in organization {OrganizationId} with {PermissionCount} permission(s).",
                currentUser.UserId, role.Id, role.OrganizationId, role.Permissions.Count);
        }

        return (await ToDetailsAsync([role], MemberScope(), cancellationToken))[0];
    }

    public async Task<Result<RoleDetails>> UpdateAsync(Guid roleId, UpdateRoleCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var found = await FindManageableAsync(roleId, "update", cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var role = found.Value;
        if (role.IsSystem)
        {
            return RoleErrors.SystemRoleLocked;
        }

        var (previousName, previousDescription) = (role.Name, role.Description);
        try
        {
            role.Describe(command.Name, command.Description);
        }
        catch (DomainException exception)
        {
            return RoleErrors.Invalid(exception.Message);
        }
        catch (ArgumentException)
        {
            return RoleErrors.Invalid("Enter a role name.");
        }

        // Nothing is saved on this path, so the tracked change above is simply discarded.
        if (await IsNameTakenAsync(role.OrganizationId, role.Name, role.Id, cancellationToken))
        {
            return RoleErrors.DuplicateName;
        }

        if (!string.Equals(previousName, role.Name, StringComparison.Ordinal) || !string.Equals(previousDescription, role.Description, StringComparison.Ordinal))
        {
            audit.Record(AuditActions.RoleUpdated, AuditActions.EntityTypes.Role, role.Id, role.OrganizationId,
                new { fromName = previousName, toName = role.Name, fromDescription = previousDescription, toDescription = role.Description });
        }

        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            return conflict;
        }

        logger.LogInformation("User {ActorId} renamed or re-described role {RoleId}.", currentUser.UserId, role.Id);
        return (await ToDetailsAsync([role], MemberScope(), cancellationToken))[0];
    }

    public async Task<Result> DeleteAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var found = await FindManageableAsync(roleId, "delete", cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var role = found.Value;
        if (role.IsSystem)
        {
            return RoleErrors.SystemRoleLocked;
        }

        var holders = await db.OrganizationMemberships.CountAsync(membership => membership.RoleId == role.Id, cancellationToken);
        if (holders > 0)
        {
            logger.LogInformation("User {ActorId} could not delete role {RoleId}: {Holders} membership(s) still hold it.", currentUser.UserId, role.Id, holders);
            return RoleErrors.InUse(holders);
        }

        db.RoleDefinitions.Remove(role); // its availability rows cascade
        audit.Record(AuditActions.RoleDeleted, AuditActions.EntityTypes.Role, role.Id, role.OrganizationId,
            new { name = role.Name, permissions = role.Permissions });
        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (role.IsPlatformRole)
        {
            logger.LogWarning("Super Admin {ActorId} deleted platform role {RoleId} ({RoleName}).", currentUser.UserId, role.Id, role.Name);
        }
        else
        {
            logger.LogInformation("User {ActorId} deleted role {RoleId} ({RoleName}) of organization {OrganizationId}.",
                currentUser.UserId, role.Id, role.Name, role.OrganizationId);
        }

        return Result.Success();
    }

    public async Task<Result<RolePermissionsDetails>> SetPermissionsAsync(Guid roleId, IReadOnlyCollection<string> permissions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var found = await FindManageableAsync(roleId, "change the permissions of", cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var role = found.Value;
        if (role.IsOwner)
        {
            return RoleErrors.OwnerPermissionsLocked;
        }

        var grantable = RoleAccessRules.CheckPermissions(currentUser, permissions);
        if (grantable.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused setting the permissions of role {RoleId} ({Code}).", currentUser.UserId, role.Id, grantable.Error.Code);
            return grantable.Error;
        }

        var before = role.Permissions.ToHashSet(StringComparer.Ordinal);
        role.SetPermissions(permissions);
        var added = role.Permissions.Where(permission => !before.Contains(permission)).ToArray();
        var removed = before.Where(permission => !role.Permissions.Contains(permission, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (added.Length > 0 || removed.Length > 0)
        {
            audit.Record(AuditActions.RolePermissionsChanged, AuditActions.EntityTypes.Role, role.Id, role.OrganizationId,
                new { role = role.Name, added, removed });
        }

        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var level = role.IsPlatformRole ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, "User {ActorId} changed the permissions of role {RoleId} (organization {OrganizationId}): added [{Added}], removed [{Removed}].",
            currentUser.UserId, role.Id, role.OrganizationId?.ToString() ?? "platform", string.Join(", ", added), string.Join(", ", removed));

        return ToPermissionsDetails(role);
    }

    public async Task<Result<RoleDetails>> SetAvailabilityAsync(Guid roleId, SetRoleAvailabilityCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!currentUser.IsSuperAdmin)
        {
            logger.LogInformation("User {ActorId} was refused changing the availability of role {RoleId}: not a Super Admin.", currentUser.UserId, roleId);
            return RoleErrors.SuperAdminRequired;
        }

        var role = await db.RoleDefinitions.SingleOrDefaultAsync(candidate => candidate.Id == roleId, cancellationToken);
        if (role is null)
        {
            return RoleErrors.NotFound;
        }

        if (!role.IsPlatformRole)
        {
            return RoleErrors.NotAPlatformRole;
        }

        if (role.IsOwner && !command.AllOrganizations)
        {
            return RoleErrors.OwnerAvailabilityLocked;
        }

        List<Guid> organizationIds = command.AllOrganizations
            ? new List<Guid>()
            : (command.OrganizationIds ?? Array.Empty<Guid>()).Distinct().ToList();
        if (organizationIds.Count > 0)
        {
            var known = await db.Organizations.Where(organization => organizationIds.Contains(organization.Id)).Select(organization => organization.Id).ToListAsync(cancellationToken);
            var unknown = organizationIds.Except(known).ToList();
            if (unknown.Count > 0)
            {
                return RoleErrors.UnknownOrganizations(unknown);
            }
        }

        if (!command.AllOrganizations)
        {
            // Organizations losing the role must not have members holding it.
            var stranded = await db.OrganizationMemberships.CountAsync(
                membership => membership.RoleId == role.Id && !organizationIds.Contains(membership.OrganizationId), cancellationToken);
            if (stranded > 0)
            {
                logger.LogInformation("Super Admin {ActorId} could not restrict role {RoleId}: {Holders} membership(s) in other organizations hold it.",
                    currentUser.UserId, role.Id, stranded);
                return RoleErrors.InUse(stranded);
            }
        }

        role.SetAvailableToAllOrganizations(command.AllOrganizations);

        // Replace the explicit list in the same SaveChanges (one transaction).
        var existing = await db.RoleAvailability.Where(availability => availability.RoleId == role.Id).ToListAsync(cancellationToken);
        db.RoleAvailability.RemoveRange(existing.Where(availability => !organizationIds.Contains(availability.OrganizationId)));
        db.RoleAvailability.AddRange(organizationIds
            .Where(organizationId => existing.All(availability => availability.OrganizationId != organizationId))
            .Select(organizationId => RoleAvailability.Create(role.Id, organizationId)));

        audit.Record(AuditActions.RoleAvailabilityChanged, AuditActions.EntityTypes.Role, role.Id, organizationId: null,
            new { role = role.Name, allOrganizations = command.AllOrganizations, organizationIds });

        if (await SaveAsync(cancellationToken) is { } conflict)
        {
            return conflict;
        }

        logger.LogWarning("Super Admin {ActorId} set the availability of role {RoleId}: all organizations {AllOrganizations}, {OrganizationCount} listed organization(s).",
            currentUser.UserId, role.Id, command.AllOrganizations, organizationIds.Count);
        return (await ToDetailsAsync([role], MemberScope(), cancellationToken))[0];
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>
    /// The role, if the caller can see it: any role for a Super Admin; otherwise only roles available to the caller's
    /// organization (everything else is <see cref="RoleErrors.NotFound"/>).
    /// </summary>
    private async Task<Result<RoleDefinition>> FindVisibleAsync(Guid roleId, bool tracked, CancellationToken cancellationToken)
    {
        if (!currentUser.IsSuperAdmin)
        {
            if (currentUser.OrganizationId is not { } organizationId)
            {
                return RoleErrors.OrganizationRequired;
            }

            if (!await roleCatalog.AvailableTo(organizationId).AnyAsync(role => role.Id == roleId, cancellationToken))
            {
                return RoleErrors.NotFound;
            }
        }

        var roles = tracked ? db.RoleDefinitions : db.RoleDefinitions.AsNoTracking();
        var found = await roles.SingleOrDefaultAsync(role => role.Id == roleId, cancellationToken);
        return found is null ? RoleErrors.NotFound : found;
    }

    /// <summary>The role, tracked, if the caller can see it AND change it (<see cref="RoleAccessRules.CheckManage"/>).</summary>
    private async Task<Result<RoleDefinition>> FindManageableAsync(Guid roleId, string action, CancellationToken cancellationToken)
    {
        var found = await FindVisibleAsync(roleId, tracked: true, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var allowed = RoleAccessRules.CheckManage(currentUser, Facts(found.Value));
        if (allowed.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused to {Action} role {RoleId} ({Code}).", currentUser.UserId, action, roleId, allowed.Error.Code);
            return allowed.Error;
        }

        return found.Value;
    }

    /// <summary>
    /// Names must be unique among the roles one organization can use: an organization role may not share a name with
    /// a role of its organization or any platform role; a platform role with any role at all.
    /// </summary>
    private Task<bool> IsNameTakenAsync(Guid? organizationId, string name, Guid? exceptRoleId, CancellationToken cancellationToken)
    {
        var others = db.RoleDefinitions.Where(role => role.Name == name && role.Id != exceptRoleId);
        return organizationId is { } owner
            ? others.AnyAsync(role => role.OrganizationId == owner || role.OrganizationId == null, cancellationToken)
            : others.AnyAsync(cancellationToken);
    }

    /// <summary>Saves; a unique-index or foreign-key violation (a concurrent change) becomes a conflict instead of a 500.</summary>
    private async Task<Error?> SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Role change by user {ActorId} lost a race with a concurrent change.", currentUser.UserId);
            return RoleErrors.ConcurrentChange;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation })
        {
            logger.LogWarning("Role change by user {ActorId} rejected: a concurrent request used the same role name.", currentUser.UserId);
            return RoleErrors.DuplicateName;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: ConstraintViolation })
        {
            logger.LogWarning("Role change by user {ActorId} rejected: the role was assigned to someone concurrently.", currentUser.UserId);
            return RoleErrors.AssignedConcurrently;
        }
    }

    /// <summary>Whose memberships count in <see cref="RoleDetails.MemberCount"/>: the caller's organization, or all (Super Admin).</summary>
    private Guid? MemberScope() => currentUser.IsSuperAdmin ? null : currentUser.OrganizationId;

    private async Task<IReadOnlyList<RoleDetails>> ToDetailsAsync(IReadOnlyList<RoleDefinition> roles, Guid? memberScope, CancellationToken cancellationToken)
    {
        if (roles.Count == 0)
        {
            return [];
        }

        var ids = roles.Select(role => role.Id).ToArray();
        var memberCounts = await db.OrganizationMemberships.AsNoTracking()
            .Where(membership => ids.Contains(membership.RoleId) && (memberScope == null || membership.OrganizationId == memberScope))
            .GroupBy(membership => membership.RoleId)
            .Select(group => new { RoleId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.RoleId, group => group.Count, cancellationToken);

        // Availability is platform configuration: Super Admins only.
        var availability = new Dictionary<Guid, List<Guid>>();
        if (currentUser.IsSuperAdmin)
        {
            availability = (await db.RoleAvailability.AsNoTracking()
                    .Where(entry => ids.Contains(entry.RoleId))
                    .ToListAsync(cancellationToken))
                .GroupBy(entry => entry.RoleId)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.OrganizationId).Order().ToList());
        }

        return roles
            .Select(role =>
            {
                var facts = Facts(role);
                return new RoleDetails(
                    role.Id,
                    role.Name,
                    role.Description,
                    role.IsPlatformRole ? RoleScope.Platform : RoleScope.Organization,
                    role.OrganizationId,
                    role.IsSystem,
                    PermissionsLocked: role.IsOwner,
                    CanManage: RoleAccessRules.CheckManage(currentUser, facts).IsSuccess,
                    facts.Permissions.ToArray(),
                    memberCounts.GetValueOrDefault(role.Id),
                    currentUser.IsSuperAdmin && role.IsPlatformRole
                        ? new RoleAvailabilityDetails(role.AvailableToAllOrganizations, (IReadOnlyList<Guid>?)availability.GetValueOrDefault(role.Id) ?? Array.Empty<Guid>())
                        : null,
                    role.CreatedAtUtc,
                    role.UpdatedAtUtc);
            })
            .ToArray();
    }

    private static RoleFacts Facts(RoleDefinition role) =>
        new(role.Id, role.OrganizationId, role.IsSystem, role.IsOwner, RoleAccessRules.EffectivePermissions(role.IsOwner, role.Permissions));

    private static RolePermissionsDetails ToPermissionsDetails(RoleDefinition role) =>
        new(role.Id, role.Name, role.IsOwner, RoleAccessRules.EffectivePermissions(role.IsOwner, role.Permissions));

    private static int SystemOrder(RoleDefinition role) =>
        OrganizationRoles.All.ToList().IndexOf(role.SystemKey!);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
