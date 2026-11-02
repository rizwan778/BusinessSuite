using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BusinessSuite.Application.Common.Abstractions;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>
/// <see cref="IEmailSender"/> for tests: keeps every e-mail in memory (registered by
/// <see cref="BusinessSuiteWebApplicationFactory"/>), so tests can read reset codes and nothing is written to disk.
/// </summary>
public sealed partial class TestEmailOutbox : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>E-mails sent to <paramref name="address"/>, oldest first.</summary>
    public IReadOnlyList<EmailMessage> SentTo(string address) =>
        _messages.Where(message => string.Equals(message.To, address, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The reset code in the latest e-mail to <paramref name="address"/>.</summary>
    public string LatestCodeFor(string address)
    {
        var message = SentTo(address).LastOrDefault() ?? throw new InvalidOperationException("No e-mail was sent to this address.");
        return CodeLine().Match(message.TextBody).Groups[1].Value;
    }

    [GeneratedRegex(@"Code:\s+(\S+)")]
    private static partial Regex CodeLine();
}
