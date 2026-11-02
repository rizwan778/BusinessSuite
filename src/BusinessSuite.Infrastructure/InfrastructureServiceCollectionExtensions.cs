using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Application.Features.Accounts;
using BusinessSuite.Application.Features.Audit;
using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Groups;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Application.Features.Organizations;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Infrastructure.Auditing;
using BusinessSuite.Infrastructure.Diagnostics;
using BusinessSuite.Infrastructure.Email;
using BusinessSuite.Infrastructure.Catalog;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Infrastructure.Inventory;
using BusinessSuite.Infrastructure.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Infrastructure.Persistence.Interceptors;
using BusinessSuite.Infrastructure.Persistence.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BusinessSuite.Infrastructure;

/// <summary>
/// Registers everything the Infrastructure layer provides. Called once from <c>Program.cs</c>.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Adds SQL Server persistence, ASP.NET Core Identity and the database health check.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPersistence();
        services.AddIdentityServices();
        services.AddEmail();
        services.AddCatalogAndInventory();

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>(name: "database", tags: [HealthCheckTags.Ready]);

        return services;
    }

    /// <summary>
    /// Applies pending migrations and seeds the development user. Call only in Development.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(cancellationToken);
    }

    private static void AddPersistence(this IServiceCollection services)
    {
        services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName) ?? string.Empty)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<AuditableEntityInterceptor>();

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();

            options.UseSqlServer(database.ConnectionString, sqlServer =>
            {
                sqlServer.CommandTimeout(database.CommandTimeoutSeconds);

                if (database.MaxRetryCount > 0)
                {
                    sqlServer.EnableRetryOnFailure(database.MaxRetryCount);
                }
            });

            options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntityInterceptor>());

            // Extra diagnostics are only ever allowed on a developer machine.
            if (environment.IsDevelopment())
            {
                options.EnableSensitiveDataLogging(database.EnableSensitiveDataLogging);
                options.EnableDetailedErrors(database.EnableDetailedErrors);
            }
        });

        services.AddOptions<DevelopmentSeedOptions>().BindConfiguration(DevelopmentSeedOptions.SectionName);
        services.AddScoped<DevelopmentUserSeeder>();
        services.AddScoped<DatabaseInitializer>();
    }

    private static void AddIdentityServices(this IServiceCollection services)
    {
        services
            .AddIdentity<ApplicationUser, ApplicationRole>(options =>
            {
                // Secure defaults. Each value can be overridden in the "Identity" section of
                // appsettings.json (see docs/CONFIGURATION.md and docs/SECURITY.md).
                options.User.RequireUniqueEmail = true;

                options.Password.RequiredLength = 12;
                options.Password.RequiredUniqueChars = 4;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;

                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                // E-mail confirmation is switched on in Phase 2, when an e-mail sender exists.
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<ApplicationSignInManager>()
            .AddClaimsPrincipalFactory<ApplicationUserClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        // appsettings.json "Identity" section overrides the defaults above.
        services.AddOptions<IdentityOptions>().BindConfiguration("Identity");

        // Re-validate the cookie against the database every 5 minutes, so a password change
        // or deactivation (which updates the security stamp) ends other sessions quickly.
        services.Configure<SecurityStampValidatorOptions>(options =>
            options.ValidationInterval = TimeSpan.FromMinutes(5));

        services.AddScoped<IUserRegistrationService, UserRegistrationService>();
        services.AddScoped<IUserSessionStore, UserSessionStore>();
        services.AddScoped<IUserAccessService, UserAccessService>();
        services.AddScoped<IUserManagementService, UserManagementService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<IStockPostingService, StockPostingService>();
        services.AddScoped<RoleCatalog>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IGroupService, GroupService>();
        services.AddScoped<IPasswordResetService, PasswordResetService>();
        services.AddScoped<PasswordResetMailer>();
        services.AddScoped<AuditTrail>();
        services.AddScoped<IAuditLogService, AuditLogService>();
    }

    private static void AddCatalogAndInventory(this IServiceCollection services)
    {
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IUnitService, UnitService>();
        services.AddScoped<IAttributeService, AttributeService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IWarehouseService, WarehouseService>();
        services.AddScoped<StockLedger>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<IStockAdjustmentService, StockAdjustmentService>();
        services.AddScoped<IStockTransferService, StockTransferService>();
        services.AddScoped<IBatchService, BatchService>();
        services.AddScoped<IInventoryReportService, InventoryReportService>();
    }

    private static void AddEmail(this IServiceCollection services)
    {
        services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IEmailSender, PickupDirectoryEmailSender>();
    }
}
