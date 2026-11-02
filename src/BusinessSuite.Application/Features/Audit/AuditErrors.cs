using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Audit;

/// <summary>Errors returned by the audit log API. The codes are part of the API contract: do not rename them.</summary>
public static class AuditErrors
{
    /// <summary>
    /// The entry does not exist OR belongs to another organization or to the platform (deliberately the same, so ids
    /// reveal nothing).
    /// </summary>
    public static readonly Error NotFound =
        Error.NotFound("Audit.NotFound", "No audit entry with this id exists in your organization.");

    /// <summary>An organization user's request acts in no organization.</summary>
    public static readonly Error OrganizationRequired =
        Error.Validation("Audit.OrganizationRequired", "Select the organization to act in with the X-Organization-Id header.");

    /// <summary>An organization user tried to read another organization's audit log.</summary>
    public static readonly Error OrganizationFilterNotAllowed =
        Error.Forbidden("Audit.OrganizationFilterNotAllowed", "Only platform operators can read the audit log of other organizations.");

    /// <summary>The time range is reversed.</summary>
    public static readonly Error InvalidRange =
        Error.Validation("Audit.InvalidRange", "'from' must be earlier than 'to'.");
}
