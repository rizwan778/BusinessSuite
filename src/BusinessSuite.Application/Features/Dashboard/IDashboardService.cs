namespace BusinessSuite.Application.Features.Dashboard;

/// <summary>
/// Use cases behind the dashboard page.
/// </summary>
/// <remarks>
/// Phase 4 extends this service with sales, stock and receivables figures. Those queries
/// will be tenant-scoped and run on the server; the browser never calculates totals.
/// </remarks>
public interface IDashboardService
{
    /// <summary>Returns the greeting and clock information for the signed-in user.</summary>
    Task<DashboardOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
}
