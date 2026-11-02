namespace BusinessSuite.Application.Common.Abstractions;

/// <summary>
/// Where the current request comes from, for the audit trail. The Web layer implements it from <c>HttpContext</c>;
/// outside a request (background jobs) both values are <see langword="null"/>.
/// </summary>
public interface IRequestInfo
{
    /// <summary>The request's correlation id (<c>X-Correlation-ID</c>), or <see langword="null"/>.</summary>
    string? CorrelationId { get; }

    /// <summary>The caller's IP address as seen by the server, or <see langword="null"/>.</summary>
    string? IpAddress { get; }
}
