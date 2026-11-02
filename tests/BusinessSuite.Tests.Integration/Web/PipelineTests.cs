using System.Net;
using BusinessSuite.Tests.Integration.Support;
using BusinessSuite.Web.Framework.Diagnostics;

namespace BusinessSuite.Tests.Integration.Web;

/// <summary>
/// Cross-cutting HTTP behaviour: health checks, security headers, correlation ids, static files.
/// No database is needed.
/// </summary>
public sealed class PipelineTests(BusinessSuiteWebApplicationFactory factory)
    : IClassFixture<BusinessSuiteWebApplicationFactory>
{
    [Fact]
    public async Task Liveness_endpoint_is_public_and_healthy()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri(HealthEndpointExtensions.LivePath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/health/live")]
    public async Task Security_headers_are_present(string path)
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.True(response.Headers.Contains("Content-Security-Policy"), "Content-Security-Policy header missing");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.True(response.Headers.Contains("Referrer-Policy"), "Referrer-Policy header missing");
    }

    [Fact]
    public async Task A_valid_incoming_correlation_id_is_echoed()
    {
        using var client = factory.CreateBrowserClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(HealthEndpointExtensions.LivePath, UriKind.Relative));
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "test-correlation-123");

        using var response = await client.SendAsync(request);

        Assert.Equal("test-correlation-123", response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task An_unsafe_incoming_correlation_id_is_replaced()
    {
        using var client = factory.CreateBrowserClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(HealthEndpointExtensions.LivePath, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, "<script>alert(1)</script>");

        using var response = await client.SendAsync(request);

        var returned = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        Assert.NotEqual("<script>alert(1)</script>", returned);
        Assert.True(CorrelationIdMiddleware.IsValid(returned));
    }

    [Fact]
    public async Task Static_files_are_served_without_signing_in()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri("/css/site.css", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Compressed_static_files_are_served_to_browsers()
    {
        // Browsers send Accept-Encoding; the app must return a real compressed file with a content type.
        using var client = factory.CreateBrowserClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/css/site.css", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0, "Compressed static file was empty.");
    }

    [Fact]
    public async Task Developer_notes_in_wwwroot_are_not_served()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri("/lib/README.md", UriKind.Relative));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
