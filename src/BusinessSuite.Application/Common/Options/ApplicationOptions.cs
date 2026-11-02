using System.ComponentModel.DataAnnotations;

namespace BusinessSuite.Application.Common.Options;

/// <summary>
/// General product settings, bound from the <c>"Application"</c> section of appsettings.json.
/// </summary>
/// <remarks>
/// Validated when the application starts: an invalid value stops start-up with a clear
/// message instead of failing later. These are platform-wide defaults; each organization
/// gets its own currency, time zone and formats in Phase 2.
/// </remarks>
public sealed class ApplicationOptions : IValidatableObject
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Application";

    /// <summary>Product name shown in the browser title, sidebar and e-mails.</summary>
    [Required]
    [StringLength(80, MinimumLength = 2)]
    public string ProductName { get; set; } = "Business Suite";

    /// <summary>
    /// Time zone used to DISPLAY dates until an organization chooses its own.
    /// Use an IANA id such as <c>"Asia/Karachi"</c> or <c>"UTC"</c>. Data is always stored in UTC.
    /// </summary>
    [Required]
    public string DefaultTimeZone { get; set; } = "UTC";

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(DefaultTimeZone, out _))
        {
            yield return new ValidationResult(
                $"Time zone '{DefaultTimeZone}' was not found on this machine. Use an IANA id such as 'Asia/Karachi' or 'UTC'.",
                [nameof(DefaultTimeZone)]);
        }
    }
}
