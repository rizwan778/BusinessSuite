using System.Net;
using System.Text.RegularExpressions;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>Helpers for driving MVC pages like a browser.</summary>
public static partial class HttpTestExtensions
{
    public const string AntiforgeryFieldName = "__RequestVerificationToken";

    /// <summary>Reads the anti-forgery token from a rendered form.</summary>
    public static string ExtractAntiforgeryToken(string html)
    {
        var match = AntiforgeryTokenRegex().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("The page does not contain an anti-forgery token. Is the form rendered with <form asp-...>?");
        }

        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    /// <summary>Opens the sign-in page, then posts the credentials with a valid anti-forgery token.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(this HttpClient client, string email, string password, string? returnUrl = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var loginPage = await client.GetAsync(new Uri("/Account/Login", UriKind.Relative));
        loginPage.EnsureSuccessStatusCode();
        var token = ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

        var form = new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["RememberMe"] = "false",
            [AntiforgeryFieldName] = token,
        };

        if (returnUrl is not null)
        {
            form["ReturnUrl"] = returnUrl;
        }

        using var content = new FormUrlEncodedContent(form);
        return await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), content);
    }

    /// <summary>Path of a redirect's Location header, whether absolute or relative.</summary>
    public static string? RedirectPath(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var location = response.Headers.Location;
        if (location is null)
        {
            return null;
        }

        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AntiforgeryTokenRegex();
}
