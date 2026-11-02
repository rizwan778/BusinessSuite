using System.Net;
using System.Text.Json;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Tests.Integration.Inventory;

/// <summary>
/// Audit information for the inventory and catalogue module: every change to products, variants, attributes, categories, units,
/// warehouses and batches is recorded with who did it, in which organization and what changed, in the same transaction as the
/// change. Refused changes leave nothing behind. (Stock documents are audited in <see cref="StockPostingApiTests"/>.)
/// </summary>
public sealed class InventoryAuditTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private readonly InventoryHarness _harness = new(app);

    [SqlServerFact]
    public async Task Every_change_to_setup_data_is_recorded_with_actor_organization_and_what_changed()
    {
        var scene = await _harness.CreateSceneAsync();
        var owner = scene.Owner;
        var baseline = await AuditIdsAsync(scene.OrganizationId);

        async Task<JsonElement> Do(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
        {
            using var response = await _harness.SendAsync(method, path, owner, body);
            if (expected == HttpStatusCode.NoContent)
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
                return default;
            }

            return await InventoryHarness.JsonAsync(response, expected);
        }

        Task<JsonElement> Create(string path, object body) => Do(HttpMethod.Post, path, body, HttpStatusCode.Created);
        object Off() => new { isActive = false };
        object On() => new { isActive = true };

        // ---- Units and conversions
        var box = (await Create("/api/v1/units", new { code = "BOX", name = "Box", decimalPlaces = 0 })).GetProperty("id").GetGuid();
        await Do(HttpMethod.Put, $"/api/v1/units/{box}", new { code = "BOX", name = "Big box", decimalPlaces = 0 });
        await Do(HttpMethod.Patch, $"/api/v1/units/{box}/status", Off());
        await Do(HttpMethod.Patch, $"/api/v1/units/{box}/status", On());
        var conversion = (await Create("/api/v1/units/conversions", new { fromUnitId = box, toUnitId = scene.UnitId, factor = 12m })).GetProperty("id").GetGuid();
        await Do(HttpMethod.Put, $"/api/v1/units/conversions/{conversion}", new { factor = 24m });
        await Do(HttpMethod.Delete, $"/api/v1/units/conversions/{conversion}", null, HttpStatusCode.NoContent);
        await Do(HttpMethod.Delete, $"/api/v1/units/{box}", null, HttpStatusCode.NoContent);

        // ---- Categories
        var category = (await Create("/api/v1/categories", new { name = "Drinks" })).GetProperty("id").GetGuid();
        await Do(HttpMethod.Put, $"/api/v1/categories/{category}", new { name = "Soft drinks" });
        await Do(HttpMethod.Patch, $"/api/v1/categories/{category}/status", Off());
        await Do(HttpMethod.Delete, $"/api/v1/categories/{category}", null, HttpStatusCode.NoContent);

        // ---- Attributes and their options
        var attribute = await Create("/api/v1/product-attributes", new
        {
            code = "colour", name = "Colour", dataType = "Dropdown", scope = "Product", options = new[] { new { label = "Red", sortOrder = 1, isActive = true } },
        });
        var attributeId = attribute.GetProperty("id").GetGuid();
        await Do(HttpMethod.Put, $"/api/v1/product-attributes/{attributeId}", new { name = "Colour of the item" });
        var withBlue = await Do(HttpMethod.Post, $"/api/v1/product-attributes/{attributeId}/options", new { label = "Blue", sortOrder = 2 }, HttpStatusCode.Created);
        var blue = withBlue.GetProperty("options").EnumerateArray().Single(option => option.GetProperty("label").GetString() == "Blue").GetProperty("id").GetGuid();
        await Do(HttpMethod.Put, $"/api/v1/product-attributes/{attributeId}/options/{blue}", new { label = "Blue", sortOrder = 3, isActive = true });
        await Do(HttpMethod.Delete, $"/api/v1/product-attributes/{attributeId}/options/{blue}", null, HttpStatusCode.OK);
        await Do(HttpMethod.Patch, $"/api/v1/product-attributes/{attributeId}/status", Off());
        await Do(HttpMethod.Delete, $"/api/v1/product-attributes/{attributeId}", null, HttpStatusCode.NoContent);

        // ---- Products
        var product = scene.Products[0]; // created by the scene (its audit entry is in the baseline)
        var current = await Do(HttpMethod.Get, $"/api/v1/products/{product}");
        await Do(HttpMethod.Put, $"/api/v1/products/{product}", new
        {
            sku = current.GetProperty("sku").GetString(), name = current.GetProperty("name").GetString(), baseUnitId = scene.UnitId,
            hasVariants = false, tracksStock = true, salePrice = 12m, // was 10
        });
        await Do(HttpMethod.Patch, $"/api/v1/products/{product}/status", Off());
        await Do(HttpMethod.Patch, $"/api/v1/products/{product}/status", On());

        var shirt = (await Create("/api/v1/products", new { sku = "SHIRT", name = "Shirt", baseUnitId = scene.UnitId, hasVariants = true, tracksStock = true })).GetProperty("id").GetGuid();
        var variant = (await Create($"/api/v1/products/{shirt}/variants", new { sku = "SHIRT-M" })).GetProperty("id").GetGuid();
        await Do(HttpMethod.Put, $"/api/v1/products/{shirt}/variants/{variant}", new { sku = "SHIRT-M", name = "Medium", salePrice = 20m });
        await Do(HttpMethod.Patch, $"/api/v1/products/{shirt}/variants/{variant}/status", Off());
        await Do(HttpMethod.Delete, $"/api/v1/products/{shirt}/variants/{variant}", null, HttpStatusCode.NoContent);
        await Do(HttpMethod.Delete, $"/api/v1/products/{shirt}", null, HttpStatusCode.NoContent);

        // ---- Batches
        var milk = (await Create("/api/v1/products", new { sku = "MILK", name = "Milk", baseUnitId = scene.UnitId, hasVariants = false, tracksStock = true, tracksBatches = true })).GetProperty("id").GetGuid();
        var batch = (await Create("/api/v1/batches", new { productId = milk, batchNumber = "LOT-1" })).GetProperty("id").GetGuid();
        await Do(HttpMethod.Patch, $"/api/v1/batches/{batch}/status", Off());
        await Do(HttpMethod.Patch, $"/api/v1/batches/{batch}/status", On());

        // ---- Warehouses
        var second = await _harness.CreateWarehouseAsync(owner, "SECOND");
        await Do(HttpMethod.Put, $"/api/v1/warehouses/{second}", new { code = "SECOND", name = "Second warehouse" });
        await Do(HttpMethod.Post, $"/api/v1/warehouses/{second}/default");
        await Do(HttpMethod.Post, $"/api/v1/warehouses/{scene.WarehouseId}/default");
        await Do(HttpMethod.Patch, $"/api/v1/warehouses/{second}/status", Off());
        await Do(HttpMethod.Patch, $"/api/v1/warehouses/{second}/status", On());
        await Do(HttpMethod.Delete, $"/api/v1/warehouses/{second}", null, HttpStatusCode.NoContent);

        // ---- Refused changes leave no trace
        var beforeRefused = (await AuditIdsAsync(scene.OrganizationId)).Count;
        Assert.Equal((HttpStatusCode.Conflict, "Products.DuplicateSku"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/products", owner,
            new { sku = "MILK", name = "Another milk", baseUnitId = scene.UnitId, hasVariants = false, tracksStock = true }));
        Assert.Equal(HttpStatusCode.NotFound, (await _harness.TryAsync(HttpMethod.Patch, $"/api/v1/products/{Guid.NewGuid()}/status", owner, Off())).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await _harness.TryAsync(HttpMethod.Post, "/api/v1/warehouses", owner, new { code = "MAIN", name = "Duplicate" })).Status);
        Assert.Equal((await AuditIdsAsync(scene.OrganizationId)).Count, beforeRefused);

        // ---- Exactly these entries were written (in the organization's own log)
        string[] expected =
        [
            "unit.created", "unit.updated", "unit.deactivated", "unit.activated", "unit.conversion_created", "unit.conversion_updated", "unit.conversion_deleted", "unit.deleted",
            "category.created", "category.updated", "category.deactivated", "category.deleted",
            "attribute.created", "attribute.updated", "attribute.option_added", "attribute.option_updated", "attribute.option_deleted", "attribute.deactivated", "attribute.deleted",
            "product.updated", "product.deactivated", "product.activated", "product.created", "variant.created", "variant.updated", "variant.deactivated", "variant.deleted",
            "product.deleted", "product.created", "batch.registered", "batch.closed", "batch.activated",
            "warehouse.created", "warehouse.updated", "warehouse.default_changed", "warehouse.default_changed", "warehouse.deactivated", "warehouse.activated", "warehouse.deleted",
        ];
        var written = await _harness.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(entry => entry.OrganizationId == scene.OrganizationId).ToListAsync());
        var added = written.Where(entry => !baseline.Contains(entry.Id)).ToList();
        Assert.Equal(expected.Order(StringComparer.Ordinal), added.Select(entry => entry.Action).Order(StringComparer.Ordinal));

        // Who, where, and which request.
        Assert.All(added, entry =>
        {
            Assert.Equal(scene.Owner.UserId, entry.ActorUserId);
            Assert.Equal(scene.OrganizationId, entry.OrganizationId);
            Assert.False(string.IsNullOrEmpty(entry.CorrelationId), $"{entry.Action} has no correlation id.");
            Assert.NotNull(entry.EntityId);
        });

        // What changed: a price change shows the old and the new value, and only that.
        var update = added.Single(entry => entry.Action == "product.updated");
        Assert.Equal(product, update.EntityId);
        using (var details = JsonDocument.Parse(update.Details!))
        {
            var changes = details.RootElement.GetProperty("changes");
            Assert.Equal(10m, changes.GetProperty("salePrice").GetProperty("from").GetDecimal());
            Assert.Equal(12m, changes.GetProperty("salePrice").GetProperty("to").GetDecimal());
            Assert.Single(changes.EnumerateObject());
        }

        // The deleted things are named in their entries, since they are gone from everywhere else.
        Assert.Contains("SHIRT-M", added.Single(entry => entry.Action == "variant.deleted").Details!, StringComparison.Ordinal);
        Assert.Contains("SECOND", added.Single(entry => entry.Action == "warehouse.deleted").Details!, StringComparison.Ordinal);

        // The audit API shows the organization's own entries to those allowed to read them, and nobody else's.
        var shown = await _harness.GetAsync("/api/v1/audit-logs?entityType=Warehouse&pageSize=100", owner);
        Assert.Contains("warehouse.created", shown.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("action").GetString()));

        var stranger = await _harness.CreateSceneAsync();
        var strangerView = await _harness.GetAsync("/api/v1/audit-logs?pageSize=100", stranger.Owner);
        Assert.DoesNotContain(strangerView.GetProperty("items").EnumerateArray(), item => item.GetProperty("organizationId").ValueKind != JsonValueKind.Null
            && item.GetProperty("organizationId").GetGuid() == scene.OrganizationId);
    }

    private Task<HashSet<Guid>> AuditIdsAsync(Guid organizationId) =>
        _harness.WithDbAsync(async db => (await db.AuditLogs.AsNoTracking().Where(entry => entry.OrganizationId == organizationId).Select(entry => entry.Id).ToListAsync()).ToHashSet());
}
