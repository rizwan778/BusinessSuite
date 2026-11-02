using System.Reflection;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Web.Framework.Security;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace BusinessSuite.Tests.Unit.Architecture;

/// <summary>
/// Conventions every MVC controller must follow. These tests make "secure by default" and
/// "thin controllers" visible: breaking them requires a deliberate, reviewed change here.
/// </summary>
public sealed class ControllerConventionTests
{
    /// <summary>
    /// The ONLY actions reachable without signing in. Adding [AllowAnonymous] anywhere else
    /// makes this test fail on purpose: review the security impact, then update this list.
    /// </summary>
    private static readonly string[] ApprovedAnonymousActions =
    [
        "Account.AccessDenied",
        "Account.Lockout",
        "Account.Login",
        "Account.Register",
        "Auth.Login",
        "Auth.Refresh",
        "Auth.Register",
        "Auth.ResetPassword",
        "Error.Index",
        "Error.Status",
    ];

    private static readonly Type[] Controllers = typeof(Program).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(type))
        .ToArray();

    [Fact]
    public void Web_assembly_contains_controllers()
    {
        Assert.NotEmpty(Controllers);
    }

    [Fact]
    public void All_controllers_inherit_AppController_or_AppApiController()
    {
        var violations = Controllers
            .Where(type => !type.IsSubclassOf(typeof(AppController)) && !type.IsSubclassOf(typeof(AppApiController)))
            .Select(type => type.Name)
            .ToList();

        Assert.True(violations.Count == 0, $"Controllers must inherit AppController (pages) or AppApiController (JSON API): {string.Join(", ", violations)}");
    }

    [Fact]
    public void Api_controllers_are_routed_under_the_api_prefix()
    {
        var violations = Controllers
            .Where(type => type.IsSubclassOf(typeof(AppApiController)))
            .Where(type => type.GetCustomAttribute<RouteAttribute>()?.Template.StartsWith("api/", StringComparison.Ordinal) != true)
            .Select(type => type.Name)
            .ToList();

        // Only /api paths get JSON errors and 401 instead of the HTML sign-in redirect (ApiRequestExtensions).
        Assert.True(violations.Count == 0, $"API controllers need [Route(\"api/...\")]: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Controllers_do_not_use_the_DbContext_directly()
    {
        var violations = Controllers
            .Where(type => type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => typeof(AppDbContext).IsAssignableFrom(parameter.ParameterType)))
            .Select(type => type.Name)
            .ToList();

        Assert.True(violations.Count == 0, $"Use an Application service instead of AppDbContext in: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Api_actions_accept_the_sign_in_cookie_only_for_GET()
    {
        // API controllers skip anti-forgery checks; a cookie-authenticated POST/PUT/DELETE would be open to CSRF.
        var violations = Controllers
            .Where(type => type.IsSubclassOf(typeof(AppApiController)))
            .SelectMany(controller => controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Where(method => method.GetCustomAttributes<AuthorizeAttribute>()
                    .Any(authorize => authorize.AuthenticationSchemes?.Contains(AppApiController.CookieScheme, StringComparison.Ordinal) == true))
                .Where(method => method.GetCustomAttributes<HttpMethodAttribute>()
                    .Any(http => http.HttpMethods.Any(verb => verb != HttpMethods.Get)))
                .Select(method => $"{controller.Name}.{method.Name}"))
            .ToList();

        Assert.True(violations.Count == 0, $"Only GET API actions may accept the sign-in cookie: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Required_permissions_and_roles_exist()
    {
        var methods = Controllers.SelectMany(controller => controller.GetMethods().Cast<MemberInfo>().Append(controller)).ToList();

        var unknownPermissions = methods
            .SelectMany(member => member.GetCustomAttributes<RequirePermissionAttribute>().Select(attribute => attribute.Permission)
                .Concat(member.GetCustomAttributes<RequireAnyPermissionAttribute>().SelectMany(attribute => attribute.Permissions)))
            .Where(permission => !Permissions.IsDefined(permission))
            .ToList();
        var unknownRoles = methods
            .SelectMany(member => member.GetCustomAttributes<RequireOrganizationRoleAttribute>())
            .SelectMany(attribute => attribute.Roles)
            .Where(role => !OrganizationRoles.IsValid(role))
            .ToList();

        // A typo would make an endpoint impossible to reach for everyone except Super Admins.
        Assert.True(unknownPermissions.Count == 0, $"Unknown permissions in [RequirePermission] or [RequireAnyPermission]:{string.Join(", ", unknownPermissions)}");
        Assert.True(unknownRoles.Count == 0, $"Unknown roles in [RequireOrganizationRole]: {string.Join(", ", unknownRoles)}");
    }

    [Fact]
    public void Only_approved_actions_allow_anonymous_access()
    {
        var anonymousActions = Controllers
            .SelectMany(controller => controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() is null)
                .Where(method => controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                    || method.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
                .Select(method => $"{controller.Name.Replace("Controller", string.Empty, StringComparison.Ordinal)}.{method.Name}"))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ApprovedAnonymousActions, anonymousActions);
    }
}
