using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Tests.Unit.Application.Results;

public sealed class ResultTests
{
    private static readonly Error SampleError = Error.NotFound("Product.NotFound", "The product was not found.");

    [Fact]
    public void Success_has_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_carries_the_error()
    {
        var result = Result.Failure(SampleError);

        Assert.True(result.IsFailure);
        Assert.Equal(SampleError, result.Error);
        Assert.Equal(ErrorType.NotFound, result.Error.Type);
    }

    [Fact]
    public void Failure_without_a_real_error_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
    }

    [Fact]
    public void Successful_value_result_exposes_the_value()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Reading_the_value_of_a_failed_result_throws()
    {
        var result = Result.Failure<int>(SampleError);

        var exception = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("Product.NotFound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_and_errors_convert_implicitly()
    {
        Result<Guid> fromValue = Guid.Empty;
        Result<Guid> fromError = SampleError;
        Result plainFromError = SampleError;

        Assert.True(fromValue.IsSuccess);
        Assert.True(fromError.IsFailure);
        Assert.True(plainFromError.IsFailure);
    }

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Failure)]
    public void Error_factories_set_the_matching_type(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("Code", "Description"),
            ErrorType.NotFound => Error.NotFound("Code", "Description"),
            ErrorType.Conflict => Error.Conflict("Code", "Description"),
            ErrorType.Forbidden => Error.Forbidden("Code", "Description"),
            _ => Error.Failure("Code", "Description"),
        };

        Assert.Equal(type, error.Type);
        Assert.Equal("Code", error.Code);
    }
}
