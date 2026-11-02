using BusinessSuite.Web.Framework.Diagnostics;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Models.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// Friendly error pages. Reached through re-execution:
/// <c>UseExceptionHandler("/Error")</c> for unhandled exceptions (production) and
/// <c>UseStatusCodePagesWithReExecute("/Error/{0}")</c> for 400/403/404/429 responses.
/// </summary>
/// <remarks>
/// No HTTP-method attributes: a failed POST is re-executed as a POST and must still render.
/// Anonymous and anti-forgery-exempt so the error page itself can never fail on those checks.
/// The details of the failure are in the server log, linked by the correlation id.
/// </remarks>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorController : AppController
{
    // Unhandled exception (production)
    [Route("Error")]
    public IActionResult Index()
    {
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View("Error", ErrorViewModel.ForStatusCode(StatusCodes.Status500InternalServerError, HttpContext.GetCorrelationId()));
    }

    // Error status codes (404, 403, 429, ...)
    [Route("Error/{statusCode:int}")]
    public IActionResult Status(int statusCode)
    {
        var code = statusCode is >= 400 and <= 599 ? statusCode : StatusCodes.Status404NotFound;
        Response.StatusCode = code;
        return View("Error", ErrorViewModel.ForStatusCode(code, HttpContext.GetCorrelationId()));
    }
}
