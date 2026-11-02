namespace BusinessSuite.Web.Models.Shared;

/// <summary>
/// Content of the friendly error page. Never contains exception messages or stack traces:
/// those are written to the server log, linked by <see cref="CorrelationId"/>.
/// </summary>
public sealed class ErrorViewModel
{
    public required int StatusCode { get; init; }

    public required string Title { get; init; }

    public required string Message { get; init; }

    /// <summary>Bootstrap Icons name.</summary>
    public required string Icon { get; init; }

    /// <summary>Reference the user can quote to support; matches the server log entries.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Builds the page content for an HTTP status code.</summary>
    public static ErrorViewModel ForStatusCode(int statusCode, string? correlationId) => statusCode switch
    {
        400 => Create(400, "Bad request",
            "The request could not be processed. If you submitted a form, reload the page and try again.",
            "exclamation-octagon", correlationId),
        403 => Create(403, "Access denied",
            "You do not have permission to open this page.",
            "shield-lock", correlationId),
        404 => Create(404, "Page not found",
            "The page you are looking for does not exist or has been moved.",
            "signpost-split", correlationId),
        405 => Create(405, "Not allowed",
            "This action cannot be performed this way.",
            "slash-circle", correlationId),
        429 => Create(429, "Too many attempts",
            "You have made too many requests in a short time. Please wait a minute and try again.",
            "hourglass-split", correlationId),
        >= 500 => Create(statusCode, "Something went wrong",
            "An unexpected error occurred and has been logged. Please try again. " +
            "If it keeps happening, contact support and quote the reference below.",
            "bug", correlationId),
        _ => Create(statusCode, "Request failed",
            "The request could not be completed.",
            "exclamation-circle", correlationId),
    };

    private static ErrorViewModel Create(int statusCode, string title, string message, string icon, string? correlationId) =>
        new()
        {
            StatusCode = statusCode,
            Title = title,
            Message = message,
            Icon = icon,
            CorrelationId = correlationId,
        };
}
