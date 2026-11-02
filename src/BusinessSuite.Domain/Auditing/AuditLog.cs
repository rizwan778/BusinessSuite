using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Auditing;

/// <summary>
/// One administrative change, recorded for traceability (<c>audit.AuditLogs</c>): who did what, to which record, in
/// which organization, when, and from where.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Append-only.</b> Entries have no methods that change them, and the database refuses UPDATE and DELETE on the
/// table (trigger <c>TR_AuditLogs_AppendOnly</c>).</item>
/// <item><b>Organization scope.</b> <see cref="OrganizationId"/> is the organization the change belongs to; organization
/// users only ever see their organization's entries. <see langword="null"/> marks a platform-wide change (shared roles,
/// account status), visible to platform operators only.</item>
/// <item><b>No foreign keys.</b> An entry must outlive the role, group or user it describes.</item>
/// <item><see cref="Details"/> is a JSON object of the values that changed. It never holds passwords, tokens, reset codes,
/// e-mail addresses or phone numbers.</item>
/// </list>
/// </remarks>
public sealed class AuditLog : Entity
{
    /// <summary>Maximum length of <see cref="Action"/>.</summary>
    public const int ActionMaxLength = 100;

    /// <summary>Maximum length of <see cref="EntityType"/>.</summary>
    public const int EntityTypeMaxLength = 50;

    /// <summary>Maximum length of <see cref="CorrelationId"/> (the correlation id middleware accepts up to 64 characters).</summary>
    public const int CorrelationIdMaxLength = 64;

    /// <summary>Maximum length of <see cref="IpAddress"/> (an IPv6 address with an IPv4 tail is 45 characters).</summary>
    public const int IpAddressMaxLength = 45;

    private AuditLog()
    {
    }

    /// <summary>When the change was made (UTC).</summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>The organization the change belongs to; <see langword="null"/> for a platform-wide change.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Who made the change; <see langword="null"/> for system processes.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>What happened, e.g. <c>user.role_changed</c> (stable codes, see <c>AuditActions</c>).</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>The kind of record changed, e.g. <c>User</c>, <c>Role</c>, <c>Group</c>, <c>Organization</c>.</summary>
    public string EntityType { get; private set; } = string.Empty;

    /// <summary>The record changed.</summary>
    public Guid? EntityId { get; private set; }

    /// <summary>JSON object with the values that changed; <see langword="null"/> when there is nothing to add.</summary>
    public string? Details { get; private set; }

    /// <summary>The request's correlation id (also in the response header and every log line of the request).</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>The caller's IP address as seen by the server.</summary>
    public string? IpAddress { get; private set; }

    /// <summary>A new entry. Values longer than the columns allow are cut, never rejected: the change itself must not fail.</summary>
    public static AuditLog Record(
        DateTimeOffset occurredAtUtc,
        Guid? organizationId,
        Guid? actorUserId,
        string action,
        string entityType,
        Guid? entityId,
        string? details,
        string? correlationId,
        string? ipAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        return new AuditLog
        {
            OccurredAtUtc = occurredAtUtc,
            OrganizationId = organizationId,
            ActorUserId = actorUserId,
            Action = Truncate(action, ActionMaxLength)!,
            EntityType = Truncate(entityType, EntityTypeMaxLength)!,
            EntityId = entityId,
            Details = details,
            CorrelationId = Truncate(correlationId, CorrelationIdMaxLength),
            IpAddress = Truncate(ipAddress, IpAddressMaxLength),
        };
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
