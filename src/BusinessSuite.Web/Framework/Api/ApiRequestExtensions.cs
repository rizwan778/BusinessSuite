namespace BusinessSuite.Web.Framework.Api;

/// <summary>Tells JSON API requests apart from browser page requests.</summary>
public static class ApiRequestExtensions
{
    /// <summary>Every JSON API route starts with this path segment, e.g. <c>/api/auth/login</c>.</summary>
    public const string PathPrefix = "/api";

    /// <summary>
    /// <see langword="true"/> for <c>/api/...</c> requests. They get JSON problem details instead of
    /// HTML error pages, and 401/403 instead of a redirect to the sign-in page.
    /// </summary>
    public static bool IsApiRequest(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
