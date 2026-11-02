using System.Text.Json;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Auditing;

/// <summary>
/// <see cref="IAuditLogService"/> on SQL Server. Which entries a caller reaches is decided by
/// <see cref="AuditAccessRules"/>; this class only reads (the log is append-only).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Organization isolation:</b> organization users only reach entries whose organization is
/// <see cref="ICurrentUser.OrganizationId"/>. Every other entry, platform-wide ones included, is
/// <see cref="AuditErrors.NotFound"/>, exactly like an unknown id.</item>
/// <item>A stored <c>Details</c> value that is not valid JSON (edited by hand) is returned as <see langword="null"/> and
/// logged as a warning instead of failing the request.</item>
/// <item>Logs contain user, organization and entry ids only.</item>
/// </list>
/// </remarks>
internal sealed class AuditLogService(
    AppDbContext db,
    ICurrentUser currentUser,
    ILogger<AuditLogService> logger) : IAuditLogService
{
    public async Task<Result<PagedResult<AuditLogSummary>>> ListAsync(AuditLogListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var scope = AuditAccessRules.ListScope(currentUser, query.OrganizationId);
        if (scope.IsFailure)
        {
            logger.LogInformation("User {ActorId} was refused an audit log list ({Code}).", currentUser.UserId, scope.Error.Code);
            return scope.Error;
        }

        if (query.From is { } from && query.To is { } to && from >= to)
        {
            return AuditErrors.InvalidRange;
        }

        var entries = db.AuditLogs.AsNoTracking();
        if (scope.Value is { } organizationId)
        {
            entries = entries.Where(entry => entry.OrganizationId == organizationId);
        }

        if (Clean(query.Action) is { } action)
        {
            entries = entries.Where(entry => entry.Action == action);
        }

        if (Clean(query.EntityType) is { } entityType)
        {
            entries = entries.Where(entry => entry.EntityType == entityType);
        }

        if (query.EntityId is { } entityId)
        {
            entries = entries.Where(entry => entry.EntityId == entityId);
        }

        if (query.ActorUserId is { } actorUserId)
        {
            entries = entries.Where(entry => entry.ActorUserId == actorUserId);
        }

        if (query.From is { } since)
        {
            entries = entries.Where(entry => entry.OccurredAtUtc >= since);
        }

        if (query.To is { } until)
        {
            entries = entries.Where(entry => entry.OccurredAtUtc < until);
        }

        var page = new PageRequest(query.Page, query.PageSize);
        var totalCount = await entries.CountAsync(cancellationToken);
        var items = await entries
            .OrderByDescending(entry => entry.OccurredAtUtc).ThenByDescending(entry => entry.Id) // stable across pages
            .Skip(page.Skip).Take(page.PageSize)
            .Select(entry => new AuditLogSummary(
                entry.Id,
                entry.OccurredAtUtc,
                entry.OrganizationId,
                entry.ActorUserId,
                entry.Action,
                entry.EntityType,
                entry.EntityId))
            .ToListAsync(cancellationToken);

        return new PagedResult<AuditLogSummary>(items, page, totalCount);
    }

    public async Task<Result<AuditLogDetails>> GetAsync(Guid auditLogId, CancellationToken cancellationToken = default)
    {
        var entry = await db.AuditLogs.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == auditLogId, cancellationToken);
        if (entry is null || !AuditAccessRules.CanRead(currentUser, entry.OrganizationId))
        {
            // Unknown, or another organization's / the platform's: the caller cannot tell which. Worth a trace when probing ids.
            logger.LogInformation("User {ActorId} asked for audit entry {AuditLogId}, which does not exist or is outside their organization {OrganizationId}.",
                currentUser.UserId, auditLogId, currentUser.OrganizationId);
            return AuditErrors.NotFound;
        }

        return new AuditLogDetails(
            entry.Id,
            entry.OccurredAtUtc,
            entry.OrganizationId,
            entry.ActorUserId,
            entry.Action,
            entry.EntityType,
            entry.EntityId,
            ParseDetails(entry.Id, entry.Details),
            entry.CorrelationId,
            entry.IpAddress);
    }

    private JsonElement? ParseDetails(Guid auditLogId, string? details)
    {
        if (string.IsNullOrEmpty(details))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(details);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            logger.LogWarning("Audit entry {AuditLogId} has details that are not valid JSON; returned without them.", auditLogId);
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
