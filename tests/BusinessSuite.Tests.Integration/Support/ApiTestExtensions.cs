using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>Helpers for calling the JSON API (<c>/api/...</c>).</summary>
public static class ApiTestExtensions
{
    public const string ProblemContentType = "application/problem+json";

    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string path, object body)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body);
    }

    /// <summary>GET with <c>Authorization: Bearer &lt;accessToken&gt;</c>.</summary>
    public static async Task<HttpResponseMessage> GetWithTokenAsync(this HttpClient client, string path, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    /// <summary>POST without a body, with <c>Authorization: Bearer &lt;accessToken&gt;</c>.</summary>
    public static async Task<HttpResponseMessage> PostWithTokenAsync(this HttpClient client, string path, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request);
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Read as a string (cached by HttpContent) so the body can be read more than once, e.g. code, then errors.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Asserts the response is RFC 9457 problem details and returns its <c>code</c>.</summary>
    public static async Task<string?> ReadProblemCodeAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Assert.Equal(ProblemContentType, response.Content.Headers.ContentType?.MediaType);

        var problem = await response.ReadJsonAsync();
        Assert.Equal((int)response.StatusCode, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("correlationId").GetString()), "correlationId missing");

        return problem.GetProperty("code").GetString();
    }
}
