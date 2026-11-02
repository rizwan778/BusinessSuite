using System.Security.Claims;
using BusinessSuite.Application.Common.Security;
using Microsoft.AspNetCore.Authorization;

namespace BusinessSuite.Web.Framework.Security;

/// <summary>
/// Evaluates <see cref="PermissionRequirement"/>, <see cref="AnyPermissionRequirement"/>,
/// <see cref="OrganizationRoleRequirement"/> and <see cref="SuperAdminRequirement"/> against the per-request access claims (<see cref="AccessClaimsTransformation"/>).
/// </summary>
/// <remarks>
/// Super Admins satisfy every requirement. Anonymous users are left unsatisfied without a failure reason, so they get
/// 401 (sign in) rather than 403. Every denial of a signed-in user is logged with the reason.
/// </remarks>
internal sealed class AccessAuthorizationHandler(ILogger<AccessAuthorizationHandler> logger) : IAuthorizationHandler
{
    internal const string PermissionDeniedCode = "Auth.PermissionDenied";
    internal const string RoleRequiredCode = "Auth.OrganizationRoleRequired";
    internal const string SuperAdminRequiredCode = "Auth.SuperAdminRequired";

    public Task HandleAsync(AuthorizationHandlerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        var isSuperAdmin = user.HasClaim(AppClaimTypes.SuperAdmin, "true");

        foreach (var requirement in context.PendingRequirements.ToList())
        {
            switch (requirement)
            {
                case PermissionRequirement permission:
                    if (isSuperAdmin || user.HasClaim(AppClaimTypes.Permission, permission.Permission))
                    {
                        context.Succeed(requirement);
                    }
                    else
                    {
                        Deny(context, user, PermissionDeniedCode, $"You need the '{permission.Permission}' permission{InOrganization(user, permission.Permission)}.");
                    }

                    break;

                case AnyPermissionRequirement anyPermission:
                    if (isSuperAdmin || anyPermission.AnyOf.Any(permission => user.HasClaim(AppClaimTypes.Permission, permission)))
                    {
                        context.Succeed(requirement);
                    }
                    else
                    {
                        var names = string.Join(", ", anyPermission.AnyOf.Select(permission => $"'{permission}'"));
                        Deny(context, user, PermissionDeniedCode, $"You need one of these permissions{InOrganization(user, anyPermission.AnyOf.ToArray())}: {names}.");
                    }

                    break;

                case OrganizationRoleRequirement role:
                    var current = user.FindFirstValue(AppClaimTypes.OrganizationRole);
                    if (isSuperAdmin || (current is not null && role.Roles.Contains(current, StringComparer.Ordinal)))
                    {
                        context.Succeed(requirement);
                    }
                    else
                    {
                        Deny(context, user, RoleRequiredCode, $"You need one of these organization roles{InOrganization(user)}: {string.Join(", ", role.Roles)}.");
                    }

                    break;

                case SuperAdminRequirement:
                    if (isSuperAdmin)
                    {
                        context.Succeed(requirement);
                    }
                    else
                    {
                        Deny(context, user, SuperAdminRequiredCode, "Only a Super Admin can do this.");
                    }

                    break;
            }
        }

        return Task.CompletedTask;
    }

    private void Deny(AuthorizationHandlerContext context, ClaimsPrincipal user, string code, string message)
    {
        logger.LogInformation(
            "Authorization denied for user {UserId} in organization {OrganizationId} ({Code}): {Reason}",
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            user.FindFirstValue(AppClaimTypes.OrganizationId) ?? "(none)",
            code,
            message);

        context.Fail(new AccessDeniedReason(this, code, message));
    }

    /// <summary>
    /// Where the missing permission is needed. Platform permissions (<c>platform.*</c>) belong to no organization, so
    /// naming one would only mislead.
    /// </summary>
    private static string InOrganization(ClaimsPrincipal user, params string[] permissions)
    {
        if (permissions.Length > 0 && permissions.All(permission => permission.StartsWith(Permissions.PlatformPrefix, StringComparison.Ordinal)))
        {
            return string.Empty;
        }

        return user.HasClaim(claim => claim.Type == AppClaimTypes.OrganizationId)
            ? " in this organization"
            : " (no organization selected: send the X-Organization-Id header)";
    }
}
