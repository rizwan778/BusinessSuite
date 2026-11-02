using System.Reflection;
using BusinessSuite.Application.Common.Security;
using BusinessSuite.Web.Framework.Api;
using BusinessSuite.Web.Framework.Security;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Tests.Unit.Architecture;

/// <summary>
/// Structural guarantees of the inventory and catalogue API that hold for every endpoint now and every endpoint added later:
/// each action is guarded by an inventory permission, and no request can carry a tenant (organization) id.
/// </summary>
public sealed class InventoryApiConventionTests
{
    private static readonly string[] ControllerNames =
    [
        "ProductsController", "CategoriesController", "UnitsController", "ProductAttributesController", "WarehousesController",
        "BatchesController", "StockController", "StockAdjustmentsController", "StockTransfersController", "InventoryReportsController",
    ];

    private static readonly Type[] Controllers = typeof(Program).Assembly.GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(AppApiController)) && ControllerNames.Contains(type.Name))
        .ToArray();

    private static IEnumerable<MethodInfo> Actions(Type controller) => controller
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Where(method => !method.IsSpecialName && method.GetCustomAttributes<NonActionAttribute>().Any() == false);

    [Fact]
    public void All_inventory_controllers_were_found()
    {
        Assert.Equal(ControllerNames.Order(), Controllers.Select(controller => controller.Name).Order());
    }

    [Fact]
    public void The_scans_see_the_whole_api_and_really_detect_a_tenant_id()
    {
        // A scan that finds nothing would pass forever: prove it looks at the API, and that it catches what it is looking for.
        Assert.True(Controllers.Sum(controller => Actions(controller).Count()) >= 50, "The inventory API should have 50 or more actions.");

        var found = OrganizationProperties(typeof(SampleRequest), depth: 2).ToList();
        Assert.Contains("SampleRequest.OrganizationId", found);
        Assert.Contains("SampleRequest.Items.SampleLine.TenantId", found); // inside a list of lines
        Assert.Empty(OrganizationProperties(typeof(CleanRequest), depth: 2));
    }

    private sealed class SampleRequest
    {
        public Guid? OrganizationId { get; set; }

        public List<SampleLine>? Items { get; set; }
    }

    private sealed class SampleLine
    {
        public Guid TenantId { get; set; }
    }

    private sealed class CleanRequest
    {
        public Guid? WarehouseId { get; set; }

        public string? Name { get; set; }
    }

    [Fact]
    public void Every_action_requires_an_inventory_permission()
    {
        var unguarded = Controllers
            .SelectMany(controller => Actions(controller).Select(action => (controller, action)))
            .Where(entry =>
            {
                var permissions = entry.action.GetCustomAttributes<RequirePermissionAttribute>().Concat(entry.controller.GetCustomAttributes<RequirePermissionAttribute>()).ToList();
                return permissions.Count == 0 || permissions.Any(permission => !permission.Permission.StartsWith("inventory.", StringComparison.Ordinal));
            })
            .Select(entry => $"{entry.controller.Name}.{entry.action.Name}")
            .ToList();

        // Being signed in is not enough: an action without a permission would be open to every member of every organization.
        Assert.True(unguarded.Count == 0, $"These actions are not guarded by an inventory permission: {string.Join(", ", unguarded)}");
    }

    [Fact]
    public void No_inventory_action_is_anonymous()
    {
        Assert.All(Controllers, controller =>
        {
            Assert.Empty(controller.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
            Assert.All(Actions(controller), action => Assert.Empty(action.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>()));
        });
    }

    [Fact]
    public void No_request_can_carry_an_organization()
    {
        // The tenant is the one the server resolved for the signed-in user. Neither an action parameter nor any property of a request
        // model (looked at two levels deep) may be called anything like "organization".
        var offenders = new List<string>();
        foreach (var controller in Controllers)
        {
            foreach (var action in Actions(controller))
            {
                foreach (var parameter in action.GetParameters())
                {
                    if (IsOrganization(parameter.Name))
                    {
                        offenders.Add($"{controller.Name}.{action.Name}({parameter.Name})");
                    }

                    foreach (var path in OrganizationProperties(parameter.ParameterType, depth: 2))
                    {
                        offenders.Add($"{controller.Name}.{action.Name}({parameter.Name}) -> {path}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, $"Requests must not carry a tenant id: {string.Join("; ", offenders)}");
    }

    private static bool IsOrganization(string? name) => name is not null && name.Contains("organization", StringComparison.OrdinalIgnoreCase)
        || name is not null && name.Contains("tenant", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> OrganizationProperties(Type type, int depth)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (depth == 0 || type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type == typeof(decimal) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly) || type == typeof(CancellationToken) || type.IsEnum)
        {
            yield break;
        }

        var element = type.IsArray ? type.GetElementType() : type.IsGenericType ? type.GetGenericArguments().FirstOrDefault() : null;
        if (element is not null)
        {
            foreach (var path in OrganizationProperties(element, depth))
            {
                yield return path;
            }

            yield break;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (IsOrganization(property.Name))
            {
                yield return $"{type.Name}.{property.Name}";
            }

            foreach (var nested in OrganizationProperties(property.PropertyType, depth - 1))
            {
                yield return $"{type.Name}.{property.Name}.{nested}"; // e.g. CreateRequest.Items.Line.OrganizationId
            }
        }
    }
}
