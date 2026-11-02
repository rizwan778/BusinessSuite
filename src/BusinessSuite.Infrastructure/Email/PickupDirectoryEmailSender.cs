using System.Globalization;
using System.Text;
using BusinessSuite.Application.Common.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Infrastructure.Email;

/// <summary>
/// <see cref="IEmailSender"/> for development: writes each e-mail to <see cref="EmailOptions.PickupDirectory"/>
/// as an <c>.eml</c> file (opens in any mail client). Throws when no folder is configured, so a missing e-mail
/// provider is noticed instead of e-mails silently disappearing.
/// </summary>
internal sealed class PickupDirectoryEmailSender(
    IOptions<EmailOptions> options,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<PickupDirectoryEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.PickupDirectory))
        {
            throw new InvalidOperationException(
                $"No e-mail delivery is configured: set {EmailOptions.SectionName}:{nameof(EmailOptions.PickupDirectory)} " +
                "(development) or configure an e-mail provider. See docs/CONFIGURATION.md.");
        }

        var directory = Path.GetFullPath(Path.Combine(environment.ContentRootPath, settings.PickupDirectory));
        Directory.CreateDirectory(directory);

        var now = timeProvider.GetUtcNow();
        var fileName = $"{now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.eml";
        var content = new StringBuilder()
            .Append("From: ").Append(settings.FromAddress).Append("\r\n")
            .Append("To: ").Append(message.To).Append("\r\n")
            .Append("Subject: ").Append(message.Subject).Append("\r\n")
            .Append("Date: ").Append(now.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Content-Type: text/plain; charset=utf-8\r\n")
            .Append("\r\n")
            .Append(message.TextBody)
            .ToString();

        await File.WriteAllTextAsync(Path.Combine(directory, fileName), content, Encoding.UTF8, cancellationToken);

        // No address or body: the body contains a reset code.
        logger.LogInformation("E-mail \"{Subject}\" written to the pickup directory as {FileName}.", message.Subject, fileName);
    }
}
