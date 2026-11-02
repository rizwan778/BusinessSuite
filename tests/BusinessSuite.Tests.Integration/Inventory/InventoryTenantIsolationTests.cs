using System.Net;
using System.Text.Json;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Tests.Integration.Inventory;

/// <summary>
/// Tenant isolation and ownership across the whole inventory and catalogue API. Organization A owns a rich set of records
/// (categories, units, attributes, simple and variant products, batches, documents); a signed-in Owner of organization B throws
/// every request they can think of at them. Nothing of A's may be read, found in a list, changed, or referenced.
/// </summary>
public sealed class InventoryTenantIsolationTests(SqlServerAppFixture app) : IClassFixture<SqlServerAppFixture>
{
    private readonly InventoryHarness _harness = new(app);

    /// <summary>Everything organization A owns.</summary>
    private sealed record Rich(
        InventoryHarness.Scene Scene, Guid Category, Guid ExtraUnit, Guid Conversion, Guid Attribute, Guid Option,
        Guid VariantProduct, Guid Variant, Guid BatchProduct, Guid Batch, Guid SecondWarehouse, Guid Adjustment, Guid Transfer)
    {
        public Guid Unit => Scene.UnitId;

        public Guid Warehouse => Scene.WarehouseId;

        public Guid Product => Scene.Products[0];

        public IEnumerable<Guid> AllIds => new[]
        {
            Scene.OrganizationId, Category, ExtraUnit, Conversion, Attribute, Option, VariantProduct, Variant, BatchProduct, Batch,
            SecondWarehouse, Adjustment, Transfer, Unit, Warehouse,
        }.Concat(Scene.Products);
    }

