using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Identity;
using BusinessSuite.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessSuite.Tests.Integration.Support;

/// <summary>
/// What the inventory integration tests share: organizations, signed-in people with real roles, API calls, setup through the
/// API (so the real validation runs) and checks made directly against the database (the ledger must always add up).
/// </summary>
public sealed class InventoryHarness(SqlServerAppFixture app)
{
    /// <summary>A signed-in person: their account id, e-mail and bearer token.</summary>
    public sealed record Person(Guid UserId, string Email, string Token);

    // ---- People and organizations -------------------------------------------------------------------------

    public async Task<Guid> CreateOrganizationAsync()
    {
        var organization = Organization.Create($"Inventory Test {Guid.NewGuid():N}");
        await WithDbAsync(async db =>
        {
            db.Organizations.Add(organization);
            return await db.SaveChangesAsync();
        });
        return organization.Id;
    }

    /// <summary>A person with a SYSTEM role (Owner, Administrator, Manager, InventoryManager, SalesOperator, Accountant, ReadOnly) in the organization.</summary>
    public Task<Person> CreatePersonAsync(Guid organizationId, string systemRole) => CreateAsync(null, (organizationId, systemRole));

    /// <summary>A person with a platform role (e.g. <c>SuperAdmin</c>) and no organization.</summary>
    public Task<Person> CreatePlatformPersonAsync(string platformRole) => CreateAsync(platformRole);

    private async Task<Person> CreateAsync(string? platformRole, params (Guid OrganizationId, string Role)[] memberships)
    {
        var email = $"inv-{Guid.NewGuid():N}@example.com";
        var password = $"Pw-{Guid.NewGuid():N}aA1!";

        using var scope = app.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        if (platformRole is not null)
        {
            Assert.True((await users.AddToRoleAsync(user, platformRole)).Succeeded);
        }

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (organizationId, role) in memberships)
        {
            db.OrganizationMemberships.Add(OrganizationMembership.Create(organizationId, user.Id, role));
        }

        await db.SaveChangesAsync();

