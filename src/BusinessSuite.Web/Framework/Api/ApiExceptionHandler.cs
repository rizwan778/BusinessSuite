using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Framework.Api;

/// <summary>
/// Last line of defence for <c>/api</c> requests: turns any unhandled exception into a logged,
/// generic JSON problem details response. Page requests are left to the HTML error page.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Client disconnected (request aborted): logged at Information, status 499, no body.</item>
/// <item>Malformed request (<see cref="BadHttpRequestException"/>, e.g. body too large): Warning, its own 4xx status.</item>
/// <item>Anything else: Error with the full exception, status 500.</item>
/// </list>
/// The response never contains exception details; the client gets the <c>correlationId</c>, which
/// finds the matching log entry. Expected failures (validation, duplicates, wrong password) are
/// <c>Result</c> values handled by the controllers and never reach this class.
/// </remarks>
internal sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Non-standard status (nginx convention) recorded when the client closed the connection.</summary>
    internal const int ClientClosedRequest = 499;

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var request = httpContext.Request;
        if (!request.IsApiRequest())
        {
            return false; // pages: UseExceptionHandler("/Error") renders the HTML error page
        }

        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("API request {Method} {Path} was cancelled by the client.", request.Method, request.Path);
            httpContext.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        ProblemDetails problem;
        if (exception is BadHttpRequestException badRequest)
        {
            logger.LogWarning("API request {Method} {Path} could not be read: {Reason}", request.Method, request.Path, badRequest.Message);
            problem = new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Detail = "The request could not be read. Check its size, headers and JSON body.",
            };
        }
        else
        {
            logger.LogError(exception, "Unhandled exception while processing API request {Method} {Path}.", request.Method, request.Path);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Detail = "The error has been logged. Quote the correlationId when reporting it.",
            };
        }

        httpContext.Response.StatusCode = problem.Status!.Value;

        var written = await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });

        if (!written)
        {
            // No writer accepted the client's Accept header (e.g. "Accept: text/html" only): send the status alone.
            logger.LogDebug("No problem details writer matched the request; sent status {StatusCode} without a body.", problem.Status);
        }

        return true;
    }
}
