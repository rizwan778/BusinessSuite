using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Groups;

/// <summary>
/// Group (department) management. Every method acts as the current user (<c>ICurrentUser</c>) in the organization the
/// request acts in; see <see cref="GroupAccessRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// A group is the managed form of a member's <c>department</c>: members belong to the group whose name equals their
/// department. <b>Renaming</b> a group renames the department of its members in the same transaction. <b>Deleting</b>
/// is refused while any member belongs to it (<see cref="GroupErrors.InUse"/>).
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Groups of other organizations answer <see cref="GroupErrors.NotFound"/>.
/// </para>
/// Callers (controllers) also require <c>users.view</c> for reads and <c>users.manage</c> for changes.
/// </remarks>
public interface IGroupService
{
    /// <summary>The organization's groups, by name.</summary>
    Task<Result<PagedResult<GroupDetails>>> ListAsync(GroupListQuery query, CancellationToken cancellationToken = default);

    /// <summary>One group.</summary>
    Task<Result<GroupDetails>> GetAsync(Guid groupId, CancellationToken cancellationToken = default);

    /// <summary>Creates a group.</summary>
    Task<Result<GroupDetails>> CreateAsync(CreateGroupCommand command, CancellationToken cancellationToken = default);

    /// <summary>Renames a group (moving its members with it) and replaces its description.</summary>
    Task<Result<GroupDetails>> UpdateAsync(Guid groupId, UpdateGroupCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes a group nobody belongs to.</summary>
    Task<Result> DeleteAsync(Guid groupId, CancellationToken cancellationToken = default);
}
