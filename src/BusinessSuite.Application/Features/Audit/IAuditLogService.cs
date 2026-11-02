using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Audit;

/// <summary>
/// Reads the audit log. Entries are written by the services that make the changes, in the same database transaction
/// as the change, so a change is never saved without its entry (and a refused or failed change leaves none).
/// </summary>
/// <remarks>Which entries a caller may read is decided by <see cref="AuditAccessRules"/>.</remarks>
public interface IAuditLogService
{
    /// <summary>One page of entries visible to the caller, newest first.</summary>
    Task<Result<PagedResult<AuditLogSummary>>> ListAsync(AuditLogListQuery query, CancellationToken cancellationToken = default);

    /// <summary>One entry, if the caller may read it; otherwise <see cref="AuditErrors.NotFound"/>.</summary>
    Task<Result<AuditLogDetails>> GetAsync(Guid auditLogId, CancellationToken cancellationToken = default);
}
