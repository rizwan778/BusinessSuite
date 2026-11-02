namespace BusinessSuite.Application.Common.Abstractions;

/// <summary>Sends transactional e-mails (password reset, account set-up).</summary>
/// <remarks>
/// Development writes each e-mail to a local folder (<c>Email:PickupDirectory</c>); a real e-mail provider is
/// added for production (see docs/IMPLEMENTATION_ROADMAP.md). Implementations throw when the e-mail cannot be
/// sent, so callers never report success for an e-mail that was not delivered to the provider.
/// </remarks>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>A plain-text e-mail.</summary>
/// <param name="To">Recipient address.</param>
/// <param name="Subject">Subject line.</param>
/// <param name="TextBody">Plain-text body. May contain secrets (reset codes): never log it.</param>
public sealed record EmailMessage(string To, string Subject, string TextBody)
{
    /// <summary>Leaves the address and body out (a record's default <c>ToString()</c> prints every property).</summary>
    public override string ToString() => $"{nameof(EmailMessage)} {{ Subject = {Subject} }}";
}
