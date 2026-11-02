using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>
/// TEST-ONLY endpoints (added to the app by <see cref="BusinessSuiteWebApplicationFactory"/>) that exercise each
/// kind of authorization exactly as a real feature controller would use it.
/// </summary>
[Route("api/v1/test/access")]
[Authorize(AuthenticationSchemes = BearerScheme)]
public sealed class AccessProbeController : AppApiController
{
    [HttpPost("invoices")]
    [RequirePermission(Permissions.Invoices.Create)]
    public IActionResult CreateInvoice() => Ok(new { allowed = true });

    [HttpPost("invoices/cancel")]
    [RequirePermission(Permissions.Invoices.View)]
    [RequirePermission(Permissions.Invoices.Cancel)]
    public IActionResult CancelInvoice() => Ok(new { allowed = true });

    [HttpPost("invoices/cancel-or-export")]
    [RequireAnyPermission(Permissions.Invoices.Cancel, Permissions.Reports.Export)]
    public IActionResult CancelOrExport() => Ok(new { allowed = true });

    [HttpGet("owners")]
    [RequireOrganizationRole(OrganizationRoles.Owner, OrganizationRoles.Administrator)]
    public IActionResult OwnersAndAdministrators() => Ok(new { allowed = true });

    [HttpGet("super-admin")]
    [RequireSuperAdmin]
    public IActionResult SuperAdminOnly() => Ok(new { allowed = true });

    [HttpGet("platform")]
    [Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]
    public IActionResult PlatformOnly() => Ok(new { allowed = true });

    /// <summary>What Application services see through <see cref="ICurrentUser"/>.</summary>
    [HttpGet("current-user")]
    public IActionResult CurrentUser([FromServices] ICurrentUser currentUser) => Ok(new
    {
        currentUser.UserId,
        currentUser.OrganizationId,
        currentUser.IsSuperAdmin,
        canCreateInvoices = currentUser.HasPermission(Permissions.Invoices.Create),
    });
}
