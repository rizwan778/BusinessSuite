using System.Text.Json;
using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Domain.Auditing;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Auditing;

/// <summary>
/// The audit logging service: adds an <see cref="AuditLog"/> entry for an administrative change to the SAME
/// <see cref="AppDbContext"/> the change is made in, so both are saved by the same <c>SaveChanges</c> (and the same
/// transaction). A change is therefore never committed without its entry, and a refused or failed change leaves none.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Call <see cref="Record"/> right before the <c>SaveChanges</c> (or Identity call) that commits the change, and inside
/// <c>DatabaseTransactions.RunAsync</c> when one is used: it starts with a cleared change tracker.</item>
/// <item>If the change is then refused without an exception (e.g. Identity validation), call <see cref="Discard"/> so a
/// later save cannot pick the entry up.</item>
/// <item>The actor, correlation id and IP address come from the server (<see cref="ICurrentUser"/>,
/// <see cref="IRequestInfo"/>), never from the request body.</item>
/// <item>The details must never contain passwords, tokens, reset codes, e-mail addresses or phone numbers.</item>
/// </list>
/// </remarks>
internal sealed class AuditTrail(
    AppDbContext db,
    ICurrentUser currentUser,
    IRequestInfo requestInfo,
    TimeProvider timeProvider,
    ILogger<AuditTrail> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Adds an entry to the context; it is saved with the change.</summary>
    /// <param name="action">One of <c>AuditActions</c>.</param>
    /// <param name="entityType">One of <c>AuditActions.EntityTypes</c>.</param>
    /// <param name="entityId">The record changed.</param>
    /// <param name="organizationId">The organization the change belongs to; <see langword="null"/> for a platform-wide change.</param>
    /// <param name="details">An object with the values that changed (serialized as camelCase JSON), or <see langword="null"/>.</param>
    public AuditLog Record(string action, string entityType, Guid? entityId, Guid? organizationId, object? details = null)
    {
        var entry = AuditLog.Record(
            timeProvider.GetUtcNow(),
            organizationId,
            currentUser.UserId,
            action,
            entityType,
            entityId,
            details is null ? null : JsonSerializer.Serialize(details, JsonOptions),
            requestInfo.CorrelationId,
            requestInfo.IpAddress);

        db.AuditLogs.Add(entry);
        logger.LogDebug("Audit entry {Action} for {EntityType} {EntityId} (organization {OrganizationId}) added by user {ActorId}.",
            action, entityType, entityId, organizationId, currentUser.UserId);
        return entry;
    }

    /// <summary>Takes back an entry whose change was refused after <see cref="Record"/> (nothing was saved).</summary>
    public void Discard(AuditLog entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        db.Entry(entry).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        logger.LogDebug("Audit entry {Action} for {EntityType} {EntityId} discarded: the change was refused.", entry.Action, entry.EntityType, entry.EntityId);
    }
}
