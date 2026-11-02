using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Roles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// Role management API: list, view, create, rename and delete roles, configure their permissions, and (Super Admins)
/// decide which shared roles each organization may use. Version 1: <c>/api/v1/roles</c>. Assign roles to people with
/// <c>PATCH /api/v1/users/{id}/role</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Kinds of role.</b> System roles (Owner, Administrator, Manager, InventoryManager, SalesOperator, Accountant,
/// ReadOnly) and other <c>Platform</c> roles are shared and managed by Super Admins; <c>Organization</c> roles are custom
/// roles of one organization, managed by its administrators (<c>roles.manage</c>).
/// </para>
/// <para>
/// <b>Tenant isolation.</b> Everything happens in the organization the request acts in (<c>X-Organization-Id</c>, or
/// the caller's only organization, checked on the server). Organization users see the roles available to it; other
/// roles answer 404 <c>Roles.NotFound</c>.
/// </para>
/// <b>No escalation.</b> Only permissions you hold can be granted, and roles granting more than you hold cannot be
/// changed by you. Changes apply to everyone holding the role on their next request. See docs/API.md §6.
/// </remarks>
[Route("api/v1/roles")]
[Authorize(AuthenticationSchemes = BearerScheme)]
public sealed class RolesController(IRoleService roles) : AppApiController
{
    // GET /api/v1/roles
    /// <summary>Lists the roles available to the organization (Super Admins: every role, or one organization's).</summary>
    /// <response code="200">The roles: system roles first, then shared, then custom ones.</response>
    /// <response code="400">Invalid filter (<c>Request.ValidationFailed</c>) or no organization selected (<c>Roles.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.view</c> (<c>Auth.PermissionDenied</c>) or <c>organizationId</c> used by a non-Super Admin (<c>Roles.OrganizationFilterNotAllowed</c>).</response>
    [HttpGet]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<IReadOnlyList<RoleDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    public async Task<IActionResult> List([FromQuery] RoleListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await roles.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/roles/{id}
    /// <summary>Gets one role with its permissions and how many members hold it.</summary>
    /// <response code="200">The role.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.view</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such role available in the organization (<c>Roles.NotFound</c>).</response>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<RoleDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await roles.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/roles
    /// <summary>Creates a custom role in the organization the request acts in, or a shared role (Super Admins, <c>scope: Platform</c>).</summary>
    /// <response code="201">The role was created; <c>Location</c> points to it.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Roles.Invalid</c>, <c>Roles.UnknownPermission</c>) or no organization selected (<c>Roles.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.manage</c>, granting a permission you lack (<c>Roles.CannotGrantPermission</c>) or a shared role without being Super Admin (<c>Roles.SuperAdminRequired</c>).</response>
    /// <response code="409">The name is taken (<c>Roles.DuplicateName</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Roles.Manage)]
    [ProducesResponseType<RoleDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await roles.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value)
            : ErrorResult(result.Error);
    }

    // PUT /api/v1/roles/{id}
    /// <summary>Renames a custom role and replaces its description.</summary>
    /// <response code="200">The updated role.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Roles.Invalid</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.manage</c>, a shared role (<c>Roles.SuperAdminRequired</c>) or a role granting more than you hold (<c>Roles.RoleExceedsYourPermissions</c>).</response>
    /// <response code="404">No such role available in the organization (<c>Roles.NotFound</c>).</response>
    /// <response code="409">System roles cannot be renamed (<c>Roles.SystemRoleLocked</c>) or the name is taken (<c>Roles.DuplicateName</c>).</response>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Roles.Manage)]
    [ProducesResponseType<RoleDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await roles.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/roles/{id}
    /// <summary>Deletes a custom role that nobody holds.</summary>
    /// <response code="204">The role was deleted.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.manage</c>, a shared role (<c>Roles.SuperAdminRequired</c>) or a role granting more than you hold (<c>Roles.RoleExceedsYourPermissions</c>).</response>
    /// <response code="404">No such role available in the organization (<c>Roles.NotFound</c>).</response>
    /// <response code="409">A system role (<c>Roles.SystemRoleLocked</c>) or members still hold it (<c>Roles.InUse</c>).</response>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Roles.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await roles.DeleteAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }

    // GET /api/v1/roles/{id}/permissions
    /// <summary>Gets a role's permissions.</summary>
    /// <response code="200">The permissions (sorted). <c>permissionsLocked</c> is true for the Owner.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.view</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such role available in the organization (<c>Roles.NotFound</c>).</response>
    [HttpGet("{id:guid}/permissions")]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<RolePermissionsDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> GetPermissions(Guid id, CancellationToken cancellationToken)
    {
        var result = await roles.GetPermissionsAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/roles/{id}/permissions
    /// <summary>Replaces a role's permissions. Applies to everyone holding the role on their next request.</summary>
    /// <remarks>
    /// Organization administrators configure their organization's custom roles; Super Admins also configure shared
    /// roles, including the system roles (except the Owner, which always holds every organization permission).
    /// </remarks>
    /// <response code="200">The role's new permissions.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>) or an unknown/platform permission (<c>Roles.UnknownPermission</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.manage</c>, a shared role (<c>Roles.SuperAdminRequired</c>), granting a permission you lack (<c>Roles.CannotGrantPermission</c>) or a role granting more than you hold (<c>Roles.RoleExceedsYourPermissions</c>).</response>
    /// <response code="404">No such role available in the organization (<c>Roles.NotFound</c>).</response>
    /// <response code="409">The Owner's permissions are fixed (<c>Roles.OwnerPermissionsLocked</c>).</response>
    [HttpPut("{id:guid}/permissions")]
    [RequirePermission(Permissions.Roles.Manage)]
    [ProducesResponseType<RolePermissionsDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> SetPermissions(Guid id, SetRolePermissionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await roles.SetPermissionsAsync(id, request.Permissions!, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // PUT /api/v1/roles/{id}/availability
    /// <summary>Super Admins: decides which organizations may use a shared (platform) role.</summary>
    /// <remarks>
    /// Organization administrators can only assign roles available to their organization. Removing an organization
    /// is refused while members there hold the role. The Owner stays available everywhere.
    /// </remarks>
    /// <response code="200">The role with its new availability.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>), an organization role (<c>Roles.NotAPlatformRole</c>) or unknown organizations (<c>Roles.UnknownOrganization</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Not a Super Admin (<c>Auth.SuperAdminRequired</c>).</response>
    /// <response code="404">No such role (<c>Roles.NotFound</c>).</response>
    /// <response code="409">Members of an excluded organization hold the role (<c>Roles.InUse</c>) or it is the Owner (<c>Roles.OwnerAvailabilityLocked</c>).</response>
    [HttpPut("{id:guid}/availability")]
    [RequireSuperAdmin]
    [ProducesResponseType<RoleDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> SetAvailability(Guid id, SetRoleAvailabilityRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await roles.SetAvailabilityAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
