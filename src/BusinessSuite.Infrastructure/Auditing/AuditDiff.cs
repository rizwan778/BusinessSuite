namespace BusinessSuite.Infrastructure.Auditing;

/// <summary>
/// Describes what an update changed for an audit entry: only the fields whose value differs, as <c>{ field: { from, to } }</c>.
/// </summary>
/// <remarks>
/// Take a snapshot (a dictionary of the audited fields) before and after applying the change. Audit only fields that are fine to
/// show to people who may read the audit log (SKUs, names, flags, prices); never e-mail addresses, phone numbers or secrets.
/// </remarks>
internal static class AuditDiff
{
    /// <summary>The fields of <paramref name="after"/> that differ from <paramref name="before"/> (same keys in both).</summary>
    public static IReadOnlyDictionary<string, object> Changes(IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var changes = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (field, now) in after)
        {
            before.TryGetValue(field, out var was);
            if (!Equals(was, now))
            {
                changes[field] = new { from = was, to = now };
            }
        }

        return changes;
    }
}
