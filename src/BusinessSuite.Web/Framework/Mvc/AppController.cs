using BusinessSuite.Application.Common.Results;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Framework.Mvc;

/// <summary>
/// Base class for every MVC controller in BusinessSuite.
/// </summary>
/// <remarks>
/// <para>Controllers must stay THIN. A typical action:</para>
/// <list type="number">
/// <item>receives a view model (validated by data annotations),</item>
/// <item>calls ONE Application service method,</item>
/// <item>turns the <see cref="Result"/> into a view, a redirect or an error.</item>
/// </list>
/// <para>
/// No database access, calculations or business rules in controllers: they belong in the
/// Application and Domain layers, where they are unit-tested and reused.
/// </para>
/// <para>
/// Every action requires a signed-in user by default (fallback policy). Mark public actions
/// with <c>[AllowAnonymous]</c> and protect privileged ones with
/// <c>[Authorize(Policy = AuthorizationPolicies.X)]</c>.
/// </para>
/// </remarks>
public abstract class AppController : Controller
{
    /// <summary>Shows a green success alert on the next rendered page.</summary>
    protected void NotifySuccess(string message) => TempData.AddAlert(AlertType.Success, message);

    /// <summary>Shows a blue information alert on the next rendered page.</summary>
    protected void NotifyInfo(string message) => TempData.AddAlert(AlertType.Info, message);

    /// <summary>Shows a yellow warning alert on the next rendered page.</summary>
    protected void NotifyWarning(string message) => TempData.AddAlert(AlertType.Warning, message);

    /// <summary>Shows a red error alert on the next rendered page.</summary>
    protected void NotifyError(string message) => TempData.AddAlert(AlertType.Danger, message);

    /// <summary>
    /// Redirects to <paramref name="returnUrl"/> only if it points inside this application
    /// (prevents "open redirect" attacks); otherwise to the dashboard.
    /// </summary>
    protected IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction("Index", "Dashboard", new { area = string.Empty });
    }

    /// <summary>
    /// Adds a failed <see cref="Result"/>'s message to the form's validation summary.
    /// Use it, then return the same view so the user can correct the input.
    /// </summary>
    protected void AddErrorToModelState(Error error, string key = "")
    {
        ArgumentNullException.ThrowIfNull(error);
        ModelState.AddModelError(key, error.Description);
    }

    /// <summary>
    /// Converts a failure that cannot be fixed by re-displaying a form into a response:
    /// NotFound becomes 404, Forbidden becomes 403 (access denied page), anything else 400.
    /// </summary>
    protected IActionResult ErrorResult(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Type switch
        {
            ErrorType.NotFound => NotFound(),
            ErrorType.Forbidden => Forbid(),
            _ => BadRequest(),
        };
    }
}
