using BusinessSuite.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>Server-side record of API sign-ins, so that tokens can expire and be revoked (see <see cref="UserSession"/>).</summary>
public interface IUserSessionStore
{
    /// <summary>
    /// Creates a session for a successful login and removes the user's expired sessions.
    /// <paramref name="expiresAtUtc"/> is capped at <paramref name="absoluteExpiresAtUtc"/>.
    /// </summary>
    Task<Guid> StartAsync(Guid userId, DateTimeOffset expiresAtUtc, DateTimeOffset absoluteExpiresAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see langword="true"/> when the session belongs to the user, is not revoked or expired, the user is still
    /// active, and <paramref name="securityStamp"/> is the user's current one (it changes on password change,
    /// "sign out everywhere" and deactivation).
    /// </summary>
    Task<bool> IsActiveAsync(Guid sessionId, Guid userId, string? securityStamp, CancellationToken cancellationToken = default);

    /// <summary>Moves an ACTIVE session's expiry forward (token refresh), never past its absolute expiry. <see langword="false"/> if no longer active.</summary>
    Task<bool> ExtendAsync(Guid sessionId, Guid userId, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Revokes one session (logout). <see langword="false"/> if it did not exist or was already revoked.</summary>
    Task<bool> RevokeAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Revokes every active session of the user ("sign out everywhere"). Returns how many were revoked.</summary>
    Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="IUserSessionStore"/> on SQL Server. Updates are single conditional statements
/// (<c>UPDATE ... WHERE RevokedAtUtc IS NULL</c>), so a refresh racing a logout can never revive a session.
/// </summary>
internal sealed class UserSessionStore(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<UserSessionStore> logger) : IUserSessionStore
{
    public async Task<Guid> StartAsync(Guid userId, DateTimeOffset expiresAtUtc, DateTimeOffset absoluteExpiresAtUtc, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var purged = await db.UserSessions
            .Where(session => session.UserId == userId && session.ExpiresAtUtc <= now)
            .ExecuteDeleteAsync(cancellationToken);

        var created = UserSession.Start(userId, now, expiresAtUtc, absoluteExpiresAtUtc);
        db.UserSessions.Add(created);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "API session {SessionId} started for user {UserId} (ends {ExpiresAtUtc:u} unless refreshed, at the latest {AbsoluteExpiresAtUtc:u}); {PurgedCount} expired session(s) removed.",
            created.Id, userId, created.ExpiresAtUtc, created.AbsoluteExpiresAtUtc, purged);

        return created.Id;
    }

    public Task<bool> IsActiveAsync(Guid sessionId, Guid userId, string? securityStamp, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        return (
                from session in db.UserSessions
                join user in db.Users on session.UserId equals user.Id
                where session.Id == sessionId
                    && session.UserId == userId
                    && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > now
                    && user.IsActive
                    && user.SecurityStamp == securityStamp
                select session.Id)
            .AnyAsync(cancellationToken);
    }

    public async Task<bool> ExtendAsync(Guid sessionId, Guid userId, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var updated = await db.UserSessions
            .Where(session => session.Id == sessionId
                && session.UserId == userId
                && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        session => session.ExpiresAtUtc,
                        session => session.AbsoluteExpiresAtUtc < expiresAtUtc ? session.AbsoluteExpiresAtUtc : expiresAtUtc)
                    .SetProperty(session => session.LastRefreshedAtUtc, now),
                cancellationToken);

        return updated == 1;
    }

    public async Task<bool> RevokeAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        var revoked = await db.UserSessions
            .Where(session => session.Id == sessionId && session.UserId == userId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAtUtc, now), cancellationToken);

        return revoked == 1;
    }

    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        return await db.UserSessions
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null && session.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.RevokedAtUtc, now), cancellationToken);
    }
}
