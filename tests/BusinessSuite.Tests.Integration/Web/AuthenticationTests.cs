using System.Net;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Tests.Integration.Support;
using BusinessSuite.Web.Framework.Hosting;

namespace BusinessSuite.Tests.Integration.Web;

/// <summary>
/// Secure-by-default behaviour of the web layer. No database is needed: signed-in requests use
/// the test authentication handler.
/// </summary>
public sealed class AuthenticationTests(BusinessSuiteWebApplicationFactory factory)
    : IClassFixture<BusinessSuiteWebApplicationFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/Dashboard")]
    [InlineData("/page-that-does-not-exist")]
    public async Task Anonymous_users_are_sent_to_the_sign_in_page(string path)
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.RedirectPath());
    }

    [Fact]
    public async Task Sign_in_page_renders_a_form_with_an_antiforgery_token()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in", html, StringComparison.Ordinal);
        Assert.NotEmpty(HttpTestExtensions.ExtractAntiforgeryToken(html));
    }

    [Fact]
    public async Task Posting_the_sign_in_form_without_an_antiforgery_token_is_rejected()
    {
        using var client = factory.CreateBrowserClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = "someone@example.com",
            ["Password"] = "irrelevant",
        });

        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_page_links_to_the_sign_up_page()
    {
        using var client = factory.CreateBrowserClient();

        var html = await client.GetStringAsync(new Uri("/Account/Login", UriKind.Relative));

        Assert.Contains("href=\"/Account/Register\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_up_page_is_public_and_posts_to_the_register_api()
    {
        using var client = factory.CreateBrowserClient();

        using var response = await client.GetAsync(new Uri("/Account/Register", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-register-api=\"/api/v1/auth/register\"", html, StringComparison.Ordinal);
        Assert.Contains("data-login-url=\"/Account/Login?signedUp=True\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=\"Email\"", html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("At least 12 characters", html, StringComparison.Ordinal);
        Assert.Contains("/js/register.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_page_confirms_a_completed_sign_up()
    {
        using var client = factory.CreateBrowserClient();

        var html = await client.GetStringAsync(new Uri("/Account/Login?signedUp=true", UriKind.Relative));

        Assert.Contains("Your account has been created. Sign in to continue.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_in_users_visiting_sign_up_are_redirected_to_the_dashboard()
    {
        using var client = factory.CreateSignedInClient();

        using var response = await client.GetAsync(new Uri("/Account/Register", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.RedirectPath());
    }

    [Fact]
    public async Task Signed_in_users_see_the_dashboard()
    {
        using var client = factory.CreateSignedInClient(displayName: "Test User");

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Welcome, Test User", html, StringComparison.Ordinal);
        Assert.Contains($"v{AppInfo.Version}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@Model", html, StringComparison.Ordinal); // Razor expression printed as text
    }

    [Fact]
    public async Task Signed_in_users_visiting_sign_in_are_redirected_to_the_dashboard()
    {
        using var client = factory.CreateSignedInClient();

        using var response = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.RedirectPath());
    }

    [Fact]
    public async Task Platform_menu_is_hidden_from_ordinary_users()
    {
        using var client = factory.CreateSignedInClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.DoesNotContain("Platform Admin", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Platform_menu_is_shown_to_platform_administrators()
    {
        using var client = factory.CreateSignedInClient("Admin", PlatformRoles.Administrator);

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.Contains("Platform Admin", html, StringComparison.Ordinal);
    }
}
