namespace BusinessSuite.Domain.Common;

/// <summary>
/// Thrown when an entity would be put into an invalid state, for example
/// "stock quantity cannot become negative".
/// </summary>
/// <remarks>
/// <para>
/// Validate user input in the Application layer first and return a <c>Result</c> failure
/// with a friendly message. A <see cref="DomainException"/> is the last line of defence:
/// if one reaches the user it signals a bug or bad data, and it is logged as an error.
/// </para>
/// <para>
/// For simple argument checks prefer the built-in helpers, e.g.
/// <c>ArgumentException.ThrowIfNullOrWhiteSpace(name)</c> or
/// <c>ArgumentOutOfRangeException.ThrowIfNegative(quantity)</c>.
/// </para>
/// </remarks>
public class DomainException : Exception
{
    /// <summary>Creates the exception with a message describing the broken rule.</summary>
    public DomainException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
