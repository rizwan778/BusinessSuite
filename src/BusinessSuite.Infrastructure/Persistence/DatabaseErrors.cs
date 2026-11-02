using BusinessSuite.Application.Common.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Infrastructure.Persistence;

/// <summary>
/// Turns the database races services expect (a unique index hit by a concurrent request, a row changed or removed under us, a
/// delete blocked by a reference) into <see cref="Error"/> values, so they become 409s instead of 500s. Anything else is
/// unexpected and is left to propagate to the API exception handler (logged, 500).
/// </summary>
internal static class DatabaseErrors
{
    // SQL Server: unique index / unique constraint violated; foreign key or check constraint violated.
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private const int ConstraintViolation = 547;

    /// <summary>Whether the exception is a unique-index or unique-constraint violation.</summary>
    public static bool IsUniqueViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation } };

    /// <summary>Whether the exception is a foreign key or check violation (on a DELETE it can only be a reference).</summary>
    public static bool IsConstraintViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: SqlException { Number: ConstraintViolation } };

    /// <summary>Whether a concurrent request or the row version made the save fail (a race, not a bug).</summary>
    public static bool IsRace(Exception exception) => exception is DbUpdateConcurrencyException || IsUniqueViolation(exception);

    /// <summary>
    /// Saves the context. A concurrently created duplicate becomes <paramref name="onDuplicate"/>, a row changed or removed
    /// under us <paramref name="onConcurrentChange"/>; both are logged as warnings (ids only, never names).
    /// </summary>
    /// <returns><see langword="null"/> on success, otherwise the error to return.</returns>
    public static async Task<Error?> TrySaveAsync(
        AppDbContext db, ILogger logger, string what, Guid? userId, Error onDuplicate, Error onConcurrentChange, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Change of {What} by user {ActorId} lost a race with a concurrent change.", what, userId);
            return onConcurrentChange;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            logger.LogWarning("Change of {What} by user {ActorId} rejected: a concurrent request created the same unique value.", what, userId);
            return onDuplicate;
        }
    }

    /// <summary>
    /// Saves a deletion. A reference from another table (the database keeps every foreign key) becomes <paramref name="inUse"/>
    /// instead of a 500; the database, not a pre-check, is the authority on whether anything still refers to the row.
    /// </summary>
    public static async Task<Error?> TryDeleteAsync(
        AppDbContext db, ILogger logger, string what, Guid id, Guid? userId, Error inUse, Error onConcurrentChange, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception) when (IsConstraintViolation(exception))
        {
            logger.LogInformation("User {ActorId} could not delete {What} {Id}: other records still refer to it.", userId, what, id);
            return inUse;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Deletion of {What} {Id} by user {ActorId} lost a race with a concurrent change.", what, id, userId);
            return onConcurrentChange;
        }
    }

    /// <summary>A search term trimmed, or <see langword="null"/> when blank.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
