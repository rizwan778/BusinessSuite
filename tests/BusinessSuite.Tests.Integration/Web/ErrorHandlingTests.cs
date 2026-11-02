using System.Net;
using BusinessSuite.Application.Features.Dashboard;
using BusinessSuite.Tests.Integration.Support;
using BusinessSuite.Web.Framework.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Web;

/// <summary>
/// Errors must produce a friendly page with a reference id, and never leak exception details.
/// </summary>
public sealed class ErrorHandlingTests(ErrorHandlingTests.FailingDashboardFactory factory)
    : IClassFixture<ErrorHandlingTests.FailingDashboardFactory>
{
    private const string SecretExceptionDetail = "Simulated failure: connection to db-server-42 refused";

    [Fact]
    public async Task Unhandled_exceptions_show_a_safe_error_page()
    {
        using var client = factory.CreateSignedInClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("Something went wrong", html, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretExceptionDetail, html, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", html, StringComparison.Ordinal);

        var correlationId = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.Contains(correlationId, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_pages_show_a_friendly_404_to_signed_in_users()
    {
        using var client = factory.CreateSignedInClient();

        using var response = await client.GetAsync(new Uri("/this/page/does-not-exist", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Page not found", html, StringComparison.Ordinal);
        Assert.True(response.Headers.Contains("Content-Security-Policy"), "Error pages must carry security headers too.");
    }

    /// <summary>The real application, with a dashboard service that always throws.</summary>
    public sealed class FailingDashboardFactory : BusinessSuiteWebApplicationFactory
    {
        protected override void ConfigureTestServices(IServiceCollection services) =>
            services.AddScoped<IDashboardService, ThrowingDashboardService>();
    }

    private sealed class ThrowingDashboardService : IDashboardService
    {
        public Task<DashboardOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(SecretExceptionDetail);
    }
}
