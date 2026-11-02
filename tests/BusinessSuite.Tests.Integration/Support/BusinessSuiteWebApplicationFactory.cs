using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>
/// Starts the real BusinessSuite web application in memory for tests.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Environment "Testing": production-style error pages, no automatic migrations.</item>
/// <item>A placeholder connection string is used unless a test supplies a real database;
/// tests that never touch the database therefore need no SQL Server.</item>
/// <item>Requests carrying the <see cref="TestAuthenticationHandler.UserIdHeader"/> header are
/// treated as signed in, so pages can be tested without creating users in a database.</item>
/// <item>Dependency-injection validation is switched on, so a missing registration fails the tests.</item>
/// </list>
/// Derive from this class and override <see cref="ConfigureSettings"/> or
/// <see cref="ConfigureTestServices"/> to change configuration or replace services.
/// </remarks>
public class BusinessSuiteWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary><see cref="IUserAccessService"/> for tests without a database: no roles, no organizations.</summary>
    private sealed class NoMembershipsUserAccessService : IUserAccessService
    {
        public Task<UserAccess> GetAccessAsync(Guid userId, Guid? requestedOrganizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(UserAccessCalculator.Calculate(userId, [], [], requestedOrganizationId));
    }

    /// <summary>A connection string nothing connects to (no credentials).</summary>
    public const string UnusedConnectionString =
        "Server=127.0.0.1,1;Database=BusinessSuite_NotUsed;Integrated Security=true;Encrypt=False;Connect Timeout=1";

    private readonly IReadOnlyDictionary<string, string?> _extraSettings;

    public BusinessSuiteWebApplicationFactory()
        : this(new Dictionary<string, string?>())
    {
    }

    // Internal: xUnit refuses a class fixture with more than one PUBLIC constructor.
    internal BusinessSuiteWebApplicationFactory(IReadOnlyDictionary<string, string?> extraSettings)
    {
        _extraSettings = extraSettings;
    }

    /// <summary>Creates a client that behaves like a browser: HTTPS, cookies, no automatic redirects.</summary>
    public HttpClient CreateBrowserClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>Creates a browser client that is signed in as a test user (no database needed).</summary>
    public HttpClient CreateSignedInClient(string displayName = "Test User", params string[] roles)
    {
        var client = CreateBrowserClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserIdHeader, Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.UserNameHeader, "test.user@example.com");
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.DisplayNameHeader, displayName);

        if (roles.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.RolesHeader, string.Join(',', roles));
        }

        return client;
    }

    /// <summary>Override to add or change configuration values for a test class.</summary>
    protected virtual void ConfigureSettings(IDictionary<string, string?> settings)
    {
    }

    /// <summary>Override to replace application services for a test class.</summary>
    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = UnusedConnectionString,
            ["Database:ApplyMigrationsOnStartup"] = "false",
        };

        foreach (var (key, value) in _extraSettings)
        {
            settings[key] = value;
        }

        ConfigureSettings(settings);
        var usesDatabase = settings["ConnectionStrings:DefaultConnection"] != UnusedConnectionString;

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { })
                .AddPolicyScheme(TestAuthenticationHandler.SelectorSchemeName, displayName: null, options =>
                    options.ForwardDefaultSelector = context =>
                        context.Request.Headers.ContainsKey(TestAuthenticationHandler.UserIdHeader)
                            ? TestAuthenticationHandler.SchemeName
                            : IdentityConstants.ApplicationScheme);

            services.Configure<AuthenticationOptions>(options =>
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SelectorSchemeName);

            // Test-only API endpoints that exercise the authorization framework (AccessProbeController).
            services.AddControllers().AddApplicationPart(typeof(BusinessSuiteWebApplicationFactory).Assembly);

            // E-mails stay in memory, where tests can read them (TestEmailOutbox).
            services.AddSingleton<TestEmailOutbox>();
            services.AddSingleton<IEmailSender>(provider => provider.GetRequiredService<TestEmailOutbox>());

            // Log lines stay in memory too (TestLogSink), so tests can check what an operation logged.
            services.AddSingleton<TestLogSink>();
            services.AddSingleton<ILoggerProvider, TestLogSinkProvider>();

            if (!usesDatabase)
            {
                // Every signed-in request looks up the user's organizations and roles; without a database the
                // user simply has none (no organization, no permissions).
                services.AddScoped<IUserAccessService, NoMembershipsUserAccessService>();
            }

            ConfigureTestServices(services);
        });
    }
}
