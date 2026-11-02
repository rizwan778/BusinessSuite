using System.Net;
using System.Text.Json;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Identity;

/// <summary>
/// End-to-end sign-up, sign-in and token renewal through the JSON API, ASP.NET Core Identity and SQL Server.
/// </summary>
public sealed class AuthApiTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string WrongPassword = "Wrong-Password-123!";

    [SqlServerFact]
    public async Task Sign_up_creates_an_account_that_can_sign_in()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = NewCredentials();

        using var register = await client.PostJsonAsync("/api/v1/auth/register", new { email, password, displayName = "Api Tester" });

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var created = await register.ReadJsonAsync();
        Assert.NotEqual(Guid.Empty, created.GetProperty("userId").GetGuid());
        Assert.Equal(email, created.GetProperty("email").GetString());
        Assert.Equal("Api Tester", created.GetProperty("displayName").GetString());

        var tokens = await LoginAsync(client, email, password);
        using var me = await client.GetWithTokenAsync("/api/v1/auth/me", tokens.GetProperty("accessToken").GetString()!);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var user = await me.ReadJsonAsync();
        Assert.Equal(created.GetProperty("userId").GetGuid(), user.GetProperty("userId").GetGuid());
        Assert.Equal("Api Tester", user.GetProperty("displayName").GetString());
    }

    [SqlServerFact]
    public async Task Me_returns_the_user_signed_in_on_the_website()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        using var signIn = await client.SignInAsync(email, password); // sign-in page: sets the auth cookie
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);

        using var me = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var user = await me.ReadJsonAsync();
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal("Integration Tester", user.GetProperty("displayName").GetString());
    }

    [SqlServerFact]
    public async Task Me_after_signing_out_on_the_website_returns_401()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        using var signIn = await client.SignInAsync(email, password);

        var dashboard = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        using var logoutForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [HttpTestExtensions.AntiforgeryFieldName] = HttpTestExtensions.ExtractAntiforgeryToken(dashboard),
        });
        using var logout = await client.PostAsync(new Uri("/Account/Logout", UriKind.Relative), logoutForm);

        using var me = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal("Auth.Unauthorized", await me.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Sign_up_with_an_existing_email_returns_409()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = NewCredentials();
        using var first = await client.PostJsonAsync("/api/v1/auth/register", new { email, password });

        using var second = await client.PostJsonAsync("/api/v1/auth/register", new { email = email.ToUpperInvariant(), password });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(AccountErrors.DuplicateEmail.Code, await second.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Sign_up_with_a_weak_password_lists_the_broken_rules()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, _) = NewCredentials();

        using var response = await client.PostJsonAsync("/api/v1/auth/register", new { email, password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AccountErrors.WeakPasswordCode, await response.ReadProblemCodeAsync());
        Assert.Contains("12", (await response.ReadJsonAsync()).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Login_returns_bearer_and_refresh_tokens()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();

        var tokens = await LoginAsync(client, email, password);

        Assert.Equal("Bearer", tokens.GetProperty("tokenType").GetString());
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(tokens.GetProperty("refreshToken").GetString()));
        Assert.Equal(3600, tokens.GetProperty("expiresIn").GetInt64());
    }

    [SqlServerFact]
    public async Task Wrong_password_unknown_email_and_deactivated_account_get_the_same_401()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, _) = await CreateUserAsync();
        var (deactivatedEmail, deactivatedPassword) = await CreateUserAsync(deactivated: true);

        using var wrongPassword = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = WrongPassword });
        using var unknownEmail = await client.PostJsonAsync("/api/v1/auth/login", new { email = $"nobody-{Guid.NewGuid():N}@example.com", password = WrongPassword });
        using var deactivated = await client.PostJsonAsync("/api/v1/auth/login", new { email = deactivatedEmail, password = deactivatedPassword });

        foreach (var response in new[] { wrongPassword, unknownEmail, deactivated })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Auth.InvalidCredentials", await response.ReadProblemCodeAsync());
        }
    }

    [SqlServerFact]
    public async Task Repeated_failures_lock_the_account()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();

        for (var attempt = 0; attempt < SqlServerAppFixture.MaxFailedAccessAttempts; attempt++)
        {
            using var failed = await client.PostJsonAsync("/api/v1/auth/login", new { email, password = WrongPassword });
        }

        // Even the correct password is refused while the account is locked.
        using var response = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Auth.LockedOut", await response.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Login_does_not_issue_the_browser_sign_in_cookie()
    {
        // Cookie handling off, so any Set-Cookie header stays visible on the response.
        using var client = app.Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });
        var (email, password) = await CreateUserAsync();

        using var response = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"), "API sign-in must not set cookies.");
    }

    [SqlServerFact]
    public async Task Refresh_token_returns_new_working_tokens()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        var tokens = await LoginAsync(client, email, password);

        using var refresh = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var renewed = await refresh.ReadJsonAsync();
        using var me = await client.GetWithTokenAsync("/api/v1/auth/me", renewed.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [SqlServerFact]
    public async Task Refresh_is_refused_after_the_account_is_deactivated()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        var tokens = await LoginAsync(client, email, password);

        using (var scope = app.Factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            user!.Deactivate();
            await userManager.UpdateAsync(user);
            await userManager.UpdateSecurityStampAsync(user);
        }

        using var refresh = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("Auth.InvalidRefreshToken", await refresh.ReadProblemCodeAsync());
    }

    [SqlServerFact]
    public async Task Login_records_an_active_session_in_the_database()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();

        await LoginAsync(client, email, password);

        var session = await SessionsOfAsync(email);
        var only = Assert.Single(session);
        Assert.Null(only.RevokedAtUtc);
        Assert.True(only.ExpiresAtUtc > DateTimeOffset.UtcNow.AddDays(13), "Session should last as long as the refresh token (14 days).");
    }

    [SqlServerFact]
    public async Task Logout_revokes_the_session_so_both_tokens_stop_working()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        var tokens = await LoginAsync(client, email, password);
        var accessToken = tokens.GetProperty("accessToken").GetString()!;

        using var before = await client.GetWithTokenAsync("/api/v1/auth/me", accessToken);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var logout = await client.PostWithTokenAsync("/api/v1/auth/logout", accessToken);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Access token: rejected immediately, although it has not expired.
        using var after = await client.GetWithTokenAsync("/api/v1/auth/me", accessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal("Auth.Unauthorized", await after.ReadProblemCodeAsync());

        // Refresh token: refused.
        using var refresh = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal("Auth.InvalidRefreshToken", await refresh.ReadProblemCodeAsync());

        // Database: the session row is marked revoked.
        Assert.NotNull(Assert.Single(await SessionsOfAsync(email)).RevokedAtUtc);
    }

    [SqlServerFact]
    public async Task Logout_signs_out_only_the_current_device()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        var phone = (await LoginAsync(client, email, password)).GetProperty("accessToken").GetString()!;
        var laptop = (await LoginAsync(client, email, password)).GetProperty("accessToken").GetString()!;

        using var logout = await client.PostWithTokenAsync("/api/v1/auth/logout", phone);

        using var phoneMe = await client.GetWithTokenAsync("/api/v1/auth/me", phone);
        using var laptopMe = await client.GetWithTokenAsync("/api/v1/auth/me", laptop);
        Assert.Equal(HttpStatusCode.Unauthorized, phoneMe.StatusCode);
        Assert.Equal(HttpStatusCode.OK, laptopMe.StatusCode);
    }

    [SqlServerFact]
    public async Task Logging_out_twice_with_the_same_token_returns_401()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        var accessToken = (await LoginAsync(client, email, password)).GetProperty("accessToken").GetString()!;

        using var first = await client.PostWithTokenAsync("/api/v1/auth/logout", accessToken);
        using var second = await client.PostWithTokenAsync("/api/v1/auth/logout", accessToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [SqlServerFact]
    public async Task Refreshed_tokens_belong_to_the_same_session_and_are_revoked_by_logout()
    {
        using var client = app.Factory.CreateBrowserClient();
        var (email, password) = await CreateUserAsync();
        var tokens = await LoginAsync(client, email, password);
        using var refresh = await client.PostJsonAsync("/api/v1/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() });
        var renewed = (await refresh.ReadJsonAsync()).GetProperty("accessToken").GetString()!;

        using var logout = await client.PostWithTokenAsync("/api/v1/auth/logout", renewed);

        using var original = await client.GetWithTokenAsync("/api/v1/auth/me", tokens.GetProperty("accessToken").GetString()!);
        Assert.Equal(HttpStatusCode.Unauthorized, original.StatusCode);
        Assert.NotNull(Assert.Single(await SessionsOfAsync(email)).LastRefreshedAtUtc);
    }

    private async Task<List<UserSession>> SessionsOfAsync(string email)
    {
        await using var db = app.Database.CreateDbContext();
        var userId = await db.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
        return await db.UserSessions.AsNoTracking().Where(session => session.UserId == userId).ToListAsync();
    }

    private static (string Email, string Password) NewCredentials() =>
        ($"api-{Guid.NewGuid():N}@example.com", $"Pw-{Guid.NewGuid():N}aA1!");

    private static async Task<JsonElement> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private async Task<(string Email, string Password)> CreateUserAsync(bool deactivated = false)
    {
        var (email, password) = NewCredentials();

        using var scope = app.Factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "Integration Tester",
        };

        if (deactivated)
        {
            user.Deactivate();
        }

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(error => error.Description)));

        return (email, password);
    }
}
