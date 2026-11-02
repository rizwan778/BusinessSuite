using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Groups;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Groups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// Group (department) management API: list, view, create, rename and delete the groups of an organization.
/// Version 1: <c>/api/v1/groups</c>. Put people in a group with <c>PATCH /api/v1/users/{id}/group</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant isolation.</b> Everything happens in the organization the request acts in (<c>X-Organization-Id</c>, or
/// the caller's only organization, checked on the server for every request); Super Admins choose theirs with the
/// header. No request carries an organization id, so another organization's group answers 404 <c>Groups.NotFound</c>,
/// exactly like an id that does not exist.
/// </para>
/// <para>
/// <b>Groups and departments.</b> A group is a named department: members belong to the group whose name equals their
/// <c>department</c>. Renaming a group renames the department of its members in one transaction. A group with members
/// cannot be deleted (409 <c>Groups.InUse</c>); move them first.
/// </para>
/// Reads need <c>users.view</c>, changes need <c>users.manage</c>. See docs/API.md §8.
/// </remarks>
[Route("api/v1/groups")]
[Authorize(AuthenticationSchemes = BearerScheme)]
public sealed class GroupsController(IGroupService groups) : AppApiController
{
    // GET /api/v1/groups
    /// <summary>Lists the organization's groups with search and paging.</summary>
    /// <response code="200">One page of groups (by name) with <c>totalCount</c> and <c>totalPages</c>.</response>
    /// <response code="400">Invalid filter (<c>Request.ValidationFailed</c>) or no organization selected (<c>Groups.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.view</c> (<c>Auth.PermissionDenied</c>).</response>
    [HttpGet]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<PagedResult<GroupDetails>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    public async Task<IActionResult> List([FromQuery] GroupListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await groups.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/groups/{id}
    /// <summary>Gets one group with its member count.</summary>
    /// <response code="200">The group.</response>
    /// <response code="400">No organization selected (<c>Groups.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.view</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such group in the organization (<c>Groups.NotFound</c>).</response>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Users.View)]
    [ProducesResponseType<GroupDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await groups.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // POST /api/v1/groups
    /// <summary>Creates a group in the organization the request acts in.</summary>
    /// <response code="201">The group was created; <c>Location</c> points to it.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Groups.Invalid</c>) or no organization selected (<c>Groups.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="409">The organization already has a group with this name (<c>Groups.DuplicateName</c>).</response>
    [HttpPost]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<GroupDetails>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Create(CreateGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await groups.CreateAsync(request.ToCommand(), cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value)
            : ErrorResult(result.Error);
    }

    // PUT /api/v1/groups/{id}
    /// <summary>Renames a group and replaces its description. Members move to the new name with it.</summary>
    /// <response code="200">The updated group.</response>
    /// <response code="400">Invalid input (<c>Request.ValidationFailed</c>, <c>Groups.Invalid</c>) or no organization selected (<c>Groups.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such group in the organization (<c>Groups.NotFound</c>).</response>
    /// <response code="409">The name is taken (<c>Groups.DuplicateName</c>) or the group was changed by someone else at the same time (<c>Groups.ConcurrentChange</c>).</response>
    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType<GroupDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Update(Guid id, UpdateGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await groups.UpdateAsync(id, request.ToCommand(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // DELETE /api/v1/groups/{id}
    /// <summary>Deletes a group that nobody belongs to.</summary>
    /// <response code="204">The group was deleted.</response>
    /// <response code="400">No organization selected (<c>Groups.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>users.manage</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such group in the organization (<c>Groups.NotFound</c>).</response>
    /// <response code="409">Members still belong to it (<c>Groups.InUse</c>) or it was changed by someone else at the same time (<c>Groups.ConcurrentChange</c>).</response>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Users.Manage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemContentType)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await groups.DeleteAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ErrorResult(result.Error);
    }
}
