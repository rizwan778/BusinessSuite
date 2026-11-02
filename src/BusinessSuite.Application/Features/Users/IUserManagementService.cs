using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Users;

/// <summary>
/// User management: list, view, create and change users, their organization role, department and status, and
/// start a password reset. Every method acts as the current user (<c>ICurrentUser</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant isolation.</b> The organization is ALWAYS the one resolved on the server for the request
/// (<c>ICurrentUser.OrganizationId</c>, checked against the caller's memberships); no method accepts an organization
/// id for authorization. An organization user can only see and change users who are members of that organization;
/// any other user id answers <see cref="UserErrors.NotFound"/>, exactly like an id that does not exist.
/// </para>
/// <para>
/// <b>Super Admins</b> can read every user (optionally filtered by organization) and change users in the
/// organization they act in (<c>X-Organization-Id</c>). The rules are in <see cref="UserAccessRules"/>.
/// </para>
/// Callers (controllers) also require <c>users.view</c> for reads and <c>users.manage</c> for changes.
/// </remarks>
public interface IUserManagementService
{
    /// <summary>One page of users, filtered and sorted.</summary>
    Task<Result<PagedResult<UserDetails>>> ListAsync(UserListQuery query, CancellationToken cancellationToken = default);

    /// <summary>One user.</summary>
    Task<Result<UserDetails>> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an account and makes it a member of the current organization. Without a password, the user is
    /// e-mailed a code to set one.
    /// </summary>
    Task<Result<UserDetails>> CreateAsync(CreateUserCommand command, CancellationToken cancellationToken = default);

    /// <summary>Changes the display name and phone number.</summary>
    Task<Result<UserDetails>> UpdateAsync(Guid userId, UpdateUserCommand command, CancellationToken cancellationToken = default);

    /// <summary>Changes the user's role in the current organization.</summary>
    Task<Result<UserDetails>> ChangeRoleAsync(Guid userId, string role, CancellationToken cancellationToken = default);

    /// <summary>Changes the user's department in the current organization; <see langword="null"/> removes it.</summary>
    Task<Result<UserDetails>> ChangeDepartmentAsync(Guid userId, string? department, CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates or deactivates the user's membership in the current organization. A Super Admin acting in no
    /// organization activates or deactivates the ACCOUNT instead (deactivation signs the user out everywhere).
    /// </summary>
    Task<Result<UserDetails>> ChangeStatusAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>E-mails the user a code to choose a new password. The caller never sees the code.</summary>
    Task<Result> InitiatePasswordResetAsync(Guid userId, CancellationToken cancellationToken = default);
}
