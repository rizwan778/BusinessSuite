namespace BusinessSuite.Infrastructure.Diagnostics;

/// <summary>
/// Tags used to group health checks.
/// </summary>
/// <remarks>
/// <c>/health/live</c> runs no checks (is the process up?).
/// <c>/health/ready</c> runs checks tagged <see cref="Ready"/> (can it serve requests, e.g. reach SQL Server?).
/// </remarks>
public static class HealthCheckTags
{
    /// <summary>Checks that must pass before the app receives traffic.</summary>
    public const string Ready = "ready";
}