    private async Task<Rich> SeedRichAsync()
    {
        var scene = await _harness.CreateSceneAsync();
        var owner = scene.Owner;

        async Task<Guid> CreateAsync(string path, object body)
        {
            using var response = await _harness.SendAsync(HttpMethod.Post, path, owner, body);
            return (await InventoryHarness.JsonAsync(response, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        }

        var category = await CreateAsync("/api/v1/categories", new { name = "Drinks" });
        var extraUnit = await CreateAsync("/api/v1/units", new { code = "BOX", name = "Box", decimalPlaces = 0 });
        var conversion = await CreateAsync("/api/v1/units/conversions", new { fromUnitId = extraUnit, toUnitId = scene.UnitId, factor = 12m });

        using var attributeResponse = await _harness.SendAsync(HttpMethod.Post, "/api/v1/product-attributes", owner, new
        {
            code = "colour", name = "Colour", dataType = "Dropdown", scope = "Product", options = new[] { new { label = "Red", sortOrder = 1, isActive = true } },
        });
        var attributeJson = await InventoryHarness.JsonAsync(attributeResponse, HttpStatusCode.Created);

        var variantProduct = await CreateAsync("/api/v1/products", new { sku = "VAR-1", name = "Shirt", baseUnitId = scene.UnitId, hasVariants = true, tracksStock = true });
        using var variantResponse = await _harness.SendAsync(HttpMethod.Post, $"/api/v1/products/{variantProduct}/variants", owner, new { sku = "VAR-1-M" });
        var variant = (await InventoryHarness.JsonAsync(variantResponse, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var batchProduct = await CreateAsync("/api/v1/products", new { sku = "BAT-1", name = "Milk", baseUnitId = scene.UnitId, hasVariants = false, tracksStock = true, tracksBatches = true });
        var batch = await CreateAsync("/api/v1/batches", new { productId = batchProduct, batchNumber = "LOT-1" });

        var secondWarehouse = await _harness.CreateWarehouseAsync(owner, "SECOND");
        var adjustment = (await _harness.CreateAdjustmentAsync(owner, scene.WarehouseId, (scene.Products[1], 5, 2m))).GetProperty("id").GetGuid();
        var transfer = (await _harness.CreateTransferAsync(owner, scene.WarehouseId, secondWarehouse, (scene.Products[1], 1))).GetProperty("id").GetGuid();

        // Some real stock, so balances and the ledger have content to leak.
        await _harness.PostAdjustmentAsync(owner, scene.WarehouseId, (scene.Products[0], 10, 3m));

        return new Rich(scene, category, extraUnit, conversion, attributeJson.GetProperty("id").GetGuid(),
            attributeJson.GetProperty("options")[0].GetProperty("id").GetGuid(), variantProduct, variant, batchProduct, batch, secondWarehouse, adjustment, transfer);
    }

    // ---- Nothing of A's can be reached by B ---------------------------------------------------------------------

    [SqlServerFact]
    public async Task Another_organizations_records_are_not_found_not_changed_and_not_listed()
    {
        var a = await SeedRichAsync();
        var b = await _harness.CreateSceneAsync();
        var intruder = b.Owner;

        // Everything A shows the world, as A's owner sees it, before B tries anything.
        string[] watched =
        [
            $"/api/v1/products/{a.Product}", $"/api/v1/products/{a.VariantProduct}/variants", $"/api/v1/categories/{a.Category}", $"/api/v1/units/{a.Unit}",
            $"/api/v1/units/conversions/{a.Conversion}", $"/api/v1/product-attributes/{a.Attribute}", $"/api/v1/warehouses/{a.Warehouse}", $"/api/v1/batches/{a.Batch}",
            $"/api/v1/stock-adjustments/{a.Adjustment}", $"/api/v1/stock-transfers/{a.Transfer}", "/api/v1/stock", "/api/v1/stock-movements",
        ];
        var before = new List<string>();
        foreach (var path in watched)
        {
            before.Add(await GetTextAsync(path, a.Scene.Owner));
        }

        // 1. Reading A's records by id: not found, for every kind of record.
        var reads = new[]
        {
            $"/api/v1/products/{a.Product}", $"/api/v1/products/{a.VariantProduct}/variants", $"/api/v1/products/{a.VariantProduct}/variants/{a.Variant}",
            $"/api/v1/categories/{a.Category}", $"/api/v1/units/{a.Unit}", $"/api/v1/units/conversions/{a.Conversion}", $"/api/v1/product-attributes/{a.Attribute}",
            $"/api/v1/warehouses/{a.Warehouse}", $"/api/v1/batches/{a.Batch}", $"/api/v1/batches/{a.Batch}/stock",
            $"/api/v1/stock-adjustments/{a.Adjustment}", $"/api/v1/stock-transfers/{a.Transfer}",
            $"/api/v1/product-attributes/schema?categoryId={a.Category}", // asking for the form of A's category
            $"/api/v1/inventory/reports/valuation?warehouseId={a.Warehouse}", // valuing A's warehouse
        };
        foreach (var path in reads)
        {
            var (status, _) = await _harness.TryAsync(HttpMethod.Get, path, intruder);
            Assert.True(status == HttpStatusCode.NotFound, $"GET {path} answered {status} instead of 404.");
        }

        // 2. Changing A's records: not found, for every kind of change.
        var product = new { sku = "HIJACK", name = "Hijacked", baseUnitId = b.UnitId, hasVariants = false, tracksStock = true };
        var off = new { isActive = false };
        var changes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Put, $"/api/v1/products/{a.Product}", product),
            (HttpMethod.Patch, $"/api/v1/products/{a.Product}/status", off),
            (HttpMethod.Delete, $"/api/v1/products/{a.Product}", null),
            (HttpMethod.Post, $"/api/v1/products/{a.VariantProduct}/variants", new { sku = "HIJACK-V" }),
            (HttpMethod.Put, $"/api/v1/products/{a.VariantProduct}/variants/{a.Variant}", new { sku = "HIJACK-V" }),
            (HttpMethod.Patch, $"/api/v1/products/{a.VariantProduct}/variants/{a.Variant}/status", off),
            (HttpMethod.Delete, $"/api/v1/products/{a.VariantProduct}/variants/{a.Variant}", null),
            (HttpMethod.Put, $"/api/v1/categories/{a.Category}", new { name = "Hijacked" }),
            (HttpMethod.Patch, $"/api/v1/categories/{a.Category}/status", off),
            (HttpMethod.Delete, $"/api/v1/categories/{a.Category}", null),
            (HttpMethod.Put, $"/api/v1/units/{a.ExtraUnit}", new { code = "HJ", name = "Hijacked", decimalPlaces = 0 }),
            (HttpMethod.Patch, $"/api/v1/units/{a.ExtraUnit}/status", off),
            (HttpMethod.Delete, $"/api/v1/units/{a.ExtraUnit}", null),
            (HttpMethod.Put, $"/api/v1/units/conversions/{a.Conversion}", new { factor = 99m }),
            (HttpMethod.Delete, $"/api/v1/units/conversions/{a.Conversion}", null),
            (HttpMethod.Put, $"/api/v1/product-attributes/{a.Attribute}", new { name = "Hijacked" }),
            (HttpMethod.Patch, $"/api/v1/product-attributes/{a.Attribute}/status", off),
            (HttpMethod.Delete, $"/api/v1/product-attributes/{a.Attribute}", null),
            (HttpMethod.Post, $"/api/v1/product-attributes/{a.Attribute}/options", new { label = "Planted" }),
            (HttpMethod.Put, $"/api/v1/product-attributes/{a.Attribute}/options/{a.Option}", new { label = "Planted" }),
            (HttpMethod.Delete, $"/api/v1/product-attributes/{a.Attribute}/options/{a.Option}", null),
            (HttpMethod.Put, $"/api/v1/warehouses/{a.Warehouse}", new { code = "HJ", name = "Hijacked" }),
            (HttpMethod.Patch, $"/api/v1/warehouses/{a.Warehouse}/status", off),
            (HttpMethod.Post, $"/api/v1/warehouses/{a.SecondWarehouse}/default", null),
            (HttpMethod.Delete, $"/api/v1/warehouses/{a.SecondWarehouse}", null),
            (HttpMethod.Patch, $"/api/v1/batches/{a.Batch}/status", off),
            (HttpMethod.Post, $"/api/v1/stock-adjustments/{a.Adjustment}/post", null),
            (HttpMethod.Post, $"/api/v1/stock-adjustments/{a.Adjustment}/cancel", null),
            (HttpMethod.Post, $"/api/v1/stock-transfers/{a.Transfer}/post", null),
            (HttpMethod.Post, $"/api/v1/stock-transfers/{a.Transfer}/cancel", null),
        };
        foreach (var (method, path, body) in changes)
        {
            var (status, _) = await _harness.TryAsync(method, path, intruder, body);
            Assert.True(status == HttpStatusCode.NotFound, $"{method} {path} answered {status} instead of 404.");
        }

        // 3. Lists show B's own records only: none of A's ids appear anywhere in what B can read.
        var foreignIds = a.AllIds.Select(id => id.ToString()).ToArray();
        string[] lists =
        [
            "/api/v1/products", "/api/v1/products?search=Shirt", $"/api/v1/products?categoryId={a.Category}", "/api/v1/categories", "/api/v1/categories/tree",
            $"/api/v1/categories?parentCategoryId={a.Category}", "/api/v1/units", "/api/v1/units/conversions", $"/api/v1/units/conversions?unitId={a.Unit}",
            "/api/v1/product-attributes", "/api/v1/product-attributes/schema", "/api/v1/warehouses", "/api/v1/warehouses/default",
            "/api/v1/batches", $"/api/v1/batches?productId={a.BatchProduct}", "/api/v1/stock", $"/api/v1/stock?productId={a.Product}",
            $"/api/v1/stock?warehouseId={a.Warehouse}", "/api/v1/stock/low", "/api/v1/stock/out-of-stock", "/api/v1/stock-movements",
            $"/api/v1/stock-movements?productId={a.Product}", $"/api/v1/stock-movements?warehouseId={a.Warehouse}", "/api/v1/stock-adjustments",
            "/api/v1/stock-transfers", "/api/v1/inventory/dashboard", "/api/v1/inventory/reports/valuation",
            "/api/v1/inventory/reports/stock-by-warehouse", "/api/v1/inventory/reports/stock-by-product",
        ];
        foreach (var path in lists)
        {
            var text = await GetTextAsync(path, intruder);
            foreach (var id in foreignIds)
            {
                Assert.False(text.Contains(id, StringComparison.OrdinalIgnoreCase), $"GET {path} leaked {id}: {text[..Math.Min(300, text.Length)]}");
            }
        }

        var dashboard = await _harness.GetAsync("/api/v1/inventory/dashboard", intruder);
        Assert.Equal(b.Products.Count, dashboard.GetProperty("totalProducts").GetInt32()); // B's two, not A's five
        Assert.Equal(1, dashboard.GetProperty("totalWarehouses").GetInt32());

        // 4. A's data is exactly as it was.
        for (var index = 0; index < watched.Length; index++)
        {
            Assert.Equal(before[index], await GetTextAsync(watched[index], a.Scene.Owner));
        }

        Assert.Equal(10, await _harness.OnHandAsync(a.Warehouse, a.Product));
        await _harness.AssertLedgerMatchesBalancesAsync(a.Scene.OrganizationId);
    }

    // ---- The tenant comes from the server, never from the request -------------------------------------------

    [SqlServerFact]
    public async Task A_client_supplied_organization_is_ignored_or_refused_everywhere()
    {
        var a = await SeedRichAsync();
        var b = await _harness.CreateSceneAsync();

        // 1. An organization id in the body is not part of any request: it is ignored, the record lands in B's own organization.
        using var created = await _harness.SendAsync(HttpMethod.Post, "/api/v1/products", b.Owner, new
        {
            sku = "SMUGGLED", name = "Smuggled", baseUnitId = b.UnitId, hasVariants = false, tracksStock = true, organizationId = a.Scene.OrganizationId,
        });
        var productId = (await InventoryHarness.JsonAsync(created, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var owner = await _harness.WithDbAsync(db => db.Products.AsNoTracking().Where(product => product.Id == productId).Select(product => product.OrganizationId).SingleAsync());
        Assert.Equal(b.OrganizationId, owner);

        // 2. An organization id in the query string does not widen what a list returns.
        var list = await GetTextAsync($"/api/v1/products?organizationId={a.Scene.OrganizationId}", b.Owner);
        Assert.DoesNotContain(a.Product.ToString(), list, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SMUGGLED", list, StringComparison.Ordinal);

        // 3. Naming A in the X-Organization-Id header gets B's Owner nothing at all: the server checks membership, finds none, and
        //    grants no inventory permission in that organization.
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "/api/v1/products", null),
                     (HttpMethod.Get, "/api/v1/stock", null),
                     (HttpMethod.Get, "/api/v1/inventory/dashboard", null),
                     (HttpMethod.Post, "/api/v1/stock-adjustments", new { warehouseId = a.Warehouse, reason = "StockCount", items = new[] { new { productId = a.Product, quantityDifference = 1m } } }),
                     (HttpMethod.Post, $"/api/v1/stock-adjustments/{a.Adjustment}/post", null),
                     (HttpMethod.Post, "/api/v1/stock-transfers", new { fromWarehouseId = a.Warehouse, toWarehouseId = a.SecondWarehouse, items = new[] { new { productId = a.Product, quantity = 1m } } }),
                 })
        {
            var (status, code) = await _harness.TryAsync(method, path, b.Owner, body, organization: a.Scene.OrganizationId);
            Assert.True(status == HttpStatusCode.Forbidden && code == "Auth.PermissionDenied", $"{method} {path} with A's header answered {status} {code}.");
        }

        Assert.Equal(10, await _harness.OnHandAsync(a.Warehouse, a.Product));
        Assert.Equal(1, await _harness.MovementCountAsync(a.Scene.OrganizationId));
    }

    [SqlServerFact]
    public async Task A_super_admin_must_name_an_organization_and_is_then_confined_to_it()
    {
        var a = await SeedRichAsync();
        var b = await _harness.CreateSceneAsync();
        var superAdmin = await _harness.CreatePlatformPersonAsync("SuperAdmin");

        // No organization selected: there is no tenant to work in, so nothing is readable or writable.
        Assert.Equal((HttpStatusCode.BadRequest, "Inventory.OrganizationRequired"), await _harness.TryAsync(HttpMethod.Get, "/api/v1/products", superAdmin));
        Assert.Equal((HttpStatusCode.BadRequest, "Inventory.OrganizationRequired"), await _harness.TryAsync(HttpMethod.Get, "/api/v1/stock", superAdmin));

        // Naming A: A's data and only A's data.
        var inA = await GetTextAsync("/api/v1/products", superAdmin, a.Scene.OrganizationId);
        Assert.Contains(a.Product.ToString(), inA, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b.Products[0].ToString(), inA, StringComparison.OrdinalIgnoreCase);

        // ...and B's records are still not found through A.
        var (status, _) = await _harness.TryAsync(HttpMethod.Get, $"/api/v1/products/{b.Products[0]}", superAdmin, organization: a.Scene.OrganizationId);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---- Ownership of what a request refers to -----------------------------------------------------------------

    [SqlServerFact]
    public async Task Requests_cannot_refer_to_another_organizations_products_variants_attributes_warehouses_or_batches()
    {
        var a = await SeedRichAsync();
        var b = await _harness.CreateSceneAsync();
        var owner = b.Owner;
        var ownProduct = b.Products[0];

        // Their category, unit or parent as MY product's references.
        Assert.Equal((HttpStatusCode.BadRequest, "Products.ReferenceInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/products", owner,
            new { sku = "X1", name = "X", baseUnitId = b.UnitId, categoryId = a.Category, hasVariants = false, tracksStock = true }));
        Assert.Equal((HttpStatusCode.BadRequest, "Products.ReferenceInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/products", owner,
            new { sku = "X2", name = "X", baseUnitId = a.Unit, hasVariants = false, tracksStock = true }));
        Assert.Equal((HttpStatusCode.BadRequest, "Categories.Invalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/categories", owner,
            new { name = "Child", parentCategoryId = a.Category }));
        Assert.Equal((HttpStatusCode.BadRequest, "Attributes.Invalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/product-attributes", owner,
            new { code = "size", name = "Size", dataType = "Text", scope = "Product", categoryId = a.Category }));

        // Their attribute (by code, which only exists in A) and their option (by id, on an attribute of mine).
        Assert.Equal((HttpStatusCode.BadRequest, "Products.AttributeValuesInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/products", owner,
            new { sku = "X3", name = "X", baseUnitId = b.UnitId, hasVariants = false, tracksStock = true, attributes = new[] { new { code = "colour", optionId = a.Option } } }));
        using (var mine = await _harness.SendAsync(HttpMethod.Post, "/api/v1/product-attributes", owner, new
        {
            code = "colour", name = "Colour", dataType = "Dropdown", scope = "Product", options = new[] { new { label = "Blue", sortOrder = 1, isActive = true } },
        }))
        {
            await InventoryHarness.JsonAsync(mine, HttpStatusCode.Created); // same code in B is a different attribute
        }

        Assert.Equal((HttpStatusCode.BadRequest, "Products.AttributeValuesInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/products", owner,
            new { sku = "X4", name = "X", baseUnitId = b.UnitId, hasVariants = false, tracksStock = true, attributes = new[] { new { code = "colour", optionId = a.Option } } }));

        // A variant under their product, a batch of their product.
        Assert.Equal((HttpStatusCode.NotFound, "Products.NotFound"), await _harness.TryAsync(HttpMethod.Post, $"/api/v1/products/{a.VariantProduct}/variants", owner, new { sku = "X5" }));
        Assert.Equal((HttpStatusCode.NotFound, "Products.NotFound"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/batches", owner, new { productId = a.BatchProduct, batchNumber = "LOT-X" }));

        // Their conversion units, their warehouse as MY warehouse's... and their records on MY stock documents.
        Assert.Equal(HttpStatusCode.NotFound, (await _harness.TryAsync(HttpMethod.Post, "/api/v1/units/conversions", owner,
            new { fromUnitId = a.ExtraUnit, toUnitId = b.UnitId, factor = 2m })).Status);
        Assert.Equal((HttpStatusCode.NotFound, "Warehouses.NotFound"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = a.Warehouse, reason = "StockCount", items = new[] { new { productId = ownProduct, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = b.WarehouseId, reason = "StockCount", items = new[] { new { productId = a.Product, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = b.WarehouseId, reason = "StockCount", items = new[] { new { productId = ownProduct, variantId = a.Variant, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = b.WarehouseId, reason = "StockCount", items = new[] { new { productId = ownProduct, batchId = a.Batch, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.NotFound, "Warehouses.NotFound"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-transfers", owner,
            new { fromWarehouseId = b.WarehouseId, toWarehouseId = a.Warehouse, items = new[] { new { productId = ownProduct, quantity = 1m } } }));
        Assert.Equal((HttpStatusCode.NotFound, "Warehouses.NotFound"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-transfers", owner,
            new { fromWarehouseId = a.Warehouse, toWarehouseId = b.WarehouseId, items = new[] { new { productId = ownProduct, quantity = 1m } } }));

        // Nothing was created in either organization by any of the above except the attribute B made on purpose.
        Assert.Equal(0, await _harness.MovementCountAsync(b.OrganizationId));
        Assert.Equal(1, await _harness.MovementCountAsync(a.Scene.OrganizationId));
        Assert.Equal(b.Products.Count, await _harness.WithDbAsync(db => db.Products.AsNoTracking().CountAsync(product => product.OrganizationId == b.OrganizationId)));
    }

    [SqlServerFact]
    public async Task Inside_one_organization_a_variant_or_batch_belongs_to_its_own_product_only()
    {
        var a = await SeedRichAsync();
        var owner = a.Scene.Owner;

        // A second product with variants and a second batch-tracking product, in the SAME organization.
        using var otherProduct = await _harness.SendAsync(HttpMethod.Post, "/api/v1/products", owner,
            new { sku = "VAR-2", name = "Trousers", baseUnitId = a.Unit, hasVariants = true, tracksStock = true });
        var variantProduct2 = (await InventoryHarness.JsonAsync(otherProduct, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using var otherBatchProduct = await _harness.SendAsync(HttpMethod.Post, "/api/v1/products", owner,
            new { sku = "BAT-2", name = "Cheese", baseUnitId = a.Unit, hasVariants = false, tracksStock = true, tracksBatches = true });
        var batchProduct2 = (await InventoryHarness.JsonAsync(otherBatchProduct, HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Variant V of product 1 is not a variant of product 2; a batch of product 1 is not a batch of product 2.
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = a.Warehouse, reason = "StockCount", items = new[] { new { productId = variantProduct2, variantId = a.Variant, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = a.Warehouse, reason = "StockCount", items = new[] { new { productId = batchProduct2, batchId = a.Batch, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.NotFound, "Variants.NotFound"), await _harness.TryAsync(HttpMethod.Get, $"/api/v1/products/{variantProduct2}/variants/{a.Variant}", owner));

        // A product with variants needs one; a product without variants or batches refuses them.
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = a.Warehouse, reason = "StockCount", items = new[] { new { productId = a.VariantProduct, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = a.Warehouse, reason = "StockCount", items = new[] { new { productId = a.Product, batchId = a.Batch, quantityDifference = 1m } } }));
        Assert.Equal((HttpStatusCode.BadRequest, "Stock.ItemInvalid"), await _harness.TryAsync(HttpMethod.Post, "/api/v1/stock-adjustments", owner,
            new { warehouseId = a.Warehouse, reason = "StockCount", items = new[] { new { productId = a.BatchProduct, quantityDifference = 1m } } })); // a batch product needs its batch
        Assert.Equal(1, await _harness.MovementCountAsync(a.Scene.OrganizationId));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private async Task<string> GetTextAsync(string path, InventoryHarness.Person who, Guid? organization = null)
    {
        using var response = await _harness.SendAsync(HttpMethod.Get, path, who, null, organization);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {response.StatusCode}: {text}");
        return text;
    }
}
