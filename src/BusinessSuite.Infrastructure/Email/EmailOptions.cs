using System.ComponentModel.DataAnnotations;

namespace BusinessSuite.Infrastructure.Email;

/// <summary>E-mail settings, bound from the <c>"Email"</c> section of appsettings.json.</summary>
public sealed class EmailOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Email";

    /// <summary>
    /// DEVELOPMENT delivery: folder (absolute, or relative to the web project) where each e-mail is written as an
    /// <c>.eml</c> file instead of being sent. Leave empty elsewhere: e-mails then fail loudly until a real e-mail
    /// provider is configured (Phase 5), rather than silently disappearing. The files contain reset codes: never
    /// use this on a shared or production server.
    /// </summary>
    public string? PickupDirectory { get; set; }

    /// <summary>Sender address.</summary>
    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = "no-reply@businesssuite.local";
}
