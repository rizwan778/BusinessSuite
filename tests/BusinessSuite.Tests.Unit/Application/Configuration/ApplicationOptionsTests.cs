using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Common.Options;

namespace BusinessSuite.Tests.Unit.Application.Configuration;

public sealed class ApplicationOptionsTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Asia/Karachi")]
    [InlineData("Asia/Riyadh")]
    public void Known_time_zones_are_valid(string timeZone)
    {
        var (isValid, _) = Validate(new ApplicationOptions { DefaultTimeZone = timeZone });

        Assert.True(isValid);
    }

    [Fact]
    public void Unknown_time_zone_fails_validation()
    {
        var (isValid, results) = Validate(new ApplicationOptions { DefaultTimeZone = "Mars/Olympus_Mons" });

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ApplicationOptions.DefaultTimeZone)));
    }

    [Fact]
    public void Product_name_is_required()
    {
        var (isValid, results) = Validate(new ApplicationOptions { ProductName = string.Empty });

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ApplicationOptions.ProductName)));
    }

    private static (bool IsValid, List<ValidationResult> Results) Validate(ApplicationOptions options)
    {
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return (isValid, results);
    }
}
