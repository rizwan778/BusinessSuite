namespace BusinessSuite.Application.Common.Results;

/// <summary>
/// Describes why an operation failed, in a form the UI can display and tests can assert on.
/// </summary>
/// <param name="Code">
/// Stable, machine-readable identifier such as <c>"Product.DuplicateSku"</c>.
/// Do not rename a code once it is released: logs, tests and API clients rely on it.
/// </param>
/// <param name="Description">Human-readable message that is safe to show to the user.</param>
/// <param name="Type">Category used to choose the response (see <see cref="ErrorType"/>).</param>
public sealed record Error(string Code, string Description, ErrorType Type = ErrorType.Failure)
{
    /// <summary>Represents "no error". Used internally by successful results.</summary>
    public static readonly Error None = new(string.Empty, string.Empty);

    /// <summary>Input or business-rule failure.</summary>
    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    /// <summary>The requested record does not exist or is not visible to the current user.</summary>
    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    /// <summary>The change conflicts with existing data (duplicate, concurrency).</summary>
    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    /// <summary>The user is not allowed to perform the operation.</summary>
    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    /// <summary>General failure.</summary>
    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);
}
