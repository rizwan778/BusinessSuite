using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Domain.Organizations;
using BusinessSuite.Infrastructure.Persistence;
using BusinessSuite.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BusinessSuite.Tests.Integration.Persistence;

/// <summary>
/// The catalogue and inventory schema on a real SQL Server (docs/INVENTORY_DATABASE.md): tenant isolation enforced by the
/// database, uniqueness, typed attribute values, the append-only ledger, protected documents and concurrency control.
/// Each test proves the DATABASE refuses the bad data, whatever the application does.
/// </summary>
public sealed class InventorySchemaTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private const int ConstraintViolation = 547;       // foreign key or check constraint
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    // ---- Tenant isolation: the database refuses references across organizations -----------------------------------

    [SqlServerFact]
    public async Task A_product_cannot_use_another_organizations_category_or_unit()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        await using var db = database.CreateDbContext();
        var categoryOfA = ProductCategory.Create(a.OrganizationId, null, null, "Beverages", null);
        db.ProductCategories.Add(categoryOfA);
        await db.SaveChangesAsync();

        // B's product with A's category.
        db.Products.Add(Product.Create(b.OrganizationId, Spec(b, "S-1", categoryOfA.Id)));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_Products_ProductCategories_OrganizationId_CategoryId");
        db.ChangeTracker.Clear();

        // B's product with A's base unit.
        db.Products.Add(Product.Create(b.OrganizationId, Spec(b, "S-2") with { BaseUnitId = a.UnitId }));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_Products_UnitsOfMeasure_OrganizationId_BaseUnitId");
    }

    [SqlServerFact]
    public async Task Stock_cannot_be_held_in_or_for_another_organizations_warehouse_or_product()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        await using var db = database.CreateDbContext();

        // B's balance in A's warehouse.
        db.InventoryBalances.Add(InventoryBalance.Open(b.OrganizationId, a.WarehouseId, b.ProductId, null, null));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_InventoryBalances_Warehouses_OrganizationId_WarehouseId");
        db.ChangeTracker.Clear();

        // A's movement for B's product.
        db.StockMovements.Add(StockMovement.Create(a.OrganizationId, a.WarehouseId, b.ProductId, null, null, StockMovementType.Receipt, 1, 1, Now));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_StockMovements_Products_OrganizationId_ProductId");
    }

    [SqlServerFact]
    public async Task A_variant_and_a_batch_must_belong_to_the_product_the_stock_is_for()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        var styleA = Product.Create(tenant.OrganizationId, Spec(tenant, "STYLE-A") with { HasVariants = true, TracksBatches = true });
        var styleB = Product.Create(tenant.OrganizationId, Spec(tenant, "STYLE-B") with { HasVariants = true, TracksBatches = true });
        db.Products.AddRange(styleA, styleB);
        var variantOfA = ProductVariant.Create(tenant.OrganizationId, styleA.Id, new ProductVariantSpec("A-BLU-L", null, null));
        var batchOfA = ProductBatch.Create(tenant.OrganizationId, styleA.Id, null, "LOT-1", null, null, false);
        db.AddRange(variantOfA, batchOfA);
        await db.SaveChangesAsync();

        // Style B's stock pointing at style A's variant, then at style A's batch.
        db.InventoryBalances.Add(InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, styleB.Id, variantOfA.Id, null));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_InventoryBalances_ProductVariants");
        db.ChangeTracker.Clear();
        db.InventoryBalances.Add(InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, styleB.Id, null, batchOfA.Id));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_InventoryBalances_ProductBatches");
        db.ChangeTracker.Clear();

        // Its own variant and batch are accepted.
        db.InventoryBalances.Add(InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, styleA.Id, variantOfA.Id, batchOfA.Id));
        await db.SaveChangesAsync();
    }

    // ---- Uniqueness --------------------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Skus_and_barcodes_are_unique_per_organization_only_and_missing_barcodes_never_clash()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        await using var db = database.CreateDbContext();
        db.Products.AddRange(
            Product.Create(a.OrganizationId, Spec(a, "SKU-1") with { Barcode = "890100" }),
            Product.Create(a.OrganizationId, Spec(a, "SKU-2")),
            Product.Create(a.OrganizationId, Spec(a, "SKU-3")),  // several products without a barcode
            Product.Create(b.OrganizationId, Spec(b, "SKU-1") with { Barcode = "890100" })); // the same values in another organization
        await db.SaveChangesAsync();

        db.Products.Add(Product.Create(a.OrganizationId, Spec(a, "sku-1")));  // case-insensitive collation
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_Products_OrganizationId_Sku");
        db.ChangeTracker.Clear();

        db.Products.Add(Product.Create(a.OrganizationId, Spec(a, "SKU-9") with { Barcode = "890100" }));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_Products_OrganizationId_Barcode");
    }

    [SqlServerFact]
    public async Task There_is_one_balance_per_warehouse_product_variant_and_batch_even_when_variant_and_batch_are_null()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        db.InventoryBalances.Add(InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null));
        await db.SaveChangesAsync();

        db.InventoryBalances.Add(InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_InventoryBalances");
    }

    [SqlServerFact]
    public async Task Units_conversions_and_batch_numbers_are_unique_and_checked()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        var carton = UnitOfMeasure.Create(tenant.OrganizationId, "CTN", "Carton", 0);
        db.UnitsOfMeasure.Add(carton);
        await db.SaveChangesAsync();
        db.UnitConversions.Add(UnitConversion.Create(tenant.OrganizationId, tenant.ProductId, carton.Id, tenant.UnitId, 24));
        db.ProductBatches.Add(ProductBatch.Create(tenant.OrganizationId, tenant.ProductId, null, "LOT-1", null, null, false));
        await db.SaveChangesAsync();

        db.UnitConversions.Add(UnitConversion.Create(tenant.OrganizationId, tenant.ProductId, carton.Id, tenant.UnitId, 12));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_UnitConversions");
        db.ChangeTracker.Clear();

        db.ProductBatches.Add(ProductBatch.Create(tenant.OrganizationId, tenant.ProductId, null, "lot-1", null, null, false));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_ProductBatches");
        db.ChangeTracker.Clear();

        // A conversion with a zero factor, and one from a unit to itself, never get past the domain, but the database refuses them too.
        await AssertRefusedAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [catalog].[UnitConversions] ([Id],[OrganizationId],[ProductId],[FromUnitId],[ToUnitId],[Factor],[CreatedAtUtc])
            VALUES (NEWID(), {tenant.OrganizationId}, NULL, {carton.Id}, {tenant.UnitId}, 0, SYSDATETIMEOFFSET())
            """),
            ConstraintViolation, "CK_UnitConversions_Factor");
        await AssertRefusedAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [catalog].[UnitConversions] ([Id],[OrganizationId],[ProductId],[FromUnitId],[ToUnitId],[Factor],[CreatedAtUtc])
            VALUES (NEWID(), {tenant.OrganizationId}, NULL, {carton.Id}, {carton.Id}, 2, SYSDATETIMEOFFSET())
            """),
            ConstraintViolation, "CK_UnitConversions_DifferentUnits");
    }

    // ---- Configurable attributes -------------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Attribute_values_of_every_data_type_are_stored_in_typed_columns_without_schema_changes()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        var brand = Define(tenant, "brand", AttributeDataType.Dropdown, AttributeScope.Product);
        var tags = Define(tenant, "tags", AttributeDataType.MultiSelect, AttributeScope.Product);
        var model = Define(tenant, "model", AttributeDataType.Text, AttributeScope.Product);
        var ram = Define(tenant, "ram_gb", AttributeDataType.Number, AttributeScope.Product);
        var volume = Define(tenant, "volume_ml", AttributeDataType.Decimal, AttributeScope.Product);
        var launched = Define(tenant, "launched", AttributeDataType.Date, AttributeScope.Product);
        var waterproof = Define(tenant, "waterproof", AttributeDataType.Boolean, AttributeScope.Product);
        db.ProductAttributeDefinitions.AddRange(brand, tags, model, ram, volume, launched, waterproof);
        await db.SaveChangesAsync();
        var samsung = ProductAttributeOption.Create(brand, "Samsung");
        var (wireless, budget) = (ProductAttributeOption.Create(tags, "Wireless"), ProductAttributeOption.Create(tags, "Budget"));
        db.ProductAttributeOptions.AddRange(samsung, wireless, budget);
        await db.SaveChangesAsync();

        var product = tenant.ProductId;
        db.ProductAttributeValues.AddRange(
            ProductAttributeValue.Create(product, brand, new AttributeValueInput(OptionId: samsung.Id)),
            ProductAttributeValue.Create(product, tags, new AttributeValueInput(OptionId: wireless.Id)),
            ProductAttributeValue.Create(product, tags, new AttributeValueInput(OptionId: budget.Id)),   // MultiSelect: several rows
            ProductAttributeValue.Create(product, model, new AttributeValueInput(Text: "Galaxy S")),
            ProductAttributeValue.Create(product, ram, new AttributeValueInput(Number: 8)),
            ProductAttributeValue.Create(product, volume, new AttributeValueInput(Number: 330.5m)),
            ProductAttributeValue.Create(product, launched, new AttributeValueInput(Date: new DateOnly(2026, 2, 25))),
            ProductAttributeValue.Create(product, waterproof, new AttributeValueInput(Boolean: true)));
        await db.SaveChangesAsync();

        // Typed columns can be filtered in SQL: "phones with at least 8 GB RAM launched this year".
        var matches = await db.ProductAttributeValues.CountAsync(value =>
            value.ProductId == product && value.AttributeDefinitionId == ram.Id && value.NumberValue >= 8);
        var tagCount = await db.ProductAttributeValues.CountAsync(value => value.ProductId == product && value.AttributeDefinitionId == tags.Id);
        var launchedIn2026 = await db.ProductAttributeValues.CountAsync(value => value.AttributeDefinitionId == launched.Id && value.DateValue >= new DateOnly(2026, 1, 1));

        Assert.Equal(1, matches);
        Assert.Equal(2, tagCount);
        Assert.Equal(1, launchedIn2026);
    }

    [SqlServerFact]
    public async Task A_dropdown_has_one_value_a_multiselect_cannot_repeat_an_option_and_one_column_is_filled()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        var brand = Define(tenant, "brand", AttributeDataType.Dropdown, AttributeScope.Product);
        var tags = Define(tenant, "tags", AttributeDataType.MultiSelect, AttributeScope.Product);
        var note = Define(tenant, "note", AttributeDataType.Text, AttributeScope.Product); // no value yet: only the check can refuse
        db.ProductAttributeDefinitions.AddRange(brand, tags, note);
        await db.SaveChangesAsync();
        var (samsung, apple) = (ProductAttributeOption.Create(brand, "Samsung"), ProductAttributeOption.Create(brand, "Apple"));
        var wireless = ProductAttributeOption.Create(tags, "Wireless");
        db.ProductAttributeOptions.AddRange(samsung, apple, wireless);
        db.ProductAttributeValues.AddRange(
            ProductAttributeValue.Create(tenant.ProductId, brand, new AttributeValueInput(OptionId: samsung.Id)),
            ProductAttributeValue.Create(tenant.ProductId, tags, new AttributeValueInput(OptionId: wireless.Id)));
        await db.SaveChangesAsync();

        // A second brand for the same product.
        db.ProductAttributeValues.Add(ProductAttributeValue.Create(tenant.ProductId, brand, new AttributeValueInput(OptionId: apple.Id)));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_ProductAttributeValues");
        db.ChangeTracker.Clear();

        // The same tag twice.
        db.ProductAttributeValues.Add(ProductAttributeValue.Create(tenant.ProductId, tags, new AttributeValueInput(OptionId: wireless.Id)));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_ProductAttributeValues");
        db.ChangeTracker.Clear();

        // Two value columns filled at once (impossible through the domain; the database still refuses it).
        await AssertRefusedAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [catalog].[ProductAttributeValues] ([Id],[OrganizationId],[ProductId],[AttributeDefinitionId],[TextValue],[NumberValue],[IsMultiValue],[CreatedAtUtc])
            VALUES (NEWID(), {tenant.OrganizationId}, {tenant.ProductId}, {note.Id}, N'x', 1, 0, SYSDATETIMEOFFSET())
            """),
            ConstraintViolation, "CK_ProductAttributeValues_ExactlyOneValue");
        // No value column filled.
        await AssertRefusedAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [catalog].[ProductAttributeValues] ([Id],[OrganizationId],[ProductId],[AttributeDefinitionId],[IsMultiValue],[CreatedAtUtc])
            VALUES (NEWID(), {tenant.OrganizationId}, {tenant.ProductId}, {note.Id}, 0, SYSDATETIMEOFFSET())
            """),
            ConstraintViolation, "CK_ProductAttributeValues_ExactlyOneValue");
    }

    [SqlServerFact]
    public async Task An_option_must_belong_to_the_attribute_it_is_chosen_for_and_to_the_same_organization()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        await using var db = database.CreateDbContext();
        var brand = Define(a, "brand", AttributeDataType.Dropdown, AttributeScope.Product);
        var color = Define(a, "color", AttributeDataType.Dropdown, AttributeScope.Product);
        var brandOfB = Define(b, "brand", AttributeDataType.Dropdown, AttributeScope.Product);
        db.ProductAttributeDefinitions.AddRange(brand, color, brandOfB);
        await db.SaveChangesAsync();
        var red = ProductAttributeOption.Create(color, "Red");
        var samsungOfB = ProductAttributeOption.Create(brandOfB, "Samsung");
        db.ProductAttributeOptions.AddRange(red, samsungOfB);
        await db.SaveChangesAsync();

        // "Brand = Red": the option belongs to a different attribute.
        var value = ProductAttributeValue.Create(a.ProductId, brand, new AttributeValueInput(OptionId: red.Id));
        db.ProductAttributeValues.Add(value);
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_ProductAttributeValues_ProductAttributeOptions");
        db.ChangeTracker.Clear();

        // Another organization's option.
        db.ProductAttributeValues.Add(ProductAttributeValue.Create(a.ProductId, brand, new AttributeValueInput(OptionId: samsungOfB.Id)));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), ConstraintViolation, "FK_ProductAttributeValues_ProductAttributeOptions");
    }

    [SqlServerFact]
    public async Task Variants_carry_their_own_attribute_values_sku_and_stock_while_simple_products_need_none()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        var size = Define(tenant, "size", AttributeDataType.Dropdown, AttributeScope.Variant);
        db.ProductAttributeDefinitions.Add(size);
        var shirt = Product.Create(tenant.OrganizationId, Spec(tenant, "SHIRT") with { HasVariants = true });
        db.Products.Add(shirt);
        await db.SaveChangesAsync();
        var (small, large) = (ProductAttributeOption.Create(size, "S"), ProductAttributeOption.Create(size, "L"));
        var (variantS, variantL) = (
            ProductVariant.Create(tenant.OrganizationId, shirt.Id, new ProductVariantSpec("SHIRT-S", "111", "S", SalePrice: 1500m)),
            ProductVariant.Create(tenant.OrganizationId, shirt.Id, new ProductVariantSpec("SHIRT-L", "222", "L", SalePrice: 1600m)));
        db.AddRange(small, large, variantS, variantL);
        await db.SaveChangesAsync();
        db.ProductVariantAttributeValues.AddRange(
            ProductVariantAttributeValue.Create(variantS.Id, size, new AttributeValueInput(OptionId: small.Id)),
            ProductVariantAttributeValue.Create(variantL.Id, size, new AttributeValueInput(OptionId: large.Id)));
        db.InventoryBalances.AddRange(
            InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, shirt.Id, variantS.Id, null),
            InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, shirt.Id, variantL.Id, null),
            InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null)); // a simple product beside them
        await db.SaveChangesAsync();

        // Each variant has its own SKU...
        db.ProductVariants.Add(ProductVariant.Create(tenant.OrganizationId, shirt.Id, new ProductVariantSpec("shirt-s", null, null)));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_ProductVariants_OrganizationId_Sku");
        db.ChangeTracker.Clear();

        // ...and a variant value for the SAME variant twice is refused.
        db.ProductVariantAttributeValues.Add(ProductVariantAttributeValue.Create(variantS.Id, size, new AttributeValueInput(OptionId: large.Id)));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_ProductVariantAttributeValues");
        Assert.Equal(3, await database.CreateDbContext().InventoryBalances.CountAsync(balance => balance.OrganizationId == tenant.OrganizationId));
    }

    // ---- Ledger and documents: posted history is never changed or deleted ----------------------------------------

    [SqlServerFact]
    public async Task The_stock_ledger_is_append_only()
    {
        var tenant = await SeedAsync();
        Guid movementId;
        await using (var db = database.CreateDbContext())
        {
            var movement = StockMovement.Create(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null, StockMovementType.Receipt, 10, 10, Now, unitCost: 4m);
            db.StockMovements.Add(movement);
            await db.SaveChangesAsync();
            movementId = movement.Id;
        }

        await using var other = database.CreateDbContext();
        await AssertRefusedAsync(() => other.Database.ExecuteSqlInterpolatedAsync($"UPDATE [inventory].[StockMovements] SET [Quantity] = 99 WHERE [Id] = {movementId}"), 51010);
        await AssertRefusedAsync(() => other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [inventory].[StockMovements] WHERE [Id] = {movementId}"), 51010);

        // Through EF Core too.
        var tracked = await other.StockMovements.SingleAsync(movement => movement.Id == movementId);
        other.StockMovements.Remove(tracked);
        await AssertRefusedAsync(() => other.SaveChangesAsync(), 51010);

        await using var check = database.CreateDbContext();
        Assert.Equal(10, (await check.StockMovements.AsNoTracking().SingleAsync(movement => movement.Id == movementId)).Quantity);
    }

    [SqlServerFact]
    public async Task A_movement_is_reversed_by_a_new_movement_at_most_once_and_its_sign_must_match_its_type()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        var original = StockMovement.Create(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null, StockMovementType.Receipt, 10, 10, Now);
        db.StockMovements.Add(original);
        await db.SaveChangesAsync();

        db.StockMovements.Add(StockMovement.Create(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null,
            StockMovementType.ReversalDecrease, 10, 0, Now, reversesMovementId: original.Id));
        await db.SaveChangesAsync();

        db.StockMovements.Add(StockMovement.Create(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null,
            StockMovementType.ReversalDecrease, 10, -10, Now, reversesMovementId: original.Id));
        await AssertRefusedAsync(() => db.SaveChangesAsync(), UniqueIndexViolation, "IX_StockMovements_OrganizationId_ReversesMovementId");
        db.ChangeTracker.Clear();

        // A receipt stored with a negative quantity (the domain signs it correctly; the database checks anyway).
        await AssertRefusedAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [inventory].[StockMovements] ([Id],[OrganizationId],[WarehouseId],[ProductId],[Type],[Quantity],[QuantityAfter],[OccurredAtUtc],[SourceType],[CreatedAtUtc])
            VALUES (NEWID(), {tenant.OrganizationId}, {tenant.WarehouseId}, {tenant.ProductId}, 2, -5, 0, SYSDATETIMEOFFSET(), N'Manual', SYSDATETIMEOFFSET())
            """),
            ConstraintViolation, "CK_StockMovements_Quantity");
    }

    [SqlServerFact]
    public async Task A_draft_document_can_be_deleted_with_its_lines_but_a_posted_one_never()
    {
        var tenant = await SeedAsync();
        Guid draftId, postedId;
        await using (var db = database.CreateDbContext())
        {
            var draft = StockAdjustment.Create(tenant.OrganizationId, "ADJ-DRAFT", tenant.WarehouseId, new DateOnly(2026, 11, 2), StockAdjustmentReason.Damage, null);
            draft.AddItem(tenant.ProductId, null, null, -1, null, null);
            var posted = StockAdjustment.Create(tenant.OrganizationId, "ADJ-POSTED", tenant.WarehouseId, new DateOnly(2026, 11, 2), StockAdjustmentReason.StockCount, null);
            posted.AddItem(tenant.ProductId, null, null, 5, 3m, null);
            db.StockAdjustments.AddRange(draft, posted);
            await db.SaveChangesAsync();                                   // every document starts as a draft ...
            posted.MarkPosted(Now, null);
            await db.SaveChangesAsync();                                   // ... and is posted later (lines cannot be added to a posted one)
            (draftId, postedId) = (draft.Id, posted.Id);
        }

        await using (var db = database.CreateDbContext())
        {
            db.StockAdjustments.Remove(await db.StockAdjustments.SingleAsync(adjustment => adjustment.Id == draftId));
            await db.SaveChangesAsync();                                   // the draft and (by cascade) its line are gone
            Assert.Equal(0, await db.StockAdjustmentItems.CountAsync(item => item.StockAdjustmentId == draftId));
        }

        await using (var db = database.CreateDbContext())
        {
            db.StockAdjustments.Remove(await db.StockAdjustments.SingleAsync(adjustment => adjustment.Id == postedId));
            await AssertRefusedAsync(() => db.SaveChangesAsync(), 51011);
        }

        await using var check = database.CreateDbContext();
        Assert.Equal(1, await check.StockAdjustments.CountAsync(adjustment => adjustment.Id == postedId));
        Assert.Equal(1, await check.StockAdjustmentItems.CountAsync(item => item.StockAdjustmentId == postedId));
    }

    [SqlServerFact]
    public async Task A_transfer_needs_two_warehouses_and_only_a_draft_transfer_can_be_deleted()
    {
        var tenant = await SeedAsync();
        Guid otherWarehouse, dispatchedId;
        await using (var db = database.CreateDbContext())
        {
            var second = Warehouse.Create(tenant.OrganizationId, null, "SHOP", "Shop floor", null);
            db.Warehouses.Add(second);
            await db.SaveChangesAsync();
            otherWarehouse = second.Id;

            var transfer = StockTransfer.Create(tenant.OrganizationId, "TR-1", tenant.WarehouseId, second.Id, new DateOnly(2026, 11, 2), null);
            transfer.AddItem(tenant.ProductId, null, null, 4);
            db.StockTransfers.Add(transfer);
            await db.SaveChangesAsync();
            transfer.MarkDispatched(Now);
            await db.SaveChangesAsync();
            dispatchedId = transfer.Id;
        }

        await using var other = database.CreateDbContext();
        await AssertRefusedAsync(() => other.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [inventory].[StockTransfers] ([Id],[OrganizationId],[Number],[FromWarehouseId],[ToWarehouseId],[TransferDate],[Status],[CreatedAtUtc])
            VALUES (NEWID(), {tenant.OrganizationId}, N'TR-2', {otherWarehouse}, {otherWarehouse}, '2026-11-02', 1, SYSDATETIMEOFFSET())
            """),
            ConstraintViolation, "CK_StockTransfers_DifferentWarehouses");

        other.StockTransfers.Remove(await other.StockTransfers.SingleAsync(transfer => transfer.Id == dispatchedId));
        await AssertRefusedAsync(() => other.SaveChangesAsync(), 51012);
    }

    // ---- Concurrency and defaults -----------------------------------------------------------------------------

    [SqlServerFact]
    public async Task Two_people_changing_the_same_balance_cannot_both_succeed()
    {
        var tenant = await SeedAsync();
        await using (var setup = database.CreateDbContext())
        {
            var balance = InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null);
            balance.Increase(10, 5m, Now);
            setup.InventoryBalances.Add(balance);
            await setup.SaveChangesAsync();
        }

        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var seenByFirst = await first.InventoryBalances.SingleAsync(balance => balance.ProductId == tenant.ProductId);
        var seenBySecond = await second.InventoryBalances.SingleAsync(balance => balance.ProductId == tenant.ProductId);

        // Both see 10 on hand and each sells 7: only one may win, or 14 would leave a stock of 10.
        seenByFirst.Decrease(7, allowNegative: false, Now);
        seenBySecond.Decrease(7, allowNegative: false, Now);
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var check = database.CreateDbContext();
        Assert.Equal(3, (await check.InventoryBalances.AsNoTracking().SingleAsync(balance => balance.ProductId == tenant.ProductId)).QuantityOnHand);
    }

    [SqlServerFact]
    public async Task Quantities_keep_four_decimals_and_a_product_does_not_allow_negative_stock_unless_told_to()
    {
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext();
        var balance = InventoryBalance.Open(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null);
        balance.Increase(12.3456m, 7.8912m, Now);
        db.InventoryBalances.Add(balance);
        await db.SaveChangesAsync();

        await using var check = database.CreateDbContext();
        var stored = await check.InventoryBalances.AsNoTracking().SingleAsync(candidate => candidate.ProductId == tenant.ProductId);
        var product = await check.Products.AsNoTracking().SingleAsync(candidate => candidate.Id == tenant.ProductId);

        Assert.Equal(12.3456m, stored.QuantityOnHand);
        Assert.Equal(7.8912m, stored.AverageCost);
        Assert.False(product.AllowNegativeStock);
    }

    [SqlServerFact]
    public async Task Every_new_row_is_stamped_with_who_and_when()
    {
        var user = Guid.NewGuid();
        var tenant = await SeedAsync();
        await using var db = database.CreateDbContext(new StubUser(user), new FixedClock(Now));
        var movement = StockMovement.Create(tenant.OrganizationId, tenant.WarehouseId, tenant.ProductId, null, null, StockMovementType.Receipt, 1, 1, Now);
        db.StockMovements.Add(movement);
        await db.SaveChangesAsync();

        await using var check = database.CreateDbContext();
        var stored = await check.StockMovements.AsNoTracking().SingleAsync(candidate => candidate.Id == movement.Id);
        Assert.Equal(user, stored.CreatedByUserId);
        Assert.Equal(Now, stored.CreatedAtUtc);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private sealed record Tenant(Guid OrganizationId, Guid UnitId, Guid WarehouseId, Guid ProductId);

    /// <summary>An organization with a unit (PCS), a warehouse and one simple product.</summary>
    private async Task<Tenant> SeedAsync()
    {
        await using var db = database.CreateDbContext();
        var organization = Organization.Create($"Inventory test {Guid.NewGuid():N}");
        db.Organizations.Add(organization);
        var unit = UnitOfMeasure.Create(organization.Id, "PCS", "Pieces", 0);
        var warehouse = Warehouse.Create(organization.Id, null, "MAIN", "Main warehouse", null);
        db.AddRange(unit, warehouse);
        var product = Product.Create(organization.Id, new ProductSpec(null, $"BASE-{Guid.NewGuid():N}"[..20], null, "Base product", null, unit.Id, HasVariants: false));
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return new Tenant(organization.Id, unit.Id, warehouse.Id, product.Id);
    }

    private static ProductSpec Spec(Tenant tenant, string sku, Guid? categoryId = null) =>
        new(categoryId, sku, null, $"Product {sku}", null, tenant.UnitId, HasVariants: false);

    private static ProductAttributeDefinition Define(Tenant tenant, string code, AttributeDataType type, AttributeScope scope) =>
        ProductAttributeDefinition.Create(tenant.OrganizationId, code, code, type, scope);

    /// <summary>Asserts the database refused the change with the given SQL Server error (and, when given, named the constraint).</summary>
    private static async Task AssertRefusedAsync(Func<Task> action, int sqlErrorNumber, string? namedInMessage = null)
    {
        var exception = await Record.ExceptionAsync(action);
        var sql = Assert.IsType<SqlException>(exception is DbUpdateException { InnerException: { } inner } ? inner : exception);

        Assert.Equal(sqlErrorNumber, sql.Number);
        if (namedInMessage is not null)
        {
            Assert.Contains(namedInMessage, sql.Message, StringComparison.Ordinal);
        }
    }

    private sealed class StubUser(Guid id) : BusinessSuite.Application.Common.Abstractions.ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId => id;

        public string? UserName => null;

        public string? DisplayName => null;

        public Guid? OrganizationId => null;

        public string? OrganizationRole => null;

        public bool IsSuperAdmin => false;

        public bool HasPermission(string permission) => false;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
