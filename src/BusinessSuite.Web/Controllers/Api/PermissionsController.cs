using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// The permission catalogue: <c>/api/v1/permissions</c>. Permissions are defined by the application (each one is
/// enforced by endpoints), so they are read-only; roles bundle them (<c>/api/v1/roles</c>).
/// </summary>
[Route("api/v1/permissions")]
[Authorize(AuthenticationSchemes = BearerScheme)]
public sealed class PermissionsController(IRoleService roles) : AppApiController
{
    // GET /api/v1/permissions
    /// <summary>
    /// Lists every permission a role can grant, grouped by area (User Management, Product Management, Inventory, Sales,
    /// Purchases, Accounts, Reports, Organization Management). Super Admins also see the platform group, which roles
    /// cannot grant.
    /// </summary>
    /// <response code="200">The permission groups.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>roles.view</c> (<c>Auth.PermissionDenied</c>).</response>
    [HttpGet]
    [RequirePermission(Permissions.Roles.View)]
    [ProducesResponseType<IReadOnlyList<PermissionGroup>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    public IActionResult List() => Ok(roles.ListPermissions());
}
