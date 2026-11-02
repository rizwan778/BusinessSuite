using System.Net;
using System.Text;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Tests.Integration.Support;
using BusinessSuite.Web.Framework.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Web;

/// <summary>
/// The JSON API answers every error with problem details (never an HTML page or a redirect),
/// never leaks exception details, and does not accept the browser sign-in session.
/// No database is needed: sign-up uses a fake registration service.
/// </summary>
public sealed class ApiErrorHandlingTests(ApiErrorHandlingTests.FakeRegistrationFactory factory)
    : IClassFixture<ApiErrorHandlingTests.FakeRegistrationFactory>
{
    [Fact]
    public async Task Protected_endpoint_without_a_token_returns_401_problem_details()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.Unauthorized", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Logout_without_a_token_returns_401_problem_details()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.PostAsync(new Uri("/api/v1/auth/logout", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.Unauthorized", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Logout_with_a_forged_token_returns_401()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.PostWithTokenAsync("/api/v1/auth/logout", "forged-access-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Problem_details_carry_the_request_correlation_id()
    {
        using var client = factory.CreateBrowserClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/auth/me", UriKind.Relative));
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "api-test-123");

        using var response = await client.SendAsync(request);
        var problem = await response.ReadJsonAsync();

        Assert.Equal("api-test-123", problem.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Unknown_api_routes_return_401_instead_of_redirecting_to_the_sign_in_page()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("Auth.Unauthorized", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Invalid_input_returns_a_validation_problem_listing_the_fields()
    {
        using var client = factory.CreateBrowserClient();

        // No anti-forgery token is sent: the API must not require one.
        using var response = await client.PostJsonAsync("/api/v1/auth/login", new { email = "not-an-email" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Request.ValidationFailed", await response.ReadProblemCodeAsync());

        var errors = (await response.ReadJsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("Email", out _), "Email error missing");
        Assert.True(errors.TryGetProperty("Password", out _), "Password error missing");
    }

    [Fact]
    public async Task Malformed_json_returns_a_validation_problem()
    {
        using var client = factory.CreateBrowserClient();
        using var content = new StringContent("{ \"email\": ", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/api/v1/auth/login", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Request.ValidationFailed", await response.ReadProblemCodeAsync());

        // Only the real cause (the JSON error, key "$" or "$.field"), not the misleading "The request field is required."
        var keys = (await response.ReadJsonAsync()).GetProperty("errors").EnumerateObject().Select(error => error.Name).ToList();
        Assert.Contains(keys, key => key.StartsWith('$'));
        Assert.DoesNotContain("request", keys);
    }

    [Fact]
    public async Task Json_with_a_trailing_comma_and_comments_is_accepted()
    {
        using var client = factory.CreateBrowserClient();
        const string body = """
            {
              // hand-written body, as typed in Swagger or Postman
              "email": "trailing@example.com",
              "password": "Irrelevant-Here-1!",
              "displayName": "awais",
            }
            """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(new Uri("/api/v1/auth/register", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("awais", (await response.ReadJsonAsync()).GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Old_unversioned_paths_still_work_and_are_marked_deprecated()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.PostJsonAsync("/api/auth/register", new { email = "legacy@example.com", password = "Irrelevant-Here-1!" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Deprecation").Single());
        Assert.Contains("/api/v1/auth/register", response.Headers.GetValues("Link").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forged_refresh_token_is_rejected()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = "forged-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.InvalidRefreshToken", await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Successful_sign_up_returns_201_with_the_new_user()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.PostJsonAsync("/api/v1/auth/register",
            new { email = "new@example.com", password = "Irrelevant-Here-1!", displayName = "New User" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await response.ReadJsonAsync();
        Assert.Equal(FakeRegistrationService.CreatedUserId, user.GetProperty("userId").GetGuid());
        Assert.Equal("new@example.com", user.GetProperty("email").GetString());
        Assert.Equal("New User", user.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Expected_failures_return_their_error_code_and_status()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.PostJsonAsync("/api/v1/auth/register",
            new { email = FakeRegistrationService.DuplicateEmail, password = "Irrelevant-Here-1!" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(AccountErrors.DuplicateEmail.Code, await response.ReadProblemCodeAsync());
    }

    [Fact]
    public async Task Unhandled_exceptions_return_a_generic_500_problem_without_details()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.PostJsonAsync("/api/v1/auth/register",
            new { email = FakeRegistrationService.ThrowingEmail, password = "Irrelevant-Here-1!" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Server.UnexpectedError", await response.ReadProblemCodeAsync());
        Assert.DoesNotContain(FakeRegistrationService.SecretExceptionMessage, body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The real application with sign-up replaced by <see cref="FakeRegistrationService"/>.</summary>
    public sealed class FakeRegistrationFactory : BusinessSuiteWebApplicationFactory
    {
        protected override void ConfigureSettings(IDictionary<string, string?> settings)
        {
            settings["RateLimiting:RegistrationPermitLimit"] = "1000";
        }

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.AddScoped<IUserRegistrationService, FakeRegistrationService>();
        }
    }

    /// <summary>Succeeds, reports a duplicate or throws, depending on the e-mail address.</summary>
    public sealed class FakeRegistrationService : IUserRegistrationService
    {
        public const string DuplicateEmail = "duplicate@example.com";
        public const string ThrowingEmail = "throws@example.com";
        public const string SecretExceptionMessage = "Connection string Server=secret-host leaked";
        public static readonly Guid CreatedUserId = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

        public Task<Result<RegisteredUser>> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken = default) =>
            command.Email switch
            {
                DuplicateEmail => Task.FromResult<Result<RegisteredUser>>(AccountErrors.DuplicateEmail),
                ThrowingEmail => throw new InvalidOperationException(SecretExceptionMessage),
                _ => Task.FromResult<Result<RegisteredUser>>(new RegisteredUser(CreatedUserId, command.Email, command.DisplayName)),
            };
    }
}
