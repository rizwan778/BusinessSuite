using BusinessSuite.Application.Common.Security;
using BusinessSuite.Web.Framework.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>
/// Names of authorization policies. Use them with <c>[Authorize(Policy = AuthorizationPolicies.X)]</c>
/// and in <c>NavigationMenu</c> so the menu and the server enforce the same rule.
/// </summary>
/// <remarks>
/// <para>
/// Hiding a menu item is cosmetic. The <c>[Authorize]</c> attribute (or a check in the
/// Application layer) is what actually protects the action.
/// </para>
/// <para>
/// Inside organizations prefer <see cref="RequirePermissionAttribute"/> (permission-based) or
/// <see cref="RequireOrganizationRoleAttribute"/> (role-based); these named policies are for platform roles.
/// </para>
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>Platform operators (the SaaS provider) and Super Admins. Organization admins never qualify.</summary>
    public const string PlatformAdministrator = nameof(PlatformAdministrator);

    /// <summary>Super Admins only.</summary>
    public const string SuperAdministrator = nameof(SuperAdministrator);
}

/// <summary>Registers authorization rules and the permission framework.</summary>
public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Secure by default: every endpoint requires a signed-in user unless it explicitly allows
    /// anonymous access with <c>[AllowAnonymous]</c> (login, error pages, health checks, static files).
    /// </summary>
    public static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(AuthorizationPolicies.PlatformAdministrator, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(PlatformRoles.Administrator, PlatformRoles.SuperAdministrator))
            .AddPolicy(AuthorizationPolicies.SuperAdministrator, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new SuperAdminRequirement()));

        // Organization claims + permissions for every authenticated request (cookie or token), from the database.
        services.AddScoped<CurrentAccessProvider>();
        services.AddScoped<IClaimsTransformation, AccessClaimsTransformation>();

        // [RequirePermission], [RequireOrganizationRole], [RequireSuperAdmin].
        services.AddSingleton<IAuthorizationHandler, AccessAuthorizationHandler>();

        // API: 403 answers explain which permission or role is missing.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();

        return services;
    }
}
