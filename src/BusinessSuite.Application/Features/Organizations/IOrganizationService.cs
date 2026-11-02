using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Application.Features.Users;

namespace BusinessSuite.Application.Features.Organizations;

/// <summary>
/// Organization (tenant) management. Every method acts as the current user (<c>ICurrentUser</c>); who reaches which
/// organization is decided by <see cref="OrganizationAccessRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant isolation.</b> Platform operators (Super Admins, Platform Administrators) reach every organization;
/// everyone else reaches only the organization the request acts in (resolved on the server) and gets
/// <see cref="OrganizationErrors.NotFound"/> for any other id.
/// </para>
/// <para>
/// <b>Suspension.</b> A suspended organization gives its members no access from their next request on; nothing is
/// deleted, and reactivating restores everything.
/// </para>
/// Callers (controllers) also require the matching permission: <c>organization.view</c> or
/// <c>organization.manage</c> (or <c>platform.organizations.manage</c>), and <c>users.view</c> / <c>roles.view</c> for
/// the member and role lists.
/// </remarks>
public interface IOrganizationService
{
    /// <summary>Organizations the caller may see: every one (platform operators) or the one the request acts in.</summary>
    Task<Result<PagedResult<OrganizationDetails>>> ListAsync(OrganizationListQuery query, CancellationToken cancellationToken = default);

    /// <summary>One organization.</summary>
    Task<Result<OrganizationDetails>> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an organization (platform operators). It starts active; with <see cref="CreateOrganizationCommand.InitialAdmin"/>
    /// (Super Admins only) its first Owner is created in the same transaction, so a failure leaves neither behind.
    /// </summary>
    Task<Result<OrganizationDetails>> CreateAsync(CreateOrganizationCommand command, CancellationToken cancellationToken = default);

    /// <summary>Renames an organization (platform operators, or the organization's own administrators).</summary>
    Task<Result<OrganizationDetails>> UpdateAsync(Guid organizationId, UpdateOrganizationCommand command, CancellationToken cancellationToken = default);

    /// <summary>Suspends or reactivates an organization (platform operators). Repeating the current status changes nothing.</summary>
    Task<Result<OrganizationDetails>> ChangeStatusAsync(Guid organizationId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// The organization's members, as <see cref="IUserManagementService.ListAsync"/> lists them (its filters, sorting and
    /// paging apply; <see cref="UserListQuery.OrganizationId"/> is ignored in favour of <paramref name="organizationId"/>).
    /// </summary>
    Task<Result<PagedResult<UserDetails>>> ListUsersAsync(Guid organizationId, UserListQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The roles the organization may use, as <see cref="IRoleService.ListAsync"/> lists them
    /// (<see cref="RoleListQuery.OrganizationId"/> is ignored in favour of <paramref name="organizationId"/>).
    /// </summary>
    Task<Result<IReadOnlyList<RoleDetails>>> ListRolesAsync(Guid organizationId, RoleListQuery query, CancellationToken cancellationToken = default);
}
