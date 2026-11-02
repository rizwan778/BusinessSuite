namespace BusinessSuite.Application.Common.Results;

/// <summary>
/// The outcome of an operation that can fail for an expected, business reason.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="Result"/> for EXPECTED failures (validation, not found, duplicate SKU,
/// subscription limit reached). Throw exceptions only for UNEXPECTED failures (database
/// unavailable, programming errors) - those are logged and shown as a generic error page.
/// </para>
/// <example>
/// <code>
/// public async Task&lt;Result&lt;Guid&gt;&gt; CreateProductAsync(CreateProductCommand command, CancellationToken ct)
/// {
///     if (await SkuExistsAsync(command.Sku, ct))
///     {
///         return Error.Conflict("Product.DuplicateSku", "A product with this SKU already exists.");
///     }
///
///     var product = Product.Create(command.Sku, command.Name);
///     // ... save ...
///     return product.Id; // converted to a successful Result&lt;Guid&gt; automatically
/// }
/// </code>
/// </example>
/// </remarks>
public class Result
{
    /// <summary>Creates a result. Use the static factory methods instead of calling this directly.</summary>
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (isSuccess && error != Error.None)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("A failed result must carry an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary><see langword="true"/> when the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary><see langword="true"/> when the operation failed; inspect <see cref="Error"/>.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Why the operation failed, or <see cref="Error.None"/> on success.</summary>
    public Error Error { get; }

    /// <summary>A successful result without a value.</summary>
    public static Result Success() => new(true, Error.None);

    /// <summary>A failed result.</summary>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>A successful result carrying <paramref name="value"/>.</summary>
    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    /// <summary>A failed result for an operation that would have returned a <typeparamref name="TValue"/>.</summary>
    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    /// <summary>Allows <c>return Error.NotFound(...);</c> in methods returning <see cref="Result"/>.</summary>
    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>
/// The outcome of an operation that returns a <typeparamref name="TValue"/> when it succeeds.
/// </summary>
/// <typeparam name="TValue">Type of the value produced on success.</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>
    /// The value produced by a successful operation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The result is a failure. Check <see cref="Result.IsSuccess"/> first.</exception>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Cannot read the value of a failed result (error '{Error.Code}'). Check IsSuccess first.");

    /// <summary>Allows <c>return value;</c> in methods returning <see cref="Result{TValue}"/>.</summary>
    public static implicit operator Result<TValue>(TValue value) => Success(value);

    /// <summary>Allows <c>return Error.NotFound(...);</c> in methods returning <see cref="Result{TValue}"/>.</summary>
    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
