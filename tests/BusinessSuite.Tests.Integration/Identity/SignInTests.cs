using System.Net;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Identity;

/// <summary>
/// End-to-end sign-in through the real login form, ASP.NET Core Identity and SQL Server.
/// </summary>
public sealed class SignInTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private const string InvalidSignInMessage = "Invalid e-mail address or password.";

    [SqlServerFact]
    public async Task Valid_credentials_sign_the_user_in()
    {
        var (email, password) = await CreateUserAsync();
        using var client = app.Factory.CreateBrowserClient();

        using var signIn = await client.SignInAsync(email, password);

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/", signIn.RedirectPath());

        var dashboard = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        Assert.Contains("Welcome, Integration Tester", dashboard, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Wrong_password_shows_a_generic_message()
    {
        var (email, _) = await CreateUserAsync();
        using var client = app.Factory.CreateBrowserClient();

        using var response = await client.SignInAsync(email, "Wrong-Password-123!");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(InvalidSignInMessage, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Unknown_email_shows_the_same_generic_message()
    {
        using var client = app.Factory.CreateBrowserClient();

        using var response = await client.SignInAsync($"nobody-{Guid.NewGuid():N}@example.com", "Some-Password-123!");

        Assert.Contains(InvalidSignInMessage, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Deactivated_users_cannot_sign_in()
    {
        var (email, password) = await CreateUserAsync(deactivated: true);
        using var client = app.Factory.CreateBrowserClient();

        using var response = await client.SignInAsync(email, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(InvalidSignInMessage, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Repeated_failures_lock_the_account()
    {
        var (email, password) = await CreateUserAsync();
        using var client = app.Factory.CreateBrowserClient();

        for (var attempt = 0; attempt < SqlServerAppFixture.MaxFailedAccessAttempts; attempt++)
        {
            using var failed = await client.SignInAsync(email, "Wrong-Password-123!");
        }

        // Even the correct password is refused while the account is locked.
        using var response = await client.SignInAsync(email, password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Lockout", response.RedirectPath());
    }

    [SqlServerFact]
    public async Task External_return_urls_are_ignored()
    {
        var (email, password) = await CreateUserAsync();
        using var client = app.Factory.CreateBrowserClient();

        using var response = await client.SignInAsync(email, password, returnUrl: "https://evil.example.com/phish");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.RedirectPath());
        Assert.DoesNotContain("evil.example.com", response.Headers.Location?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [SqlServerFact]
    public async Task Signing_out_ends_the_session()
    {
        var (email, password) = await CreateUserAsync();
        using var client = app.Factory.CreateBrowserClient();
        using var signIn = await client.SignInAsync(email, password);

        var dashboard = await client.GetStringAsync(new Uri("/", UriKind.Relative));
        var token = HttpTestExtensions.ExtractAntiforgeryToken(dashboard);
        using var logoutForm = new FormUrlEncodedContent(new Dictionary<string, string> { [HttpTestExtensions.AntiforgeryFieldName] = token });
        using var logout = await client.PostAsync(new Uri("/Account/Logout", UriKind.Relative), logoutForm);

        using var afterLogout = await client.GetAsync(new Uri("/", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.Equal("/Account/Login", afterLogout.RedirectPath());
    }

    private async Task<(string Email, string Password)> CreateUserAsync(bool deactivated = false)
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var password = $"Pw-{Guid.NewGuid():N}aA1!";

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
