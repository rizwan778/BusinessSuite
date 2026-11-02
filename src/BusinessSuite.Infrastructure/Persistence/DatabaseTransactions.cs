using BusinessSuite.Application.Common.Results;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Infrastructure.Persistence;

/// <summary>All-or-nothing database work for services that change several rows (or several services' rows) at once.</summary>
internal static class DatabaseTransactions
{
    /// <summary>
    /// Runs <paramref name="operation"/> in a transaction that is committed only when it returns a successful
    /// <see cref="Result"/>. A failed result, or an exception, rolls everything back, so a refused or failed request
    /// never leaves half of its changes behind.
    /// </summary>
    /// <remarks>
    /// Retry-safe: with a retrying execution strategy the whole operation may run again, so it starts from a clean change
    /// tracker and must load the entities it changes itself (not receive them from outside).
    /// </remarks>
    public static Task<TResult> RunAsync<TResult>(AppDbContext db, Func<Task<TResult>> operation, CancellationToken cancellationToken)
        where TResult : Result
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(operation);

        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var result = await operation();
            if (result.IsSuccess)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return result;
        });
    }
}
