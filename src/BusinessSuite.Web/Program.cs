using BusinessSuite.Application;
using BusinessSuite.Infrastructure;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Diagnostics;
using BusinessSuite.Web.Framework.Hosting;
using BusinessSuite.Web.Framework.Preview;
using BusinessSuite.Web.Framework.Security;

// =============================================================================
// BusinessSuite.Web - application entry point and "composition root".
// Read it top to bottom:
//   1. register services   2. build   3. HTTP pipeline   4. endpoints   5. run
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// Do not advertise the web server product in a "Server" response header.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

// Serve css/js (including pre-compressed .gz/.br variants) from the BUILD output in every
// environment, so "dotnet run --environment Production" works on a developer machine.
// No effect after "dotnet publish", where the files are already in the publish folder.
builder.WebHost.UseStaticWebAssets();

// Also log to logs/app-yyyyMMdd.log (levels: Logging:File:LogLevel), so problems can be inspected afterwards.
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "logs")));

// ---- 1. Services (dependency injection) ------------------------------------
builder.Services
    .AddApplication()           // use cases, options, clock      -> BusinessSuite.Application
    .AddInfrastructure()        // SQL Server, EF Core, Identity  -> BusinessSuite.Infrastructure
    .AddWebPresentation()       // MVC, cookies, policies, limits -> Framework/Hosting
    .AddPurchasingPreview();    // in-memory stand-in until the purchasing backend exists -> Framework/Preview

// ---- 2. Build ---------------------------------------------------------------
var app = builder.Build();

// ---- 3. HTTP request pipeline: ORDER MATTERS (each step wraps the next) -----
app.UseCorrelationId();                  // a. id on every request, response and log line

if (app.Environment.IsDevelopment())
{
    // b. Development: ASP.NET Core shows the detailed Developer Exception Page automatically.
}
else
{
    app.UseExceptionHandler("/Error");   // b. Production: friendly page; details only in logs
    app.UseHsts();                       //    browsers remember to use HTTPS
}

app.UseApiErrorResponses();              // c. /api/*: exceptions and errors as JSON problem details
app.UseWhen(context => !context.Request.IsApiRequest(), pages =>
    pages.UseStatusCodePagesWithReExecute("/Error/{0}")); // d. pages: friendly 400/403/404/429 pages
app.UseSecurityHeaders();                // e. CSP, X-Frame-Options, ... on every response
app.UseHttpsRedirection();               // f. http:// -> https://

if (app.Environment.IsDevelopment())
{
    app.UseApiDocumentationUi();         //    Development: Swagger UI at /swagger (public page)
}

app.UseLegacyApiPaths();                 //    /api/auth/* -> /api/v1/auth/* (deprecated paths keep working)
app.UseRouting();                      // g. select the endpoint (controller action)
app.UseRateLimiter();                    // h. throttle sensitive endpoints (after routing)
app.UseAuthentication();                 // i. who is the user? (Identity cookie; API: bearer token)
app.UseAuthorization();                  // j. are they allowed? (fallback policy, [Authorize])

// ---- 4. Endpoints -------------------------------------------------------------
app.MapStaticAssets().AllowAnonymous();  // css, js, fonts, images are public
app.MapHealthEndpoints();                // /health/live and /health/ready

if (app.Environment.IsDevelopment())
{
    app.MapApiDocument();                // OpenAPI document at /openapi/v1.json
}

app.MapControllerRoute(                  // module areas, e.g. /Inventory/Products
        name: "areas",
        pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapControllerRoute(                  // root controllers, e.g. /Account/Login
        name: "default",
        pattern: "{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();

// ---- 5. Development database: apply migrations and create the dev user ------
if (app.Environment.IsDevelopment())
{
    await app.Services.InitializeDatabaseAsync();
}

await app.RunAsync();

/// <summary>The application entry point. Public so integration tests can host the app.</summary>
public partial class Program
{
}
