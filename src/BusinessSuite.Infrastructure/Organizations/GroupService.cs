using System.Linq.Expressions;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Groups;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Organizations;

/// <summary>
/// <see cref="IGroupService"/> on SQL Server. The organization comes from <see cref="GroupAccessRules"/>; this class
/// loads the facts, applies the change and records it in the log.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant isolation:</b> every query is filtered by <c>ICurrentUser.OrganizationId</c>; a group of another
/// organization is <see cref="GroupErrors.NotFound"/>.</item>
/// <item>A member belongs to the group whose name equals their department. Renaming moves the members in the SAME
/// transaction as the group, so the two can never disagree; deleting is refused while members remain.</item>
/// <item>Expected failures are <see cref="Result"/> values. Database races (two groups given the same name, a group
/// changed or deleted by someone else) are caught by the unique index / concurrency check and reported as conflicts;
/// anything else is unexpected and left to the API exception handler (logged, 500).</item>
/// <item>Logs contain user, organization and group ids only, never group names.</item>
/// <item><b>Audit:</b> creating, changing and deleting a group adds an <see cref="AuditTrail"/> entry in the organization's
/// log (with the names, which the organization may see), saved with the change.</item>
/// </list>
/// </remarks>
internal sealed class GroupService(
    AppDbContext db,
    ICurrentUser currentUser,
    AuditTrail audit,
    ILogger<GroupService> logger) : IGroupService
{
    // SQL Server: unique index / unique constraint violated.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    private readonly Expression<Func<Group, GroupDetails>> toDetails = group => new GroupDetails(
        group.Id,
        group.Name,
        group.Description,
        db.OrganizationMemberships.Count(membership => membership.OrganizationId == group.OrganizationId && membership.Department == group.Name),
        group.CreatedAtUtc,
        group.UpdatedAtUtc);

    // ---- Reads ------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<GroupDetails>>> ListAsync(GroupListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var acting = GroupAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var groups = db.Groups.AsNoTracking().Where(group => group.OrganizationId == organizationId);
        if (Clean(query.Search) is { } search)
        {
            groups = groups.Where(group => group.Name.Contains(search));
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await groups.CountAsync(cancellationToken);
        var items = await groups
            .OrderBy(group => group.Name).ThenBy(group => group.Id) // stable across pages
            .Skip(page.Skip).Take(page.PageSize)
            .Select(toDetails)
            .ToListAsync(cancellationToken);

        return new PagedResult<GroupDetails>(items, page, totalCount);
    }

    public async Task<Result<GroupDetails>> GetAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var acting = GroupAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var details = await LoadAsync(groupId, acting.Value, cancellationToken);
        if (details is null)
        {
            // Unknown, or another organization's: the caller cannot tell which. Worth a trace when probing ids.
            logger.LogInformation("User {ActorId} asked for group {GroupId}, which does not exist in organization {OrganizationId}.",
                currentUser.UserId, groupId, acting.Value);
            return GroupErrors.NotFound;
        }

        return details;
    }

    // ---- Changes ----------------------------------------------------------------------------------------

    public async Task<Result<GroupDetails>> CreateAsync(CreateGroupCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = GroupAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        Group group;
        try
        {
            group = Group.Create(organizationId, command.Name, command.Description);
        }
        catch (DomainException exception)
        {
            return GroupErrors.Invalid(exception.Message);
        }
        catch (ArgumentException)
        {
            return GroupErrors.Invalid("Enter a group name.");
        }

        if (await IsNameTakenAsync(organizationId, group.Name, exceptGroupId: null, cancellationToken))
        {
            logger.LogInformation("User {ActorId} could not create a group in organization {OrganizationId}: the name is taken.", currentUser.UserId, organizationId);
            return GroupErrors.DuplicateName;
        }

        db.Groups.Add(group);
        audit.Record(AuditActions.GroupCreated, AuditActions.EntityTypes.Group, group.Id, organizationId,
            new { name = group.Name, description = group.Description });
        if (await SaveAsync(organizationId, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        logger.LogInformation("User {ActorId} created group {GroupId} in organization {OrganizationId}.", currentUser.UserId, group.Id, organizationId);
        return await ReloadAsync(group.Id, organizationId, cancellationToken);
    }

    public async Task<Result<GroupDetails>> UpdateAsync(Guid groupId, UpdateGroupCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var acting = GroupAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var movedMembers = 0;
        var renamed = false;
        Result outcome;
        try
        {
            // The group and its members change together or not at all.
            outcome = await DatabaseTransactions.RunAsync<Result>(db, async () =>
            {
                var group = await db.Groups.SingleOrDefaultAsync(candidate => candidate.Id == groupId && candidate.OrganizationId == organizationId, cancellationToken);
                if (group is null)
                {
                    return GroupErrors.NotFound;
                }

                var (previousName, previousDescription) = (group.Name, group.Description);
                try
                {
                    group.Describe(command.Name, command.Description);
                }
                catch (DomainException exception)
                {
                    return GroupErrors.Invalid(exception.Message);
                }
                catch (ArgumentException)
                {
                    return GroupErrors.Invalid("Enter a group name.");
                }

                // A change of letter case only is still a rename: the members' text must follow.
                renamed = !string.Equals(previousName, group.Name, StringComparison.Ordinal);
                if (renamed)
                {
                    if (await IsNameTakenAsync(organizationId, group.Name, group.Id, cancellationToken))
                    {
                        return GroupErrors.DuplicateName;
                    }

                    var members = await db.OrganizationMemberships
                        .Where(membership => membership.OrganizationId == organizationId && membership.Department == previousName)
                        .ToListAsync(cancellationToken);
                    foreach (var member in members)
                    {
                        member.ChangeDepartment(group.Name);
                    }

                    movedMembers = members.Count;
                }

                if (renamed || !string.Equals(previousDescription, group.Description, StringComparison.Ordinal))
                {
                    audit.Record(AuditActions.GroupUpdated, AuditActions.EntityTypes.Group, group.Id, organizationId,
                        new { fromName = previousName, toName = group.Name, fromDescription = previousDescription, toDescription = group.Description, movedMembers });
                }

                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Update of group {GroupId} by user {ActorId} lost a race with a concurrent change.", groupId, currentUser.UserId);
            return GroupErrors.ConcurrentChange;
        }
        catch (DbUpdateException exception) when (IsUniqueKeyViolation(exception))
        {
            logger.LogWarning("Update of group {GroupId} by user {ActorId} rejected: a concurrent request used the same group name.", groupId, currentUser.UserId);
            return GroupErrors.DuplicateName;
        }

        if (outcome.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused updating group {GroupId} in organization {OrganizationId} ({Code}).",
                currentUser.UserId, groupId, organizationId, outcome.Error.Code);
            return outcome.Error;
        }

        logger.LogInformation("User {ActorId} updated group {GroupId} in organization {OrganizationId}; renamed {Renamed}, {MovedMembers} member(s) moved with it.",
            currentUser.UserId, groupId, organizationId, renamed, movedMembers);
        return await ReloadAsync(groupId, organizationId, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var acting = GroupAccessRules.ActingOrganization(currentUser);
        if (acting.IsFailure)
        {
            return acting.Error;
        }

        var organizationId = acting.Value;
        var group = await db.Groups.SingleOrDefaultAsync(candidate => candidate.Id == groupId && candidate.OrganizationId == organizationId, cancellationToken);
        if (group is null)
        {
            logger.LogInformation("User {ActorId} tried to delete group {GroupId}, which does not exist in organization {OrganizationId}.",
                currentUser.UserId, groupId, organizationId);
            return GroupErrors.NotFound;
        }

        // Safe to delete only when nobody belongs to it (members keep their department text, so deleting would orphan it).
        var members = await db.OrganizationMemberships.CountAsync(
            membership => membership.OrganizationId == organizationId && membership.Department == group.Name, cancellationToken);
        if (members > 0)
        {
            logger.LogInformation("User {ActorId} could not delete group {GroupId} of organization {OrganizationId}: {Members} member(s) still belong to it.",
                currentUser.UserId, groupId, organizationId, members);
            return GroupErrors.InUse(members);
        }

        db.Groups.Remove(group);
        audit.Record(AuditActions.GroupDeleted, AuditActions.EntityTypes.Group, group.Id, organizationId, new { name = group.Name });
        if (await SaveAsync(organizationId, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        logger.LogInformation("User {ActorId} deleted group {GroupId} of organization {OrganizationId}.", currentUser.UserId, groupId, organizationId);
        return Result.Success();
    }

    // ---- Helpers ------------------------------------------------------------------------------------------

    /// <summary>Whether another group of the organization has this name (the database's collation decides letter case).</summary>
    private Task<bool> IsNameTakenAsync(Guid organizationId, string name, Guid? exceptGroupId, CancellationToken cancellationToken) =>
        db.Groups.AnyAsync(group => group.OrganizationId == organizationId && group.Name == name && group.Id != exceptGroupId, cancellationToken);

    /// <summary>Saves; a unique-index violation or a row changed or removed concurrently becomes a conflict instead of a 500.</summary>
    private async Task<Error?> SaveAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Group change by user {ActorId} in organization {OrganizationId} lost a race with a concurrent change.", currentUser.UserId, organizationId);
            return GroupErrors.ConcurrentChange;
        }
        catch (DbUpdateException exception) when (IsUniqueKeyViolation(exception))
        {
            logger.LogWarning("Group change by user {ActorId} in organization {OrganizationId} rejected: a concurrent request used the same group name.", currentUser.UserId, organizationId);
            return GroupErrors.DuplicateName;
        }
    }

    private async Task<Result<GroupDetails>> ReloadAsync(Guid groupId, Guid organizationId, CancellationToken cancellationToken)
    {
        var details = await LoadAsync(groupId, organizationId, cancellationToken);
        return details is null ? GroupErrors.NotFound : details;
    }

    private Task<GroupDetails?> LoadAsync(Guid groupId, Guid organizationId, CancellationToken cancellationToken) =>
        db.Groups.AsNoTracking()
            .Where(group => group.Id == groupId && group.OrganizationId == organizationId)
            .Select(toDetails)
            .SingleOrDefaultAsync(cancellationToken);

    private static bool IsUniqueKeyViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
