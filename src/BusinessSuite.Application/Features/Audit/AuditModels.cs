using System.Text.Json;

namespace BusinessSuite.Application.Features.Audit;

/// <summary>An audit entry as listed by <c>GET /api/v1/audit-logs</c>.</summary>
/// <param name="Id">The entry's id.</param>
/// <param name="OccurredAtUtc">When the change was made.</param>
/// <param name="OrganizationId">The organization the change belongs to; <see langword="null"/> for a platform-wide change.</param>
/// <param name="ActorUserId">Who made the change; <see langword="null"/> for system processes.</param>
/// <param name="Action">What happened, one of <see cref="AuditActions"/>, e.g. <c>user.role_changed</c>.</param>
/// <param name="EntityType">The kind of record changed: <c>User</c>, <c>Role</c>, <c>Group</c> or <c>Organization</c>.</param>
/// <param name="EntityId">The record changed.</param>
public sealed record AuditLogSummary(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    Guid? OrganizationId,
    Guid? ActorUserId,
    string Action,
    string EntityType,
    Guid? EntityId);

/// <summary>One audit entry with everything recorded (<c>GET /api/v1/audit-logs/{id}</c>).</summary>
/// <param name="Id">The entry's id.</param>
/// <param name="OccurredAtUtc">When the change was made.</param>
/// <param name="OrganizationId">The organization the change belongs to; <see langword="null"/> for a platform-wide change.</param>
/// <param name="ActorUserId">Who made the change; <see langword="null"/> for system processes.</param>
/// <param name="Action">What happened, one of <see cref="AuditActions"/>.</param>
/// <param name="EntityType">The kind of record changed.</param>
/// <param name="EntityId">The record changed.</param>
/// <param name="Details">The values that changed, as a JSON object (e.g. <c>{"from":"Sales","to":"Retail"}</c>); <see langword="null"/> when none.</param>
/// <param name="CorrelationId">The request's correlation id: search the server logs for it.</param>
/// <param name="IpAddress">The caller's IP address as seen by the server.</param>
public sealed record AuditLogDetails(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    Guid? OrganizationId,
    Guid? ActorUserId,
    string Action,
    string EntityType,
    Guid? EntityId,
    JsonElement? Details,
    string? CorrelationId,
    string? IpAddress);

/// <summary>Filters and paging for <see cref="IAuditLogService.ListAsync"/>. Every value is optional; newest first.</summary>
public sealed record AuditLogListQuery
{
    /// <summary>Only this organization's entries. Platform operators only; others always see their own organization.</summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>Only this action, e.g. <c>user.role_changed</c>.</summary>
    public string? Action { get; init; }

    /// <summary>Only this kind of record, e.g. <c>User</c>.</summary>
    public string? EntityType { get; init; }

    /// <summary>Only entries about this record.</summary>
    public Guid? EntityId { get; init; }

    /// <summary>Only changes made by this user.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Only changes at or after this moment.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only changes before this moment.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page (capped at <c>PageRequest.MaxPageSize</c>).</summary>
    public int PageSize { get; init; } = Common.Paging.PageRequest.DefaultPageSize;
}
