using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Api.Audit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers.Api;

/// <summary>
/// Audit log API: who created users and changed roles, groups, permissions, status and organizations, when and from
/// where. Read-only: entries are written by the changes themselves and can never be edited or deleted.
/// Version 1: <c>/api/v1/audit-logs</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Organization isolation.</b> Organization users (<c>audit.view</c>: Owners and Administrators by default) read only
/// the log of the organization the request acts in (<c>X-Organization-Id</c>, or their only organization, checked on the
/// server). An entry of another organization, or a platform-wide one, answers 404 <c>Audit.NotFound</c>, exactly like an
/// id that does not exist.
/// </para>
/// <para>
/// <b>Platform operators</b> (Super Admins, and <c>platform.audit.view</c>: Platform Administrators) read every entry,
/// platform-wide changes included (no organization: shared roles, account status), and may filter by organization.
/// </para>
/// See docs/API.md §9 and docs/SECURITY.md §7.
/// </remarks>
[Route("api/v1/audit-logs")]
[Authorize(AuthenticationSchemes = BearerScheme)]
[RequireAnyPermission(Permissions.Audit.View, Permissions.Platform.AuditView)]
public sealed class AuditLogsController(IAuditLogService auditLogs) : AppApiController
{
    // GET /api/v1/audit-logs
    /// <summary>Lists audit entries, newest first, with filters and paging.</summary>
    /// <remarks>The list carries what happened to what; open an entry for the changed values, correlation id and IP address.</remarks>
    /// <response code="200">One page of entries with <c>totalCount</c> and <c>totalPages</c>.</response>
    /// <response code="400">Invalid filter (<c>Request.ValidationFailed</c>, <c>Audit.InvalidRange</c>) or no organization selected (<c>Audit.OrganizationRequired</c>).</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>audit.view</c> (<c>Auth.PermissionDenied</c>) or <c>organizationId</c> used by an organization user (<c>Audit.OrganizationFilterNotAllowed</c>).</response>
    [HttpGet]
    [ProducesResponseType<PagedResult<AuditLogSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    public async Task<IActionResult> List([FromQuery] AuditLogListRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await auditLogs.ListAsync(request.ToQuery(), cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }

    // GET /api/v1/audit-logs/{id}
    /// <summary>Gets one audit entry with the values that changed, the correlation id and the caller's IP address.</summary>
    /// <response code="200">The entry.</response>
    /// <response code="401">Not signed in (<c>Auth.Unauthorized</c>).</response>
    /// <response code="403">Missing <c>audit.view</c> (<c>Auth.PermissionDenied</c>).</response>
    /// <response code="404">No such entry in the caller's organization (<c>Audit.NotFound</c>).</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<AuditLogDetails>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemContentType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemContentType)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var result = await auditLogs.GetAsync(id, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : ErrorResult(result.Error);
    }
}
