namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// One API sign-in (one device or client). Created by login, extended by token refresh and
/// revoked by logout. Its id travels inside the access and refresh tokens, so revoking the row
/// immediately invalidates both tokens even though the tokens themselves are stateless.
/// </summary>
/// <remarks>
/// Website (cookie) sign-ins do not create sessions; they end with the cookie and the security stamp.
/// </remarks>
public sealed class UserSession
{
    private UserSession()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The signed-in user (<c>identity.Users.Id</c>).</summary>
    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>When the session ends unless refreshed; moved forward by every refresh, never past <see cref="AbsoluteExpiresAtUtc"/>.</summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    /// <summary>Hard limit set at login: however often it is refreshed, the session ends here and the user signs in again.</summary>
    public DateTimeOffset AbsoluteExpiresAtUtc { get; private set; }

    public DateTimeOffset? LastRefreshedAtUtc { get; private set; }

    /// <summary>Set by logout. A revoked session never becomes active again.</summary>
    public DateTimeOffset? RevokedAtUtc { get; private set; }

    internal static UserSession Start(Guid userId, DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc, DateTimeOffset absoluteExpiresAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        CreatedAtUtc = nowUtc,
        ExpiresAtUtc = expiresAtUtc < absoluteExpiresAtUtc ? expiresAtUtc : absoluteExpiresAtUtc,
        AbsoluteExpiresAtUtc = absoluteExpiresAtUtc,
    };
}
