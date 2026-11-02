using System.Net;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Tests.Integration.Support;

namespace BusinessSuite.Tests.Integration.Inventory;

/// <summary>
/// The inventory API against the existing role and permission system. Every endpoint family is wired to the permission it should
/// be: a person holding ONLY one inventory permission reaches exactly the endpoints that need it, and each system role reaches what
/// its permissions allow. Allowed means "not 403": the request then runs and may fail validation or find nothing, which is fine
/// here, since only authorization is being tested.
/// </summary>
public sealed class InventoryPermissionTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private readonly InventoryHarness _harness = new(app);

    private static readonly Guid Unknown = Guid.NewGuid();

    private static readonly object Empty = new { };
    private static readonly object On = new { isActive = true };

    /// <summary>The permission each inventory endpoint requires.</summary>
    private static readonly (HttpMethod Method, string Path, object? Body, string Permission)[] Endpoints =
    [
        // Reading anything in the module
        (HttpMethod.Get, "/api/v1/products", null, Permissions.Inventory.View),
        (HttpMethod.Get, $"/api/v1/products/{Unknown}", null, Permissions.Inventory.View),
        (HttpMethod.Get, $"/api/v1/products/{Unknown}/variants", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/categories", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/categories/tree", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/units", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/units/conversions", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/product-attributes", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/product-attributes/schema", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/warehouses", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/warehouses/default", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/batches", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/stock", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/stock/low", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/stock/out-of-stock", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/stock-movements", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/stock-adjustments", null, Permissions.Inventory.View),
        (HttpMethod.Get, "/api/v1/stock-transfers", null, Permissions.Inventory.View),

        // The dashboard and reports show costs and values
        (HttpMethod.Get, "/api/v1/inventory/dashboard", null, Permissions.Inventory.ReportsView),
        (HttpMethod.Get, "/api/v1/inventory/reports/valuation", null, Permissions.Inventory.ReportsView),
        (HttpMethod.Get, "/api/v1/inventory/reports/stock-by-warehouse", null, Permissions.Inventory.ReportsView),
        (HttpMethod.Get, "/api/v1/inventory/reports/stock-by-product", null, Permissions.Inventory.ReportsView),

        // Products, variants, categories, units
        (HttpMethod.Post, "/api/v1/products", Empty, Permissions.Inventory.ProductManage),
        (HttpMethod.Put, $"/api/v1/products/{Unknown}", new { sku = "S", name = "N", baseUnitId = Unknown }, Permissions.Inventory.ProductManage),
        (HttpMethod.Patch, $"/api/v1/products/{Unknown}/status", On, Permissions.Inventory.ProductManage),
        (HttpMethod.Delete, $"/api/v1/products/{Unknown}", null, Permissions.Inventory.ProductManage),
        (HttpMethod.Post, $"/api/v1/products/{Unknown}/variants", new { sku = "S" }, Permissions.Inventory.ProductManage),
        (HttpMethod.Put, $"/api/v1/products/{Unknown}/variants/{Unknown}", new { sku = "S" }, Permissions.Inventory.ProductManage),
        (HttpMethod.Delete, $"/api/v1/products/{Unknown}/variants/{Unknown}", null, Permissions.Inventory.ProductManage),
        (HttpMethod.Post, "/api/v1/categories", Empty, Permissions.Inventory.ProductManage),
        (HttpMethod.Delete, $"/api/v1/categories/{Unknown}", null, Permissions.Inventory.ProductManage),
        (HttpMethod.Post, "/api/v1/units", Empty, Permissions.Inventory.ProductManage),
        (HttpMethod.Delete, $"/api/v1/units/{Unknown}", null, Permissions.Inventory.ProductManage),
        (HttpMethod.Post, "/api/v1/units/conversions", Empty, Permissions.Inventory.ProductManage),

        // The attributes products are described with
        (HttpMethod.Post, "/api/v1/product-attributes", Empty, Permissions.Inventory.AttributeManage),
        (HttpMethod.Put, $"/api/v1/product-attributes/{Unknown}", new { name = "N" }, Permissions.Inventory.AttributeManage),
        (HttpMethod.Delete, $"/api/v1/product-attributes/{Unknown}", null, Permissions.Inventory.AttributeManage),
        (HttpMethod.Post, $"/api/v1/product-attributes/{Unknown}/options", new { label = "L" }, Permissions.Inventory.AttributeManage),
        (HttpMethod.Delete, $"/api/v1/product-attributes/{Unknown}/options/{Unknown}", null, Permissions.Inventory.AttributeManage),

        // Stock adjustments and batches
        (HttpMethod.Post, "/api/v1/stock-adjustments", Empty, Permissions.Inventory.StockAdjust),
        (HttpMethod.Post, $"/api/v1/stock-adjustments/{Unknown}/post", null, Permissions.Inventory.StockAdjust),
        (HttpMethod.Post, $"/api/v1/stock-adjustments/{Unknown}/cancel", null, Permissions.Inventory.StockAdjust),
        (HttpMethod.Post, "/api/v1/batches", Empty, Permissions.Inventory.StockAdjust),
        (HttpMethod.Patch, $"/api/v1/batches/{Unknown}/status", On, Permissions.Inventory.StockAdjust),

        // Stock transfers
        (HttpMethod.Post, "/api/v1/stock-transfers", Empty, Permissions.Inventory.StockTransfer),
        (HttpMethod.Post, $"/api/v1/stock-transfers/{Unknown}/post", null, Permissions.Inventory.StockTransfer),
        (HttpMethod.Post, $"/api/v1/stock-transfers/{Unknown}/cancel", null, Permissions.Inventory.StockTransfer),

        // Warehouses
        (HttpMethod.Post, "/api/v1/warehouses", Empty, Permissions.Inventory.Manage),
        (HttpMethod.Put, $"/api/v1/warehouses/{Unknown}", new { code = "C", name = "N" }, Permissions.Inventory.Manage),
        (HttpMethod.Patch, $"/api/v1/warehouses/{Unknown}/status", On, Permissions.Inventory.Manage),
        (HttpMethod.Post, $"/api/v1/warehouses/{Unknown}/default", null, Permissions.Inventory.Manage),
        (HttpMethod.Delete, $"/api/v1/warehouses/{Unknown}", null, Permissions.Inventory.Manage),
    ];

    private static readonly string[] InventoryPermissions =
    [
        Permissions.Inventory.View, Permissions.Inventory.Manage, Permissions.Inventory.ProductManage, Permissions.Inventory.AttributeManage,
        Permissions.Inventory.StockAdjust, Permissions.Inventory.StockTransfer, Permissions.Inventory.ReportsView,
    ];

    [Fact]
    public void Every_inventory_permission_guards_at_least_one_endpoint_and_every_endpoint_names_a_real_one()
    {
        Assert.Equal(InventoryPermissions.Order(StringComparer.Ordinal), Endpoints.Select(endpoint => endpoint.Permission).Distinct().Order(StringComparer.Ordinal));
        Assert.All(Endpoints, endpoint => Assert.True(Permissions.IsOrganizationScoped(endpoint.Permission), endpoint.Permission));
    }

    [SqlServerFact]
    public async Task A_person_holding_only_one_inventory_permission_reaches_exactly_the_endpoints_that_need_it()
    {
        var organizationId = await _harness.CreateOrganizationAsync();
        var owner = await _harness.CreatePersonAsync(organizationId, OrganizationRoles.Owner);

        var wrong = new List<string>();
        foreach (var permission in InventoryPermissions)
        {
            var person = await PersonWithOnlyAsync(organizationId, owner, permission);
            foreach (var (method, path, body, required) in Endpoints)
            {
                var (status, code) = await _harness.TryAsync(method, path, person, body);
                var allowed = status != HttpStatusCode.Forbidden;
                if (allowed != (required == permission))
                {
                    wrong.Add($"only [{permission}] -> {method} {path} answered {(int)status} {code}; it requires [{required}]");
                }
                else if (!allowed)
                {
                    Assert.Equal("Auth.PermissionDenied", code);
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [SqlServerFact]
    public async Task Each_system_role_reaches_what_its_permissions_allow()
    {
        var organizationId = await _harness.CreateOrganizationAsync();
        var wrong = new List<string>();

        foreach (var role in OrganizationRoles.All)
        {
            var person = await _harness.CreatePersonAsync(organizationId, role);
            var held = role == OrganizationRoles.Owner ? Permissions.OrganizationScoped.ToHashSet() : RolePermissions.ForOrganizationRole(role).ToHashSet();
            foreach (var (method, path, body, required) in Endpoints)
            {
                var (status, _) = await _harness.TryAsync(method, path, person, body);
                if ((status != HttpStatusCode.Forbidden) != held.Contains(required))
                {
                    wrong.Add($"{role} -> {method} {path} answered {(int)status}; requires [{required}], which the role {(held.Contains(required) ? "holds" : "lacks")}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [SqlServerFact]
    public async Task The_default_roles_split_inventory_duties_sensibly()
    {
        // Pins the intended defaults, so a change to them is a decision and not an accident.
        bool Holds(string role, string permission) => RolePermissions.ForOrganizationRole(role).Contains(permission);

        // Selling people see stock but not costs and valuations; they change nothing in inventory.
        Assert.True(Holds(OrganizationRoles.SalesOperator, Permissions.Inventory.View));
        Assert.All(InventoryPermissions.Except([Permissions.Inventory.View]), permission => Assert.False(Holds(OrganizationRoles.SalesOperator, permission), permission));

        // The inventory manager runs the module end to end; the manager and administrator can too.
        Assert.All(InventoryPermissions, permission => Assert.True(Holds(OrganizationRoles.InventoryManager, permission), permission));
        Assert.All(InventoryPermissions, permission => Assert.True(Holds(OrganizationRoles.Administrator, permission), permission));
        Assert.All(InventoryPermissions, permission => Assert.True(Holds(OrganizationRoles.Manager, permission), permission));

        // Read-only people look at everything in the module and change nothing.
        Assert.True(Holds(OrganizationRoles.ReadOnly, Permissions.Inventory.View));
        Assert.True(Holds(OrganizationRoles.ReadOnly, Permissions.Inventory.ReportsView));
        Assert.All(InventoryPermissions.Where(permission => !permission.EndsWith(".view", StringComparison.Ordinal)), permission =>
            Assert.False(Holds(OrganizationRoles.ReadOnly, permission), permission));

        // Accountants have no inventory permissions by default (an administrator can grant them).
        Assert.All(InventoryPermissions, permission => Assert.False(Holds(OrganizationRoles.Accountant, permission), permission));
        await Task.CompletedTask;
    }

    [SqlServerFact]
    public async Task Changing_a_role_changes_what_its_holders_can_do_on_their_next_request()
    {
        var organizationId = await _harness.CreateOrganizationAsync();
        var owner = await _harness.CreatePersonAsync(organizationId, OrganizationRoles.Owner);
        var (person, roleId) = await PersonWithOnlyAndRoleAsync(organizationId, owner, Permissions.Inventory.View);

        Assert.Equal(HttpStatusCode.OK, (await _harness.TryAsync(HttpMethod.Get, "/api/v1/products", person)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", person, Empty)).Status);

        // The Owner swaps the permission: same token, different rights, immediately.
        using (var change = await _harness.SendAsync(HttpMethod.Put, $"/api/v1/roles/{roleId}/permissions", owner, new { permissions = new[] { Permissions.Inventory.StockAdjust } }))
        {
            await InventoryHarness.JsonAsync(change);
        }

        Assert.Equal((HttpStatusCode.Forbidden, "Auth.PermissionDenied"), await _harness.TryAsync(HttpMethod.Get, "/api/v1/products", person));
        Assert.Equal(HttpStatusCode.BadRequest, (await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", person, Empty)).Status);
    }

    [SqlServerFact]
    public async Task Anonymous_and_invalid_tokens_reach_nothing_and_a_member_of_another_organization_has_no_rights_here()
    {
        var mine = await _harness.CreateOrganizationAsync();
        var theirs = await _harness.CreateOrganizationAsync();
        var ownerOfTheirs = await _harness.CreatePersonAsync(theirs, OrganizationRoles.Owner);
        var nobody = new InventoryHarness.Person(Guid.Empty, "nobody@example.com", "not-a-token");

        foreach (var (method, path, body, _) in Endpoints)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _harness.TryAsync(method, path, nobody, body)).Status);
        }

        // Their Owner, naming my organization: not a member there, so no rights at all.
        foreach (var (method, path, body, _) in Endpoints.Take(12))
        {
            var (status, code) = await _harness.TryAsync(method, path, ownerOfTheirs, body, organization: mine);
            Assert.True(status == HttpStatusCode.Forbidden && code == "Auth.PermissionDenied", $"{method} {path} answered {(int)status} {code}");
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private async Task<InventoryHarness.Person> PersonWithOnlyAsync(Guid organizationId, InventoryHarness.Person owner, params string[] permissions) =>
        (await PersonWithOnlyAndRoleAsync(organizationId, owner, permissions)).Person;

    /// <summary>A person whose role (a new custom role) holds exactly <paramref name="permissions"/>, made through the real roles and users APIs.</summary>
    private async Task<(InventoryHarness.Person Person, Guid RoleId)> PersonWithOnlyAndRoleAsync(Guid organizationId, InventoryHarness.Person owner, params string[] permissions)
    {
        var name = "Only " + Guid.NewGuid().ToString("N")[..10];
        Guid roleId;
        using (var created = await _harness.SendAsync(HttpMethod.Post, "/api/v1/roles", owner, new { name, permissions }))
        {
            roleId = (await InventoryHarness.JsonAsync(created, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        }

        var person = await _harness.CreatePersonAsync(organizationId, OrganizationRoles.ReadOnly);
        using (var assigned = await _harness.SendAsync(HttpMethod.Patch, $"/api/v1/users/{person.UserId}/role", owner, new { role = roleId.ToString() }))
        {
            await InventoryHarness.JsonAsync(assigned);
        }

        return (person, roleId);
    }
}
