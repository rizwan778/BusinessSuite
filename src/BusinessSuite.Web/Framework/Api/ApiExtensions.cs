using BusinessSuite.Web.Framework.Diagnostics;
using BusinessSuite.Web.Framework.Security;
using Microsoft.AspNetCore.Authentication.BearerToken;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Web.Framework.Api;

/// <summary>
/// Registers the JSON API infrastructure: bearer tokens, problem details, exception handling
/// and validation logging. Called from <c>AddWebPresentation</c> and <c>Program.cs</c>.
/// </summary>
public static class ApiExtensions
{
    /// <summary>
    /// Adds ASP.NET Core Identity bearer tokens (scheme <see cref="AppApiController.BearerScheme"/>),
    /// RFC 9457 problem details for every API error, <see cref="ApiExceptionHandler"/> and logging of
    /// rejected input.
    /// </summary>
    /// <remarks>
    /// Tokens are opaque and encrypted with the application's data-protection keys (the same keys as
    /// the sign-in cookie), so no signing secret has to be managed. Only this application can read them.
    /// </remarks>
    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication().AddBearerToken(AppApiController.BearerScheme);
        services.AddOptions<BearerTokenOptions>(AppApiController.BearerScheme)
            .Configure<IOptions<AuthenticationSettings>>((bearer, settings) =>
            {
                bearer.BearerTokenExpiration = TimeSpan.FromMinutes(settings.Value.ApiAccessTokenMinutes);
                bearer.RefreshTokenExpiration = TimeSpan.FromDays(settings.Value.ApiRefreshTokenDays);

                // Logout support: access tokens of ended API sessions fail authentication (401).
                bearer.Events.OnMessageReceived = ApiSessionValidator.ValidateAsync;
            });

        services.AddProblemDetails(options => options.CustomizeProblemDetails = AddDiagnostics);
        services.AddExceptionHandler<ApiExceptionHandler>();

        // Accept what people commonly type in hand-written JSON: a comma after the last property
        // ({ "a": 1, }) and // or /* */ comments. Both are ignored; nothing else about parsing changes.
        services.Configure<JsonOptions>(options =>
        {
            options.JsonSerializerOptions.AllowTrailingCommas = true;
            options.JsonSerializerOptions.ReadCommentHandling = JsonCommentHandling.Skip;
        });

        // [ApiController] answers invalid input with 400 before the action runs; log which fields failed.
        // PostConfigure: runs after MVC has installed its default response factory, which is kept.
        services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            var createDefaultResponse = options.InvalidModelStateResponseFactory;
            options.InvalidModelStateResponseFactory = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(AppApiController).FullName!);

                RemoveBodyRequiredErrorWhenJsonIsMalformed(context);

                // Field names only: the submitted values may be passwords or personal data.
                var invalidFields = context.ModelState
                    .Where(entry => entry.Value is { Errors.Count: > 0 })
                    .Select(entry => string.IsNullOrEmpty(entry.Key) ? "(body)" : entry.Key);

                logger.LogInformation(
                    "API request {Method} {Path} rejected: invalid input in {InvalidFields}.",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path,
                    string.Join(", ", invalidFields));

                return createDefaultResponse(context);
            };
        });

        return services;
    }

    /// <summary>
    /// When the body is not valid JSON, MVC reports the parse error (key <c>"$"</c>, with line and position)
    /// AND "The request field is required." for the body parameter. The second is only a consequence of the
    /// first and sends readers looking for a missing field, so it is removed.
    /// </summary>
    private static void RemoveBodyRequiredErrorWhenJsonIsMalformed(ActionContext context)
    {
        var modelState = context.ModelState;
        if (!modelState.Keys.Any(key => key == "$" || key.StartsWith("$.", StringComparison.Ordinal)))
        {
            return;
        }

        var bodyParameters = context.ActionDescriptor.Parameters
            .Where(parameter => parameter.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(parameter => parameter.Name);

        foreach (var name in bodyParameters)
        {
            modelState.Remove(name);
        }
    }

    /// <summary>
    /// For <c>/api</c> requests only: unhandled exceptions and empty error responses (401, 403, 404,
    /// 405, 429, ...) become JSON problem details. Add it after the page exception handler, so it runs first.
    /// </summary>
    public static IApplicationBuilder UseApiErrorResponses(this IApplicationBuilder app) =>
        app.UseWhen(context => context.Request.IsApiRequest(), api =>
        {
            api.UseExceptionHandler();   // ApiExceptionHandler
            api.UseStatusCodePages();    // problem details body for status-only responses
        });

    /// <summary>
    /// Keeps the pre-versioning paths working: <c>/api/auth/...</c> is served by <c>/api/v1/auth/...</c>, with a
    /// <c>Deprecation</c> header and a <c>Link</c> to the new path. Add it before <c>UseRouting</c>.
    /// </summary>
    public static IApplicationBuilder UseLegacyApiPaths(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(LegacyAuthPath, StringComparison.OrdinalIgnoreCase, out var rest))
            {
                var successor = new PathString(CurrentAuthPath).Add(rest);
                context.Request.Path = successor;
                context.Response.Headers["Deprecation"] = "true";
                context.Response.Headers.Link = $"<{successor}>; rel=\"successor-version\"";
            }

            return next(context);
        });

    private const string LegacyAuthPath = "/api/auth";
    private const string CurrentAuthPath = "/api/v1/auth";

    /// <summary>
    /// Adds the correlation id to every problem details document, and a stable <c>code</c> when the
    /// response does not carry a more specific one.
    /// </summary>
    private static void AddDiagnostics(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;

        if (context.HttpContext.GetCorrelationId() is { } correlationId)
        {
            problem.Extensions["correlationId"] = correlationId;
        }

        if (!problem.Extensions.ContainsKey(AppApiController.ErrorCodeExtension))
        {
            problem.Extensions[AppApiController.ErrorCodeExtension] = problem.Status switch
            {
                StatusCodes.Status400BadRequest when problem is HttpValidationProblemDetails => "Request.ValidationFailed",
                StatusCodes.Status400BadRequest => "Request.BadRequest",
                StatusCodes.Status401Unauthorized => "Auth.Unauthorized",
                StatusCodes.Status403Forbidden => "Auth.Forbidden",
                StatusCodes.Status404NotFound => "Request.NotFound",
                StatusCodes.Status405MethodNotAllowed => "Request.MethodNotAllowed",
                StatusCodes.Status413PayloadTooLarge => "Request.TooLarge",
                StatusCodes.Status415UnsupportedMediaType => "Request.UnsupportedMediaType",
                StatusCodes.Status429TooManyRequests => "Request.RateLimited",
                >= 500 => "Server.UnexpectedError",
                _ => "Request.Failed",
            };
        }
    }
}
