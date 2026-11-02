using BusinessSuite.Web.Controllers;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Diagnostics;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Navigation;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Models.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BusinessSuite.Tests.Unit.Web;

public sealed class WebBuildingBlockTests
{
    [Theory]
    [InlineData("abc-123", true)]
    [InlineData("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", true)]
    [InlineData("", false)]
    [InlineData("contains spaces", false)]
    [InlineData("new\nline", false)]
    [InlineData("<script>", false)]
    public void Correlation_ids_from_callers_are_validated(string value, bool expected)
    {
        Assert.Equal(expected, CorrelationIdMiddleware.IsValid(value));
    }

    [Fact]
    public void Correlation_ids_longer_than_64_characters_are_rejected()
    {
        Assert.False(CorrelationIdMiddleware.IsValid(new string('a', 65)));
    }

    [Fact]
    public void Api_bearer_scheme_constant_matches_Identity()
    {
        Assert.Equal(AppApiController.BearerScheme, IdentityConstants.BearerScheme);
        Assert.Equal(AppApiController.CookieScheme, IdentityConstants.ApplicationScheme);
    }

    [Theory]
    [InlineData("/api/v1/auth/login", true)]
    [InlineData("/API/auth/login", true)]
    [InlineData("/api", true)]
    [InlineData("/apidocs", false)]
    [InlineData("/Account/Login", false)]
    public void Api_requests_are_recognised_by_their_path(string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(expected, context.Request.IsApiRequest());
    }

    [Fact]
    public void Password_hint_lists_every_configured_rule()
    {
        var policy = new PasswordOptions
        {
            RequiredLength = 12,
            RequireUppercase = true,
            RequireLowercase = true,
            RequireDigit = true,
            RequireNonAlphanumeric = true,
        };

        Assert.Equal(
            "At least 12 characters, with an upper-case letter, a lower-case letter, a digit and a symbol.",
            AccountController.DescribePasswordPolicy(policy));
    }

    [Fact]
    public void Password_hint_without_character_rules_mentions_only_the_length()
    {
        var policy = new PasswordOptions
        {
            RequiredLength = 8,
            RequireUppercase = false,
            RequireLowercase = false,
            RequireDigit = false,
            RequireNonAlphanumeric = false,
        };

        Assert.Equal("At least 8 characters.", AccountController.DescribePasswordPolicy(policy));
    }

    [Fact]
    public void Security_headers_are_added()
    {
        var headers = new HeaderDictionary();

        SecurityHeadersMiddleware.ApplyHeaders(headers, "default-src 'self'");

        Assert.Equal("default-src 'self'", headers["Content-Security-Policy"].ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", headers["X-Frame-Options"].ToString());
        Assert.True(headers.ContainsKey("Referrer-Policy"));
        Assert.True(headers.ContainsKey("Permissions-Policy"));
    }

    [Fact]
    public void Security_headers_set_by_an_endpoint_are_not_overwritten()
    {
        var headers = new HeaderDictionary { ["X-Frame-Options"] = "SAMEORIGIN" };

        SecurityHeadersMiddleware.ApplyHeaders(headers, "default-src 'self'");

        Assert.Equal("SAMEORIGIN", headers["X-Frame-Options"].ToString());
    }

    [Theory]
    [InlineData(404, "Page not found")]
    [InlineData(403, "Access denied")]
    [InlineData(429, "Too many attempts")]
    [InlineData(500, "Something went wrong")]
    [InlineData(503, "Something went wrong")]
    public void Error_pages_have_friendly_titles(int statusCode, string expectedTitle)
    {
        var model = ErrorViewModel.ForStatusCode(statusCode, "ref-1");

        Assert.Equal(expectedTitle, model.Title);
        Assert.Equal(statusCode, model.StatusCode);
        Assert.Equal("ref-1", model.CorrelationId);
    }

    [Fact]
    public void Navigation_items_are_complete()
    {
        var items = NavigationMenu.Sections.SelectMany(section => section.Items).ToList();

        Assert.All(items, item => Assert.False(string.IsNullOrWhiteSpace(item.Icon)));
        Assert.All(items.Where(item => item.Status == ModuleStatus.Planned), item => Assert.NotNull(item.PlannedPhase));
        Assert.All(items.Where(item => item.Status == ModuleStatus.Available), item => Assert.NotNull(item.Controller));
        Assert.Equal(items.Count, items.Select(item => item.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Platform_administration_menu_requires_the_platform_policy()
    {
        var platformItem = NavigationMenu.Sections.SelectMany(section => section.Items).Single(item => item.Title == "Platform Admin");

        Assert.Equal(AuthorizationPolicies.PlatformAdministrator, platformItem.Policy);
    }

    [Fact]
    public void Breadcrumbs_keep_their_order()
    {
        var viewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());

        viewData.SetPageTitle("Products");
        viewData.AddBreadcrumb("Inventory", "/inventory");
        viewData.AddBreadcrumb("Products");

        Assert.Equal("Products", viewData.GetPageTitle());
        Assert.Equal(new[] { "Inventory", "Products" }, viewData.GetBreadcrumbs().Select(crumb => crumb.Title));
        Assert.Null(viewData.GetBreadcrumbs()[1].Url);
    }

    [Fact]
    public void Alerts_are_queued_and_taken_once()
    {
        var tempData = new TempDataDictionary(new DefaultHttpContext(), new InMemoryTempDataProvider());

        tempData.AddAlert(AlertType.Success, "Saved.");
        tempData.AddAlert(AlertType.Success, "Synced.");

        Assert.Equal(new[] { "Saved.", "Synced." }, tempData.TakeAlerts(AlertType.Success));
        Assert.Empty(tempData.TakeAlerts(AlertType.Danger));
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
