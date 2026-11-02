using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// User management API: list, view, create and change users, their role, department and status, and start a
/// password reset. Version 1: <c>/api/v1/users</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant isolation.</b> Everything happens in the organization the request acts in: the <c>X-Organization-Id</c>
/// header, or the caller's only organization, checked against the caller's memberships on the server for every
/// request. No request body carries an organization id. An Organization Administrator only ever sees and changes
/// members of their own organization; other user ids answer 404 <c>Users.NotFound</c>.
/// </para>
/// <para>
/// <b>Super Admins</b> can list every user and filter by any organization (<c>organizationId</c>), view any user,
/// and change users in the organization they act in (<c>X-Organization-Id</c>). Without an organization, the status
/// endpoint activates or deactivates the account itself.
/// </para>
/// Reads need <c>users.view</c>, changes need <c>users.manage</c>. The rules for who may change whom (no
/// self-promotion, Owner-only Owner changes, last Owner protection, platform accounts) are in <see cref="UserAccessRules"/>.
/// See docs/API.md §5 and docs/AUTHORIZATION.md.
/// </remarks>
[Route("api/v1/users")]
[Authorize(AuthenticationSchemes = BearerScheme)]
public sealed class UsersController(IUserManagementService users) : AppApiController
{
    // GET /api/v1/users
    /// <summary>Lists users with search, filters, sorting and paging.</summary>
    /// <remarks>
    /// Organization users see the members of their organization. Super Admins see every user, or one organization's
    /// with <c>organizationId</c>. Each user lists their membership(s) visible to the caller.
    /// </remarks>
    /// <response code="200">One page of users with <c>totalCount</c> and <c>totalPages</c>.</response>
    /// <response code="400">Invalid filter (<c>Request.ValidationFailed</c>, <c>Users.InvalidRole</c>, <c>Users.InvalidSort</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.view</c> (<c>Auth.PermissionDenied</c>) or <c>organizationId</c> used by a non-Super Admin (<c>Users.OrganizationFilterNotAllowed</c>).</response>
    [HttpGet]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<PagedResult<UserDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    public async Task<IActionResult> List([FromQuery] UserListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await users.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/users/{id}
    /// <summary>Gets one user.</summary>
    /// <response code="200">The user.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.view</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such user in the caller's organization (<c>Users.NotFound</c>).</response>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<UserDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await users.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/users
    /// <summary>Creates a user in the organization the request acts in.</summary>
    /// <remarks>
    /// Without <c>password</c> (recommended) the user is e-mailed a code to choose their own password
    /// (<c>POST /api/v1/auth/reset-password</c>). The e-mail address must not belong to an existing account.
    /// </remarks>
    /// <response code="201">The user was created; <c>Location</c> points to it.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Users.InvalidRole</c>, <c>Account.WeakPassword</c>, <c>Account.InvalidEmail</c>) or no organization selected (<c>Users.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c> (<c>Auth.PermissionDenied</c>), creating an Owner without being one (<c>Users.OwnerRequired</c>) or a role granting more than you hold (<c>Users.RoleExceedsYourPermissions</c>).</response>
    /// <response code="409">The e-mail address belongs to an existing account (<c>Account.DuplicateEmail</c>) or member (<c>Users.AlreadyMember</c>), or the role was just deleted (<c>Users.RoleNoLongerExists</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<UserDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await users.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value)
            : ErrorResult(result.Error);
    }

    // PUT /api/v1/users/{id}
    /// <summary>Updates the profile fields an administrator may change: display name and phone number.</summary>
    /// <remarks>Both fields are replaced; send <c>null</c> to clear one. The user stays signed in.</remarks>
    /// <response code="200">The updated user.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Users.Invalid</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c>, or the user is protected (<c>Users.OwnerRequired</c>, <c>Users.PlatformAccountProtected</c>).</response>
    /// <response code="404">No such user in the caller's organization (<c>Users.NotFound</c>).</response>
    /// <response code="409">Changed by someone else at the same time (<c>Users.ConcurrentChange</c>).</response>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<UserDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await users.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/users/{id}/role
    /// <summary>Changes the user's role in the organization the request acts in. Applies on the user's next request.</summary>
    /// <remarks>
    /// <c>role</c> is a role id or name among the roles available to the organization (<c>GET /api/v1/roles</c>): system roles
    /// and shared roles the Super Admin enabled for it, and its own custom roles.
    /// </remarks>
    /// <response code="200">The updated user.</response>
    /// <response code="400">Unknown role (<c>Users.InvalidRole</c>), invalid input or no organization selected (<c>Users.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c>, own role (<c>Users.CannotChangeOwnAccess</c>), Owner rules (<c>Users.OwnerRequired</c>), a role granting more than you hold (<c>Users.RoleExceedsYourPermissions</c>) or platform account (<c>Users.PlatformAccountProtected</c>).</response>
    /// <response code="404">No such user in the organization (<c>Users.NotFound</c>).</response>
    /// <response code="409">Would remove the last active Owner (<c>Users.LastOwner</c>) or the role was just deleted (<c>Users.RoleNoLongerExists</c>).</response>
    [HttpPatch("{id:guid}/role")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<UserDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeRole(Guid id, ChangeUserRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await users.ChangeRoleAsync(id, request.Role, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/users/{id}/group
    /// <summary>Changes the user's department or group in the organization the request acts in.</summary>
    /// <response code="200">The updated user.</response>
    /// <response code="400">Invalid input or no organization selected (<c>Users.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c>, or the user is protected (<c>Users.OwnerRequired</c>, <c>Users.PlatformAccountProtected</c>).</response>
    /// <response code="404">No such user in the organization (<c>Users.NotFound</c>).</response>
    [HttpPatch("{id:guid}/group")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<UserDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> ChangeGroup(Guid id, ChangeUserGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await users.ChangeDepartmentAsync(id, request.Department, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/users/{id}/status
    /// <summary>Activates or deactivates the user.</summary>
    /// <remarks>
    /// Changes the user's membership in the organization the request acts in: a deactivated member keeps their
    /// account but loses all access to this organization on their next request. A Super Admin acting in no
    /// organization changes the ACCOUNT instead: deactivation blocks sign-in and ends every session immediately.
    /// </remarks>
    /// <response code="200">The updated user.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>) or no organization selected (<c>Users.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c>, own status (<c>Users.CannotChangeOwnAccess</c>), Owner rules (<c>Users.OwnerRequired</c>) or platform account (<c>Users.PlatformAccountProtected</c>).</response>
    /// <response code="404">No such user in the organization (<c>Users.NotFound</c>).</response>
    /// <response code="409">Would deactivate the last active Owner (<c>Users.LastOwner</c>).</response>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<UserDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeUserStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await users.ChangeStatusAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/users/{id}/reset-password
    /// <summary>E-mails the user a code to choose a new password.</summary>
    /// <remarks>
    /// The current password keeps working until the user completes the reset with
    /// <c>POST /api/v1/auth/reset-password</c>. The caller never sees the code.
    /// </remarks>
    /// <response code="202">The e-mail was handed over for delivery.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c>, or the user is protected (<c>Users.OwnerRequired</c>, <c>Users.PlatformAccountProtected</c>).</response>
    /// <response code="404">No such user in the caller's organization (<c>Users.NotFound</c>).</response>
    [HttpPost("{id:guid}/reset-password")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> ResetPassword(Guid id, CancellationToken cancellationToken)
    {
        var result = await users.InitiatePasswordResetAsync(id, cancellationToken);
        return result.IsSuccess ? Accepted() : ErrorResult(result.Error);
    }
}
