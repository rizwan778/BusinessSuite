using BusinessSuite.Application.Common.Abstractions;

namespace BusinessSuite.Web.Framework.Diagnostics;

/// <summary>
/// <see cref="IRequestInfo"/> for web requests: the correlation id set by <see cref="CorrelationIdMiddleware"/> and the
/// remote IP address of the connection (behind a proxy, configure forwarded headers so this is the client's address).
/// </summary>
internal sealed class HttpRequestInfo(IHttpContextAccessor httpContextAccessor) : IRequestInfo
{
    /// <inheritdoc />
    public string? CorrelationId => httpContextAccessor.HttpContext?.GetCorrelationId();

    /// <inheritdoc />
    public string? IpAddress => httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}
