using System.Net;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Web;

/// <summary>Sign-in attempts are throttled per client to slow down password guessing.</summary>
public sealed class RateLimitingTests(RateLimitingTests.LowLimitFactory factory)
    : IClassFixture<RateLimitingTests.LowLimitFactory>
{
    [Fact]
    public async Task Too_many_sign_in_posts_are_rejected_with_429()
    {
        using var client = factory.CreateBrowserClient();
        var statusCodes = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < LowLimitFactory.PermitLimit + 1; attempt++)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Email"] = "a@example.com" });
            using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
            statusCodes.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statusCodes[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statusCodes.Take(LowLimitFactory.PermitLimit));
    }

    [Fact]
    public async Task Too_many_api_sign_ups_are_rejected_with_a_429_problem_and_retry_after()
    {
        using var client = factory.CreateBrowserClient();
        var statusCodes = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < LowLimitFactory.PermitLimit; attempt++)
        {
            using var allowed = await client.PostJsonAsync("/api/v1/auth/register", new { email = $"u{attempt}@example.com", password = "Irrelevant-Here-1!" });
            statusCodes.Add(allowed.StatusCode);
        }

        using var response = await client.PostJsonAsync("/api/v1/auth/register", new { email = "last@example.com", password = "Irrelevant-Here-1!" });

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statusCodes);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("Request.RateLimited", await response.ReadProblemCodeAsync());
        Assert.NotNull(response.Headers.RetryAfter);
    }

    /// <summary>
    /// The real application with sign-in and sign-up limits of two requests per minute.
    /// Sign-up uses a fake service, so no database is needed.
    /// </summary>
    public sealed class LowLimitFactory : BusinessSuiteWebApplicationFactory
    {
        public const int PermitLimit = 2;

        protected override void ConfigureSettings(IDictionary<string, string?> settings)
        {
            var limit = PermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["RateLimiting:LoginPermitLimit"] = limit;
            settings["RateLimiting:LoginWindowSeconds"] = "60";
            settings["RateLimiting:RegistrationPermitLimit"] = limit;
            settings["RateLimiting:RegistrationWindowSeconds"] = "60";
        }

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.AddScoped<IUserRegistrationService, ApiErrorHandlingTests.FakeRegistrationService>();
        }
    }
}
