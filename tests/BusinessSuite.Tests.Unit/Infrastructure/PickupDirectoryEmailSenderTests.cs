using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Infrastructure.Email;
using BusinessSuite.Tests.Unit.TestDoubles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Tests.Unit.Infrastructure;

/// <summary>The development e-mail sender (<see cref="PickupDirectoryEmailSender"/>).</summary>
public sealed class PickupDirectoryEmailSenderTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"bs-email-{Guid.NewGuid():N}");

    [Fact]
    public async Task Writes_each_email_as_an_eml_file_relative_to_the_content_root()
    {
        var sender = CreateSender(pickupDirectory: "App_Data/emails");

        await sender.SendAsync(new EmailMessage("ali@example.com", "Reset your password", "Code: abc123"));

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_contentRoot, "App_Data", "emails"), "*.eml"));
        var content = await File.ReadAllTextAsync(file);
        Assert.Contains("To: ali@example.com", content, StringComparison.Ordinal);
        Assert.Contains("Subject: Reset your password", content, StringComparison.Ordinal);
        Assert.EndsWith("Code: abc123", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_loudly_when_no_delivery_is_configured()
    {
        var sender = CreateSender(pickupDirectory: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(new EmailMessage("ali@example.com", "Subject", "Body")));
        Assert.Contains("Email:PickupDirectory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_text_never_shows_the_address_or_body()
    {
        var text = new EmailMessage("ali@example.com", "Subject", "Code: secret").ToString();

        Assert.DoesNotContain("ali@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }

    private PickupDirectoryEmailSender CreateSender(string? pickupDirectory) => new(
        Options.Create(new EmailOptions { PickupDirectory = pickupDirectory }),
        new TestHostEnvironment { ContentRootPath = _contentRoot },
        new FixedTimeProvider(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero)),
        NullLogger<PickupDirectoryEmailSender>.Instance);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "BusinessSuite.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
