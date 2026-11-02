namespace BusinessSuite.Application.Common.Results;

/// <summary>
/// Category of an <see cref="Error"/>. The Web layer uses it to decide how to respond
/// (re-display the form, show "not found", show "access denied", ...).
/// </summary>
public enum ErrorType
{
    /// <summary>Unexpected or general failure.</summary>
    Failure = 0,

    /// <summary>Input or a business rule was not satisfied. Show the message next to the form.</summary>
    Validation = 1,

    /// <summary>The requested record does not exist (or is not visible to this user).</summary>
    NotFound = 2,

    /// <summary>The change conflicts with existing data, e.g. a duplicate SKU or a concurrent edit.</summary>
    Conflict = 3,

    /// <summary>The user is signed in but is not allowed to perform the operation.</summary>
    Forbidden = 4,
}
