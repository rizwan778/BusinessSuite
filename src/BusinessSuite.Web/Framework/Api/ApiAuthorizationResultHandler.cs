using BusinessSuite.Web.Framework.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Framework.Api;

/// <summary>
/// For <c>/api</c> requests by a signed-in user that authorization refused: answers 403 with problem details
/// carrying the reason and a stable code (e.g. <c>Auth.PermissionDenied</c>) instead of an empty 403.
/// Everything else (401 challenges, page requests) keeps ASP.NET Core's default behaviour.
/// </summary>
internal sealed class ApiAuthorizationResultHandler(IProblemDetailsService problemDetailsService) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (!authorizeResult.Forbidden || !context.Request.IsApiRequest() || context.User.Identity?.IsAuthenticated != true)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var reasons = authorizeResult.AuthorizationFailure?.FailureReasons.OfType<AccessDeniedReason>().ToList() ?? [];

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Detail = reasons.Count > 0
                ? string.Join(" ", reasons.Select(reason => reason.Message).Distinct(StringComparer.Ordinal))
                : "You are not allowed to do this.",
        };
        problem.Extensions[AppApiController.ErrorCodeExtension] = reasons.FirstOrDefault()?.Code ?? "Auth.Forbidden";

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }
}
