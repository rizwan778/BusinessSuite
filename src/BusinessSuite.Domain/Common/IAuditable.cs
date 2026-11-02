namespace BusinessSuite.Domain.Common;

/// <summary>
/// Marks an entity whose creation and last modification are stamped automatically.
/// </summary>
/// <remarks>
/// <para>
/// Do not set these values in your own code. The Infrastructure layer's
/// <c>AuditableEntityInterceptor</c> fills them in whenever <c>SaveChangesAsync</c> runs,
/// using the signed-in user and the current UTC time.
/// </para>
/// <para>
/// All instants are stored in UTC. Convert to an organization's time zone only when
/// displaying or reporting.
/// </para>
/// <para>
/// These columns answer "who created / last changed this row and when". The detailed
/// business audit trail (who changed what, old and new values) is a separate feature
/// planned for Phase 2.
/// </para>
/// </remarks>
public interface IAuditable
{
    /// <summary>When the row was first saved (UTC).</summary>
    DateTimeOffset CreatedAtUtc { get; }

    /// <summary>User who created the row, or <see langword="null"/> for system processes.</summary>
    Guid? CreatedByUserId { get; }

    /// <summary>When the row was last modified (UTC), or <see langword="null"/> if never modified.</summary>
    DateTimeOffset? UpdatedAtUtc { get; }

    /// <summary>User who last modified the row, or <see langword="null"/>.</summary>
    Guid? UpdatedByUserId { get; }
}
