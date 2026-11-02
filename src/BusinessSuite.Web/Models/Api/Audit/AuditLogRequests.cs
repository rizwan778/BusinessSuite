using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Domain.Auditing;

namespace BusinessSuite.Web.Models.Api.Audit;

/// <summary>
/// Query string of <c>GET /api/v1/audit-logs</c>, e.g.
/// <c>?action=user.role_changed&amp;entityId=3f2c...&amp;from=2026-11-01T00:00:00Z&amp;page=2</c>.
/// </summary>
public sealed class AuditLogListRequest
{
    /// <summary>
    /// Only this organization's entries. <b>Platform operators only</b>: organization users always see their own
    /// organization, and naming another one answers 403 <c>Audit.OrganizationFilterNotAllowed</c>.
    /// </summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Only this action, e.g. <c>user.role_changed</c> (see docs/API.md for the list).</summary>
    [StringLength(AuditLog.ActionMaxLength)]
    public string? Action { get; set; }

    /// <summary>Only this kind of record: <c>User</c>, <c>Role</c>, <c>Group</c> or <c>Organization</c>.</summary>
    [StringLength(AuditLog.EntityTypeMaxLength)]
    public string? EntityType { get; set; }

    /// <summary>Only entries about this record (e.g. a user id).</summary>
    public Guid? EntityId { get; set; }

    /// <summary>Only changes made by this user.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Only changes at or after this moment (ISO 8601).</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Only changes before this moment (ISO 8601).</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>1-based page number.</summary>
    [Range(1, PageRequest.MaxPage)]
    public int Page { get; set; } = 1;

    /// <summary>Rows per page, 1 to 100.</summary>
    [Range(1, PageRequest.MaxPageSize)]
    public int PageSize { get; set; } = PageRequest.DefaultPageSize;

    public AuditLogListQuery ToQuery() => new()
    {
        OrganizationId = OrganizationId,
        Action = Action,
        EntityType = EntityType,
        EntityId = EntityId,
        ActorUserId = ActorUserId,
        From = From,
        To = To,
        Page = Page,
        PageSize = PageSize,
    };
}
