using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BusinessSuite.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Fills in <see cref="IAuditable"/> stamps (created/updated, at/by) just before EF Core saves.
/// </summary>
/// <remarks>
/// <para>
/// Runs automatically for every <c>SaveChanges</c>/<c>SaveChangesAsync</c> call, so no
/// application service has to remember to set these values, and nobody can forge them:
/// on update, the original "created" values are protected from modification.
/// </para>
/// <para>
/// Registered as a scoped service because it depends on the current user.
/// </para>
/// </remarks>
public sealed class AuditableEntityInterceptor(ICurrentUser currentUser, TimeProvider timeProvider)
    : SaveChangesInterceptor
{
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyAuditStamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyAuditStamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyAuditStamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var userId = currentUser.UserId;

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(nameof(IAuditable.CreatedAtUtc)).CurrentValue = now;
                    entry.Property(nameof(IAuditable.CreatedByUserId)).CurrentValue = userId;
                    break;

                case EntityState.Modified:
                    entry.Property(nameof(IAuditable.UpdatedAtUtc)).CurrentValue = now;
                    entry.Property(nameof(IAuditable.UpdatedByUserId)).CurrentValue = userId;

                    // Never let an update overwrite who/when created the row.
                    entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedByUserId)).IsModified = false;
                    break;
            }
        }
    }
}
