using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Organizations;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// Organization (tenant) management API: list, view, create, rename, suspend and reactivate organizations, and see an
/// organization's members and the roles it can use. Version 1: <c>/api/v1/organizations</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who sees what.</b> Platform operators (Super Admins and Platform Administrators, <c>platform.organizations.manage</c>)
/// see and rename every organization and are the only ones who create, suspend or reactivate them. Everyone else sees
/// only the organization the request acts in (<c>X-Organization-Id</c>, or their only organization, checked on the
/// server); any other id answers 404 <c>Organizations.NotFound</c>, exactly like an id that does not exist.
/// </para>
/// <para>
/// <b>Suspension</b> removes every member's access to the organization from their next request; nothing is deleted
/// and reactivating restores it. The member and role lists reuse the rules of the users and roles APIs.
/// See docs/API.md §7.
/// </para>
/// </remarks>
[Route("api/v1/organizations")]
[Authorize(AuthenticationSchemes = BearerScheme)]
public sealed class OrganizationsController(IOrganizationService organizations) : AppApiController
{
    // GET /api/v1/organizations
    /// <summary>Lists organizations with search, status filter and paging.</summary>
    /// <remarks>Platform operators see every organization; everyone else sees the one the request acts in.</remarks>
    /// <response code="200">One page of organizations with <c>totalCount</c> and <c>totalPages</c>.</response>
    /// <response code="400">Invalid filter (<c>Request.ValidationFailed</c>) or no organization selected (<c>Organizations.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>organization.view</c> or <c>platform.organizations.manage</c> (<c>Auth.PermissionDenied</c>).</response>
    [HttpGet]
    [RequireAnyPermission(Permissions.Organization.View, Permissions.Platform.OrganizationsManage)]
    [ProducesResponseType<PagedResult<OrganizationDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    public async Task<IActionResult> List([FromQuery] OrganizationListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await organizations.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/organizations/{id}
    /// <summary>Gets one organization.</summary>
    /// <response code="200">The organization.</response>
    /// <response code="400">No organization selected (<c>Organizations.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>organization.view</c> or <c>platform.organizations.manage</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such organization, or not the one you act in (<c>Organizations.NotFound</c>).</response>
    [HttpGet("{id:guid}")]
    [RequireAnyPermission(Permissions.Organization.View, Permissions.Platform.OrganizationsManage)]
    [ProducesResponseType<OrganizationDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await organizations.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/organizations
    /// <summary>Creates an organization, optionally with its first administrator (platform operators only).</summary>
    /// <remarks>
    /// <para>The organization starts active. Without <c>initialAdmin</c> it has no members.</para>
    /// <para>
    /// With <c>initialAdmin</c> (<b>Super Admins only</b>) the administrator's account is created in the same transaction
    /// and made the organization's <c>Owner</c>, so the organization is usable at once and any failure creates nothing.
    /// Without a <c>password</c> (recommended) they are e-mailed a code to choose their own
    /// (<c>POST /api/v1/auth/reset-password</c>). The e-mail address must not belong to an existing account.
    /// </para>
    /// </remarks>
    /// <response code="201">The organization was created; <c>Location</c> points to it.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Organizations.Invalid</c>, <c>Account.WeakPassword</c>, <c>Account.InvalidEmail</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Not a platform operator (<c>Auth.PermissionDenied</c>), or <c>initialAdmin</c> sent by someone who is not a Super Admin (<c>Organizations.SuperAdminRequired</c>).</response>
    /// <response code="409">The administrator's e-mail address belongs to an existing account (<c>Account.DuplicateEmail</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Platform.OrganizationsManage)]
    [ProducesResponseType<OrganizationDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(CreateOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await organizations.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value)
            : ErrorResult(result.Error);
    }

    // PUT /api/v1/organizations/{id}
    /// <summary>Renames an organization.</summary>
    /// <remarks>Platform operators rename any organization; organization administrators (<c>organization.manage</c>) their own.</remarks>
    /// <response code="200">The updated organization.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Organizations.Invalid</c>) or no organization selected (<c>Organizations.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>organization.manage</c> or <c>platform.organizations.manage</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such organization, or not the one you act in (<c>Organizations.NotFound</c>).</response>
    /// <response code="409">Changed by someone else at the same time (<c>Organizations.ConcurrentChange</c>).</response>
    [HttpPut("{id:guid}")]
    [RequireAnyPermission(Permissions.Organization.Manage, Permissions.Platform.OrganizationsManage)]
    [ProducesResponseType<OrganizationDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, UpdateOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await organizations.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PATCH /api/v1/organizations/{id}/status
    /// <summary>Suspends or reactivates an organization (platform operators only).</summary>
    /// <remarks>
    /// A suspended organization gives its members no access from their next request: no permissions, and
    /// <c>X-Organization-Id</c> naming it is refused. Nothing is deleted. Sending the current status changes nothing.
    /// </remarks>
    /// <response code="200">The organization with its new status.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Not a platform operator (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such organization (<c>Organizations.NotFound</c>).</response>
    /// <response code="409">Changed by someone else at the same time (<c>Organizations.ConcurrentChange</c>).</response>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Platform.OrganizationsManage)]
    [ProducesResponseType<OrganizationDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeOrganizationStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await organizations.ChangeStatusAsync(id, request.IsActive!.Value, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/organizations/{id}/users
    /// <summary>Lists the organization's users, with the filters, sorting and paging of <c>GET /api/v1/users</c>.</summary>
    /// <remarks>
    /// Same result as <c>GET /api/v1/users?organizationId={id}</c>: organization users get their own organization's
    /// members, Super Admins any organization's.
    /// </remarks>
    /// <response code="200">One page of users with <c>totalCount</c> and <c>totalPages</c>.</response>
    /// <response code="400">Invalid filter (<c>Request.ValidationFailed</c>, <c>Users.InvalidRole</c>) or no organization selected (<c>Organizations.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.view</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such organization, or not the one you act in (<c>Organizations.NotFound</c>).</response>
    [HttpGet("{id:guid}/users")]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<PagedResult<UserDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> ListUsers(Guid id, [FromQuery] OrganizationUsersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await organizations.ListUsersAsync(id, request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/organizations/{id}/roles
    /// <summary>Lists the roles the organization can use (and assign to its users).</summary>
    /// <remarks>
    /// Same result as <c>GET /api/v1/roles?organizationId={id}</c>: system roles, shared roles the Super Admin enabled
    /// for the organization, and its own custom roles.
    /// </remarks>
    /// <response code="200">The roles: system roles first, then shared, then custom ones.</response>
    /// <response code="400">Invalid filter (<c>Request.ValidationFailed</c>) or no organization selected (<c>Organizations.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.view</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such organization, or not the one you act in (<c>Organizations.NotFound</c>).</response>
    [HttpGet("{id:guid}/roles")]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<IReadOnlyList<RoleDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> ListRoles(Guid id, [FromQuery] OrganizationRolesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await organizations.ListRolesAsync(id, request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
