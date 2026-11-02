using System.Reflection;

namespace BusinessSuite.Web.Framework.Hosting;

/// <summary>
/// Build information shown on the dashboard and useful in support conversations.
/// </summary>
public static class AppInfo
{
    /// <summary>Product version from <c>Directory.Build.props</c> (for example "0.1.0").</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        // The SDK appends "+<git commit>" when building inside a Git repository; keep the readable part.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
