using BusinessSuite.Application.Common.Results;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Framework.Api;

/// <summary>
/// Base class for every JSON API controller in BusinessSuite (routes under <c>/api</c>).
/// </summary>
/// <remarks>
/// <para>Same rules as <c>AppController</c>: controllers stay THIN and call the Application layer.</para>
/// <list type="bullet">
/// <item><c>[ApiController]</c>: invalid input is answered with 400 and a validation problem
/// (logged, see <see cref="ApiExtensions.AddApi"/>) before the action runs.</item>
/// <item>Every error response is an RFC 9457 problem details document with a stable <c>code</c>
/// and the request's <c>correlationId</c>.</item>
/// <item>No anti-forgery check: API callers authenticate with a bearer token in the
/// <c>Authorization</c> header, which browsers never attach automatically, so CSRF does not apply.
/// Only read-only GET actions may also accept the sign-in cookie (<see cref="BearerOrCookieSchemes"/>).</item>
/// <item>Every action requires a signed-in user by default (fallback policy). Protected API actions
/// must name the bearer scheme: <c>[Authorize(AuthenticationSchemes = BearerScheme)]</c>.</item>
/// </list>
/// </remarks>
[ApiController]
[IgnoreAntiforgeryToken]
public abstract class AppApiController : ControllerBase
{
    /// <summary>
    /// ASP.NET Core Identity's bearer token scheme (<c>IdentityConstants.BearerScheme</c>), as a constant
    /// so it can be used in attributes. A unit test checks the two stay equal.
    /// </summary>
    public const string BearerScheme = "Identity.Bearer";

    /// <summary>
    /// ASP.NET Core Identity's sign-in cookie scheme (<c>IdentityConstants.ApplicationScheme</c>), as a constant.
    /// </summary>
    public const string CookieScheme = "Identity.Application";

    /// <summary>
    /// Bearer token OR the website's sign-in cookie. Use ONLY on read-only GET actions: the API skips
    /// anti-forgery checks, so an action that changes data must never accept the cookie (CSRF).
    /// </summary>
    public const string BearerOrCookieSchemes = BearerScheme + "," + CookieScheme;

    /// <summary>Content type of every error response (RFC 9457 problem details).</summary>
    public const string ProblemContentType = "application/problem+json";

    /// <summary>Name of the problem details extension that carries <see cref="Error.Code"/>.</summary>
    public const string ErrorCodeExtension = "code";

    /// <summary>
    /// Converts a failed <see cref="Result"/> into a problem details response:
    /// Validation 400, NotFound 404, Conflict 409, Forbidden 403, anything else 400.
    /// </summary>
    protected ObjectResult ErrorResult(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var statusCode = error.Type switch
        {
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest,
        };

        return ProblemResult(statusCode, error.Code, error.Description);
    }

    /// <summary>A problem details response with a stable error <paramref name="code"/> and a user-safe <paramref name="detail"/>.</summary>
    protected ObjectResult ProblemResult(int statusCode, string code, string detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: statusCode, detail: detail);
        problem.Extensions[ErrorCodeExtension] = code;

        return new ObjectResult(problem)
        {
            StatusCode = statusCode,
            ContentTypes = { ProblemContentType },
        };
    }
}
