namespace BusinessSuite.Tests.Unit.TestDoubles;

/// <summary>A clock frozen at one instant, so time-dependent code gives repeatable results.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
