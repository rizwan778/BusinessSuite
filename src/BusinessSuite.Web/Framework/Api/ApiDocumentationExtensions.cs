using BusinessSuite.Application.Common.Security;
using BusinessSuite.Web.Framework.Hosting;
using BusinessSuite.Web.Framework.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace BusinessSuite.Web.Framework.Api;

/// <summary>
/// OpenAPI document for the <c>/api</c> endpoints and the Swagger UI page that displays it.
/// Served in Development only: <c>/swagger</c> (UI) and <c>/openapi/v1.json</c> (document).
/// </summary>
/// <remarks>
/// The document is generated from the controllers: routes, request/response types,
/// <c>[ProducesResponseType]</c> attributes and the XML <c>///</c> comments on actions.
/// Swagger UI's "Authorize" button accepts the <c>accessToken</c> from <c>/api/auth/login</c>.
/// </remarks>
public static class ApiDocumentationExtensions
{
    /// <summary>URL of the Swagger UI page.</summary>
    public const string UiPath = "/swagger";

    /// <summary>URL of the OpenAPI document.</summary>
    public const string DocumentPath = "/openapi/v1.json";

    private const string SecuritySchemeId = "Bearer";

    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi(options =>
        {
            // Only the JSON API; MVC pages and error routes are not part of it.
            options.ShouldInclude = description =>
                description.RelativePath?.StartsWith("api/", StringComparison.OrdinalIgnoreCase) == true;

            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "BusinessSuite API",
                    Version = AppInfo.Version,
                    Description = "Sign up and sign in with `/api/v1/auth/register` and `/api/v1/auth/login`, then press " +
                                  "**Authorize** and paste the `accessToken`. If you belong to several organizations, choose " +
                                  "one per request with the `X-Organization-Id` header. Errors are RFC 9457 problem details " +
                                  "with a stable `code` and a `correlationId` (see docs/API.md).",
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[SecuritySchemeId] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "The `accessToken` returned by `/api/auth/login` or `/api/auth/refresh`.",
                };

                return Task.CompletedTask;
            });

            // Say what the caller needs. Read from the real authorization attributes, so the documentation cannot drift from what is enforced.
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var needs = new List<string>();
                var inOrganization = false;

                var permissions = metadata.OfType<RequirePermissionAttribute>().Select(attribute => attribute.Permission).Distinct(StringComparer.Ordinal).ToList();
                if (permissions.Count > 0)
                {
                    needs.Add($"the permission{(permissions.Count > 1 ? "s" : string.Empty)} {string.Join(" and ", permissions.Select(permission => $"`{permission}`"))}");
                    inOrganization |= permissions.Any(permission => !permission.StartsWith(Permissions.PlatformPrefix, StringComparison.Ordinal));
                }

                foreach (var any in metadata.OfType<RequireAnyPermissionAttribute>())
                {
                    needs.Add($"one of the permissions {string.Join(", ", any.Permissions.Select(permission => $"`{permission}`"))}");
                    inOrganization |= any.Permissions.Any(permission => !permission.StartsWith(Permissions.PlatformPrefix, StringComparison.Ordinal));
                }

                foreach (var roles in metadata.OfType<RequireOrganizationRoleAttribute>())
                {
                    needs.Add($"one of the organization roles {string.Join(", ", roles.Roles.Select(role => $"`{role}`"))}");
                    inOrganization = true;
                }

                if (metadata.OfType<RequireSuperAdminAttribute>().Any())
                {
                    needs.Add("the Super Admin role");
                }

                if (needs.Count > 0)
                {
                    var line = $"**Requires** {string.Join(", and ", needs)}." +
                               (inOrganization ? " Checked in the organization the request acts in (`X-Organization-Id`, or the caller's only organization)." : string.Empty);
                    operation.Description = string.IsNullOrWhiteSpace(operation.Description) ? line : $"{operation.Description}\n\n{line}";
                }

                return Task.CompletedTask;
            });

            // Mark actions protected by the bearer scheme, so Swagger UI sends the token and shows a lock.
            options.AddOperationTransformer((operation, context, _) =>
            {
                var requiresToken = context.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IAuthorizeData>()
                    .Any(authorize => authorize.AuthenticationSchemes?.Contains(AppApiController.BearerScheme, StringComparison.Ordinal) == true);

                if (requiresToken)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(SecuritySchemeId, context.Document)] = [],
                    });

                    // The organization to act in; always checked against the caller's memberships on the server.
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = CurrentAccessProvider.OrganizationHeader,
                        In = ParameterLocation.Header,
                        Required = false,
                        Description = "Organization to act in. Optional when you belong to exactly one. Must be one you are an " +
                                      "active member of (Super Admins: any active organization); otherwise you get no organization permissions.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                    });
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>Maps the OpenAPI document (anonymous). Call only in Development.</summary>
    public static IEndpointRouteBuilder MapApiDocument(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapOpenApi(DocumentPath).AllowAnonymous();
        return endpoints;
    }

    /// <summary>
    /// Serves the Swagger UI page. Call only in Development, after the security headers (so the page gets
    /// the normal CSP) and before authorization (the page itself is public; the API calls it makes are not).
    /// </summary>
    public static IApplicationBuilder UseApiDocumentationUi(this IApplicationBuilder app)
    {
        return app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = UiPath.TrimStart('/');
            options.SwaggerEndpoint(DocumentPath, "BusinessSuite API");
            options.DocumentTitle = "BusinessSuite API";
            options.EnablePersistAuthorization(); // keep the pasted token across page reloads
        });
    }
}
