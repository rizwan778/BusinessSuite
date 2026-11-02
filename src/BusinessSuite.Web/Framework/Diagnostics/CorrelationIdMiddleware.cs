using System.Diagnostics;

namespace BusinessSuite.Web.Framework.Diagnostics;

/// <summary>
/// Gives every request a correlation id, returns it in the <c>X-Correlation-ID</c> response
/// header, and adds it to every log line written while the request runs.
/// </summary>
/// <remarks>
/// <para>
/// When a user reports "I got an error", the error page shows this id. Search the logs for it
/// to find exactly what happened, even offline on the server.
/// </para>
/// <para>
/// A caller (another service, a load balancer) may send its own id in the request header.
/// It is accepted only if it is short and contains safe characters, which prevents log
/// injection; otherwise a new id is generated.
/// </para>
/// </remarks>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Request and response header name.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>Key under which the id is stored in <see cref="HttpContext.Items"/>.</summary>
    internal const string ItemKey = "BusinessSuite.CorrelationId";

    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Items[ItemKey] = correlationId;

        // OnStarting runs just before headers are sent, so the header survives error handling
        // (which clears headers before rendering the error page).
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // Readable in the console ("CorrelationId:abc123") and a structured property in JSON logs.
        using (logger.BeginScope("CorrelationId:{CorrelationId}", correlationId))
        {
            await next(context);
        }
    }

    /// <summary>Returns the caller's id if it is safe, otherwise a new one.</summary>
    internal static string ResolveCorrelationId(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        if (IsValid(incoming))
        {
            return incoming;
        }

        return Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>Accepts 1-64 characters from [A-Za-z0-9-_.:].</summary>
    internal static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Helpers for <see cref="CorrelationIdMiddleware"/>.</summary>
public static class CorrelationIdExtensions
{
    /// <summary>Adds the correlation id middleware. Register it first in the pipeline.</summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();

    /// <summary>The current request's correlation id, or <see langword="null"/> outside the middleware.</summary>
    public static string? GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var value) ? value as string : null;
}