        using var client = app.Factory.CreateBrowserClient();
        using var login = await client.PostJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return new Person(user.Id, email, (await login.ReadJsonAsync()).GetProperty("accessToken").GetString()!);
    }

    // ---- Calls -------------------------------------------------------------------------------------------------

    /// <summary>Calls the API as <paramref name="who"/>. <paramref name="organization"/> is the <c>X-Organization-Id</c> header (checked by the server).</summary>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Person who, object? body = null, Guid? organization = null)
    {
        using var client = app.Factory.CreateBrowserClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", who.Token);
        if (organization is { } organizationId)
        {
            request.Headers.Add("X-Organization-Id", organizationId.ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    /// <summary>The JSON of a response that must have the <paramref name="expected"/> status; the body is part of the failure message.</summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected} but got {response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public async Task<JsonElement> GetAsync(string path, Person who, Guid? organization = null)
    {
        using var response = await SendAsync(HttpMethod.Get, path, who, null, organization);
        return await JsonAsync(response);
    }

    /// <summary>Calls the API and returns the status plus the problem <c>code</c> of an error response (or <see langword="null"/> on success).</summary>
    public async Task<(HttpStatusCode Status, string? Code)> TryAsync(HttpMethod method, string path, Person who, object? body = null, Guid? organization = null)
    {
        using var response = await SendAsync(method, path, who, body, organization);
        if (response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null);
        }

        return (response.StatusCode, await response.ReadProblemCodeAsync());
    }

    // ---- Setup through the API -----------------------------------------------------------------------------

    public async Task<Guid> CreateUnitAsync(Person who, string code = "PCS", int decimalPlaces = 0)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/units", who, new { code, name = code + " unit", decimalPlaces });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    public async Task<Guid> CreateWarehouseAsync(Person who, string code)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/warehouses", who, new { code, name = "Warehouse " + code });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    /// <summary>A simple stock-tracking product (no variants, no batches) counted in <paramref name="unitId"/>.</summary>
    public async Task<Guid> CreateProductAsync(Person who, Guid unitId, string sku, bool allowNegativeStock = false, decimal? reorderLevel = null)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/products", who,
            new { sku, name = "Product " + sku, baseUnitId = unitId, hasVariants = false, tracksStock = true, allowNegativeStock, reorderLevel, salePrice = 10m });
        return (await JsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    /// <summary>An organization with an Owner, a unit, one warehouse and a few simple products: the usual starting point.</summary>
    public async Task<Scene> CreateSceneAsync(int products = 2)
    {
        var organizationId = await CreateOrganizationAsync();
        var owner = await CreatePersonAsync(organizationId, OrganizationRoles.Owner);
        var unit = await CreateUnitAsync(owner);
        var warehouse = await CreateWarehouseAsync(owner, "MAIN");
        var productIds = new List<Guid>();
        for (var number = 1; number <= products; number++)
        {
            productIds.Add(await CreateProductAsync(owner, unit, $"SKU-{Guid.NewGuid():N}"[..14]));
        }

        return new Scene(organizationId, owner, unit, warehouse, productIds);
    }

    public sealed record Scene(Guid OrganizationId, Person Owner, Guid UnitId, Guid WarehouseId, IReadOnlyList<Guid> Products);

    /// <summary>A draft adjustment (a line is product, signed quantity, optional unit cost); returns the created document.</summary>
    public async Task<JsonElement> CreateAdjustmentAsync(Person who, Guid warehouseId, params (Guid Product, decimal Difference, decimal? Cost)[] lines)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/stock-adjustments", who, new
        {
            warehouseId,
            reason = "StockCount",
            items = lines.Select(line => new { productId = line.Product, quantityDifference = line.Difference, unitCost = line.Cost }).ToArray(),
        });
        return await JsonAsync(response, HttpStatusCode.Created);
    }

    /// <summary>Creates and posts an adjustment; returns its id. Fails the test if either step is refused.</summary>
    public async Task<Guid> PostAdjustmentAsync(Person who, Guid warehouseId, params (Guid Product, decimal Difference, decimal? Cost)[] lines)
    {
        var id = (await CreateAdjustmentAsync(who, warehouseId, lines)).GetProperty("id").GetGuid();
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/stock-adjustments/{id}/post", who);
        await JsonAsync(response);
        return id;
    }

    public async Task<JsonElement> CreateTransferAsync(Person who, Guid fromWarehouseId, Guid toWarehouseId, params (Guid Product, decimal Quantity)[] lines)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/stock-transfers", who, new
        {
            fromWarehouseId,
            toWarehouseId,
            items = lines.Select(line => new { productId = line.Product, quantity = line.Quantity }).ToArray(),
        });
        return await JsonAsync(response, HttpStatusCode.Created);
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a fresh request scope in which the CURRENT USER is <paramref name="who"/> acting in
    /// <paramref name="organizationId"/> (the same claims the server adds to a verified request). Lets a test call Application
    /// services directly, as another module (Sales, Purchases) would.
    /// </summary>
    public async Task<T> RunAsAsync<T>(Person who, Guid? organizationId, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = app.Factory.Services.CreateScope();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, who.UserId.ToString()) };
        if (organizationId is { } actingIn)
        {
            claims.Add(new Claim(AppClaimTypes.OrganizationId, actingIn.ToString())); // absent = the request acts in no organization
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "Test");
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext =
            new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = scope.ServiceProvider };
        return await action(scope.ServiceProvider);
    }

    // ---- Database checks --------------------------------------------------------------------------------

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = app.Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Quantity on hand of a product at a warehouse, read from the balance table (0 when there is no balance row yet).</summary>
    public Task<decimal> OnHandAsync(Guid warehouseId, Guid productId) =>
        WithDbAsync(async db => await db.InventoryBalances.AsNoTracking()
            .Where(balance => balance.WarehouseId == warehouseId && balance.ProductId == productId)
            .Select(balance => (decimal?)balance.QuantityOnHand).SingleOrDefaultAsync() ?? 0m);

    public Task<int> MovementCountAsync(Guid organizationId) =>
        WithDbAsync(db => db.StockMovements.AsNoTracking().CountAsync(movement => movement.OrganizationId == organizationId));

    public Task<int> AuditCountAsync(Guid entityId, string action) =>
        WithDbAsync(db => db.AuditLogs.AsNoTracking().CountAsync(entry => entry.EntityId == entityId && entry.Action == action));

    /// <summary>
    /// The core inventory invariant, checked on the database itself: for every balance the quantity on hand equals the sum of its
    /// ledger movements (nothing else changes stock), no balance of this organization is negative unless allowed, and there are no
    /// movements without a balance.
    /// </summary>
    public async Task AssertLedgerMatchesBalancesAsync(Guid organizationId)
    {
        await WithDbAsync(async db =>
        {
            var movementTotals = (await db.StockMovements.AsNoTracking().Where(movement => movement.OrganizationId == organizationId)
                    .GroupBy(movement => new { movement.WarehouseId, movement.ProductId, movement.ProductVariantId, movement.BatchId })
                    .Select(group => new { group.Key, Total = group.Sum(movement => movement.Quantity) })
                    .ToListAsync())
                .ToDictionary(row => row.Key, row => row.Total);

            var balances = await db.InventoryBalances.AsNoTracking().Where(balance => balance.OrganizationId == organizationId).ToListAsync();
            foreach (var balance in balances)
            {
                var key = new { balance.WarehouseId, balance.ProductId, balance.ProductVariantId, balance.BatchId };
                var ledgerTotal = movementTotals.GetValueOrDefault(key);
                Assert.True(ledgerTotal == balance.QuantityOnHand,
                    $"Balance of product {balance.ProductId} at warehouse {balance.WarehouseId} is {balance.QuantityOnHand} but its ledger adds up to {ledgerTotal}.");
                movementTotals.Remove(key);
            }

            Assert.True(movementTotals.Count == 0, $"{movementTotals.Count} group(s) of movements have no balance row.");
            return 0;
        });
    }

    /// <summary>Runs <paramref name="command"/> against the database and returns the SQL Server error number it was refused with (a trigger's THROW number, 2601 for a unique index, 547 for a constraint).</summary>
    public async Task<int> RefusedWithAsync(Func<AppDbContext, Task> command)
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => WithDbAsync(async db =>
        {
            await command(db);
            return 0;
        }));

        var sql = exception as SqlException ?? exception.InnerException as SqlException ?? exception.InnerException?.InnerException as SqlException;
        Assert.True(sql is not null, $"Expected the database to refuse the change but got {exception.GetType().Name}: {exception.Message}");
        return sql!.Number;
    }
}
