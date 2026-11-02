using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Common;
using BusinessSuite.Domain.Inventory;

namespace BusinessSuite.Tests.Unit.Domain;

/// <summary>The catalogue and inventory entities' own rules (no database).</summary>
public sealed class InventoryDomainTests
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    // ---- Balance: moving average cost, no negative stock ------------------------------------------------------

    [Fact]
    public void Receipts_blend_into_a_weighted_average_cost_and_issues_leave_it_alone()
    {
        var balance = NewBalance();

        balance.Increase(10, 4m, Now);   // 10 @ 4
        balance.Increase(30, 8m, Now);   // 30 @ 8 -> (40 + 240) / 40 = 7
        balance.Decrease(5, allowNegative: false, Now);

        Assert.Equal(35, balance.QuantityOnHand);
        Assert.Equal(7m, balance.AverageCost);
        Assert.Equal(245m, balance.StockValue);
        Assert.Equal(Now, balance.LastMovementAtUtc);
    }

    [Fact]
    public void An_increase_without_a_cost_keeps_the_average()
    {
        var balance = NewBalance();
        balance.Increase(10, 5m, Now);

        balance.Increase(10, null, Now);

        Assert.Equal(20, balance.QuantityOnHand);
        Assert.Equal(5m, balance.AverageCost);
    }

    [Fact]
    public void Stock_cannot_go_negative_by_default()
    {
        var balance = NewBalance();
        balance.Increase(3, 1m, Now);

        Assert.False(balance.CanDecrease(3.0001m, allowNegative: false));
        Assert.True(balance.CanDecrease(3, allowNegative: false));
        Assert.Throws<DomainException>(() => balance.Decrease(4, allowNegative: false, Now));
        Assert.Equal(3, balance.QuantityOnHand);
    }

    [Fact]
    public void Negative_stock_needs_the_product_to_allow_it_and_the_next_receipt_resets_the_cost()
    {
        var balance = NewBalance();
        balance.Increase(2, 10m, Now);

        balance.Decrease(5, allowNegative: true, Now);
        Assert.Equal(-3, balance.QuantityOnHand);

        balance.Increase(10, 6m, Now); // nothing real was on hand: the new cost stands, no blending with a negative quantity
        Assert.Equal(7, balance.QuantityOnHand);
        Assert.Equal(6m, balance.AverageCost);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantities_must_be_positive(decimal quantity)
    {
        var balance = NewBalance();
        balance.Increase(5, 1m, Now);

        Assert.Throws<DomainException>(() => balance.Increase(quantity, 1m, Now));
        Assert.Throws<DomainException>(() => balance.Decrease(quantity, allowNegative: true, Now));
    }

    // ---- Movements: sign follows the type ---------------------------------------------------------------------

    [Theory]
    [InlineData(StockMovementType.OpeningBalance, 1)]
    [InlineData(StockMovementType.Receipt, 1)]
    [InlineData(StockMovementType.AdjustmentIncrease, 1)]
    [InlineData(StockMovementType.TransferIn, 1)]
    [InlineData(StockMovementType.ReversalIncrease, 1)]
    [InlineData(StockMovementType.Issue, -1)]
    [InlineData(StockMovementType.AdjustmentDecrease, -1)]
    [InlineData(StockMovementType.TransferOut, -1)]
    [InlineData(StockMovementType.ReversalDecrease, -1)]
    public void A_movement_is_signed_by_its_type(StockMovementType type, int sign)
    {
        var movement = NewMovement(type, 4);

        Assert.Equal(sign * 4, movement.Quantity);
        Assert.Equal(StockMovement.IncreasesStock(type), sign > 0);
    }

    [Fact]
    public void Every_movement_type_is_classified()
    {
        // The database check constraint lists types 1,2,4,7,8 as increases and 3,5,6,9 as decreases.
        var increases = Enum.GetValues<StockMovementType>().Where(StockMovement.IncreasesStock).Select(type => (int)type).Order();
        Assert.Equal([1, 2, 4, 7, 8], increases);
        Assert.Equal(9, Enum.GetValues<StockMovementType>().Length);
    }

    [Fact]
    public void A_movement_keeps_what_the_user_entered_next_to_the_base_quantity()
    {
        var cartonUnit = Guid.NewGuid();
        var movement = StockMovement.Create(Org, Guid.NewGuid(), Guid.NewGuid(), null, null, StockMovementType.Receipt,
            120, 120, Now, unitCost: 2m, enteredUnitId: cartonUnit, enteredQuantity: 5);

        Assert.Equal(120, movement.Quantity);
        Assert.Equal(5, movement.EnteredQuantity);
        Assert.Equal(cartonUnit, movement.EnteredUnitId);
    }

    [Fact]
    public void Movements_reject_nonsense()
    {
        var (warehouse, product) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.Throws<DomainException>(() => StockMovement.Create(Org, warehouse, product, null, null, StockMovementType.Receipt, 0, 0, Now));
        Assert.Throws<DomainException>(() => StockMovement.Create(Org, warehouse, product, null, null, StockMovementType.Receipt, 1, 1, Now, unitCost: -1));
        Assert.Throws<DomainException>(() => StockMovement.Create(Org, warehouse, product, null, null, StockMovementType.Receipt, 1, 1, Now, enteredQuantity: 2));
        Assert.Throws<DomainException>(() => StockMovement.Create(Org, warehouse, product, null, null, StockMovementType.Receipt, 1, 1, Now, reversesMovementId: Guid.NewGuid()));
        Assert.Throws<DomainException>(() => StockMovement.Create(Org, Guid.Empty, product, null, null, StockMovementType.Receipt, 1, 1, Now));
        Assert.Throws<DomainException>(() => StockMovement.Create(Guid.Empty, warehouse, product, null, null, StockMovementType.Receipt, 1, 1, Now));
        Assert.Throws<DomainException>(() => StockMovement.Create(Org, warehouse, product, null, null, (StockMovementType)99, 1, 1, Now));
    }

    [Fact]
    public void Only_a_reversal_may_point_at_the_movement_it_undoes()
    {
        var movement = StockMovement.Create(Org, Guid.NewGuid(), Guid.NewGuid(), null, null, StockMovementType.ReversalDecrease, 2, 0, Now,
            reversesMovementId: Guid.NewGuid());

        Assert.NotNull(movement.ReversesMovementId);
        Assert.Equal(-2, movement.Quantity);
    }

    // ---- Attributes: typed values -----------------------------------------------------------------------------

    [Theory]
    [InlineData(AttributeDataType.Text)]
    [InlineData(AttributeDataType.Number)]
    [InlineData(AttributeDataType.Decimal)]
    [InlineData(AttributeDataType.Date)]
    [InlineData(AttributeDataType.Boolean)]
    [InlineData(AttributeDataType.Dropdown)]
    [InlineData(AttributeDataType.MultiSelect)]
    public void Every_data_type_stores_exactly_one_typed_value(AttributeDataType type)
    {
        var definition = Define(type);
        var input = type switch
        {
            AttributeDataType.Text => new AttributeValueInput(Text: "Cotton"),
            AttributeDataType.Number => new AttributeValueInput(Number: 24),
            AttributeDataType.Decimal => new AttributeValueInput(Number: 1.5m),
            AttributeDataType.Date => new AttributeValueInput(Date: new DateOnly(2027, 1, 31)),
            AttributeDataType.Boolean => new AttributeValueInput(Boolean: true),
            _ => new AttributeValueInput(OptionId: Guid.NewGuid()),
        };

        var value = ProductAttributeValue.Create(Guid.NewGuid(), definition, input);

        var filled = new object?[] { value.TextValue, value.NumberValue, value.DateValue, value.BooleanValue, value.OptionId }.Count(slot => slot is not null);
        Assert.Equal(1, filled);
        Assert.Equal(type == AttributeDataType.MultiSelect, value.IsMultiValue);
    }

    [Fact]
    public void A_value_must_fit_its_attributes_data_type()
    {
        var product = Guid.NewGuid();

        Assert.Throws<DomainException>(() => ProductAttributeValue.Create(product, Define(AttributeDataType.Number), new AttributeValueInput(Number: 2.5m)));
        Assert.Throws<DomainException>(() => ProductAttributeValue.Create(product, Define(AttributeDataType.Number), new AttributeValueInput(Text: "24")));
        Assert.Throws<DomainException>(() => ProductAttributeValue.Create(product, Define(AttributeDataType.Text), new AttributeValueInput(Text: new string('x', 501))));
        Assert.Throws<DomainException>(() => ProductAttributeValue.Create(product, Define(AttributeDataType.Dropdown), new AttributeValueInput()));
        Assert.Throws<DomainException>(() => ProductAttributeValue.Create(product, Define(AttributeDataType.Dropdown), new AttributeValueInput(Text: "x", Number: 1)));
        Assert.Throws<DomainException>(() => ProductAttributeValue.Create(product, Define(AttributeDataType.Dropdown), new AttributeValueInput(OptionId: Guid.Empty)));
    }

    [Fact]
    public void Product_attributes_and_variant_attributes_cannot_be_mixed_up()
    {
        var sizeOfAVariant = Define(AttributeDataType.Dropdown, AttributeScope.Variant);
        var brandOfAProduct = Define(AttributeDataType.Dropdown, AttributeScope.Product);
        var option = new AttributeValueInput(OptionId: Guid.NewGuid());

        Assert.Throws<DomainException>(() => ProductAttributeValue.Create(Guid.NewGuid(), sizeOfAVariant, option));
        Assert.Throws<DomainException>(() => ProductVariantAttributeValue.Create(Guid.NewGuid(), brandOfAProduct, option));
        Assert.NotNull(ProductVariantAttributeValue.Create(Guid.NewGuid(), sizeOfAVariant, option));
    }

    [Theory]
    [InlineData("ram", true)]
    [InlineData("shelf_life_days", true)]
    [InlineData("Size", false)]
    [InlineData("2ram", false)]
    [InlineData("screen size", false)]
    [InlineData("", false)]
    public void Attribute_codes_are_stable_lower_case_keys(string code, bool valid)
    {
        Func<ProductAttributeDefinition> create = () => ProductAttributeDefinition.Create(Org, code, "Name", AttributeDataType.Text, AttributeScope.Product);

        if (valid)
        {
            Assert.Equal(code, create().Code);
        }
        else
        {
            Assert.Throws<DomainException>(create);
        }
    }

    [Fact]
    public void Options_belong_only_to_dropdown_and_multiselect_attributes_and_a_unit_label_only_to_numbers()
    {
        var text = Saved(Define(AttributeDataType.Text));
        var dropdown = Saved(Define(AttributeDataType.Dropdown));

        Assert.Throws<DomainException>(() => ProductAttributeOption.Create(Define(AttributeDataType.Dropdown), "XL")); // not saved yet: no id to point at

        Assert.Throws<DomainException>(() => ProductAttributeOption.Create(text, "XL"));
        Assert.Equal("XL", ProductAttributeOption.Create(dropdown, " XL ").Label);
        Assert.Throws<DomainException>(() => ProductAttributeDefinition.Create(Org, "fabric", "Fabric", AttributeDataType.Text, AttributeScope.Product, unitLabel: "kg"));
        Assert.Equal("GB", ProductAttributeDefinition.Create(Org, "storage", "Storage", AttributeDataType.Number, AttributeScope.Variant, unitLabel: "GB").UnitLabel);
    }

    // ---- Products, variants, units ----------------------------------------------------------------------------

    [Fact]
    public void A_product_is_created_without_negative_stock_variants_or_batches_by_default()
    {
        var product = Product.Create(Org, new ProductSpec(null, "  SKU-1 ", null, " Tea ", null, Guid.NewGuid(), HasVariants: false));

        Assert.Equal("SKU-1", product.Sku);
        Assert.Equal("Tea", product.Name);
        Assert.True(product.TracksStock);
        Assert.False(product.AllowNegativeStock);
        Assert.False(product.TracksBatches);
        Assert.True(product.IsActive);
    }

    [Fact]
    public void Batch_and_expiry_tracking_need_their_prerequisites()
    {
        var unit = Guid.NewGuid();

        Assert.Throws<DomainException>(() => Product.Create(Org, new ProductSpec(null, "S", null, "N", null, unit, false, TracksStock: false, TracksBatches: true)));
        Assert.Throws<DomainException>(() => Product.Create(Org, new ProductSpec(null, "S", null, "N", null, unit, false, RequiresExpiry: true)));
        Assert.True(Product.Create(Org, new ProductSpec(null, "S", null, "N", null, unit, false, TracksBatches: true, RequiresExpiry: true)).RequiresExpiry);
    }

    [Theory]
    [InlineData(null, "Name")]
    [InlineData("SKU", null)]
    [InlineData("SKU", "   ")]
    public void A_product_needs_a_sku_and_a_name(string? sku, string? name)
    {
        Assert.Throws<DomainException>(() => Product.Create(Org, new ProductSpec(null, sku, null, name, null, Guid.NewGuid(), false)));
    }

    [Fact]
    public void Prices_and_reorder_levels_cannot_be_negative_and_a_unit_is_required()
    {
        Assert.Throws<DomainException>(() => Product.Create(Org, new ProductSpec(null, "S", null, "N", null, Guid.NewGuid(), false, SalePrice: -1)));
        Assert.Throws<DomainException>(() => Product.Create(Org, new ProductSpec(null, "S", null, "N", null, Guid.NewGuid(), false, ReorderLevel: -1)));
        Assert.Throws<DomainException>(() => Product.Create(Org, new ProductSpec(null, "S", null, "N", null, Guid.Empty, false)));
        Assert.Throws<DomainException>(() => Product.Create(Guid.Empty, new ProductSpec(null, "S", null, "N", null, Guid.NewGuid(), false)));
    }

    [Fact]
    public void A_variant_has_its_own_sku_and_belongs_to_a_product()
    {
        var variant = ProductVariant.Create(Org, Guid.NewGuid(), new ProductVariantSpec("TSHIRT-BLU-L", "123", "Blue / L", SalePrice: 1999m));

        Assert.Equal("TSHIRT-BLU-L", variant.Sku);
        Assert.Throws<DomainException>(() => ProductVariant.Create(Org, Guid.Empty, new ProductVariantSpec("X", null, null)));
        Assert.Throws<DomainException>(() => ProductVariant.Create(Org, Guid.NewGuid(), new ProductVariantSpec(" ", null, null)));
    }

    [Fact]
    public void Units_limit_decimals_and_conversions_need_a_positive_factor_between_two_units()
    {
        Assert.Equal(3, UnitOfMeasure.Create(Org, "KG", "Kilogram", 3).DecimalPlaces);
        Assert.Throws<DomainException>(() => UnitOfMeasure.Create(Org, "KG", "Kilogram", 5));
        Assert.Throws<DomainException>(() => UnitOfMeasure.Create(Org, "KG", "Kilogram", -1));

        var (carton, piece) = (Guid.NewGuid(), Guid.NewGuid());
        var conversion = UnitConversion.Create(Org, null, carton, piece, 24);
        Assert.Equal(120m, conversion.ToBase(5));
        Assert.Throws<DomainException>(() => UnitConversion.Create(Org, null, carton, carton, 2));
        Assert.Throws<DomainException>(() => UnitConversion.Create(Org, null, carton, piece, 0));
        Assert.Throws<DomainException>(() => UnitConversion.Create(Org, null, carton, piece, 0.0000001m));
    }

    [Fact]
    public void A_category_cannot_be_its_own_parent()
    {
        var category = ProductCategory.Create(Org, null, null, "Beverages", null);

        Assert.Equal("Beverages", category.Name);
        Assert.Throws<DomainException>(() => ProductCategory.Create(Org, null, null, " ", null));
    }

    // ---- Batches ----------------------------------------------------------------------------------------------

    [Fact]
    public void Batches_check_their_dates_and_a_required_expiry()
    {
        var (product, today) = (Guid.NewGuid(), new DateOnly(2026, 11, 2));

        var batch = ProductBatch.Create(Org, product, null, "LOT-7", new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), requiresExpiry: true);
        Assert.True(batch.IsExpiredOn(today));
        Assert.False(batch.IsExpiredOn(new DateOnly(2026, 11, 1)));

        Assert.Throws<DomainException>(() => ProductBatch.Create(Org, product, null, "LOT-8", null, null, requiresExpiry: true));
        Assert.Throws<DomainException>(() => ProductBatch.Create(Org, product, null, "LOT-9", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1), false));
        Assert.Throws<DomainException>(() => ProductBatch.Create(Org, product, null, " ", null, null, false));
        Assert.False(ProductBatch.Create(Org, product, null, "LOT-10", null, null, false).IsExpiredOn(today));
    }

    // ---- Documents --------------------------------------------------------------------------------------------

    [Fact]
    public void An_adjustment_is_a_draft_until_posted_and_then_final()
    {
        var adjustment = NewAdjustment();
        var product = Guid.NewGuid();
        adjustment.AddItem(product, null, null, -3, null, "Broken");

        Assert.Throws<DomainException>(() => adjustment.AddItem(product, null, null, 1, null, null));   // same line twice
        adjustment.MarkPosted(Now, Guid.NewGuid());

        Assert.Equal(StockAdjustmentStatus.Posted, adjustment.Status);
        Assert.Equal(Now, adjustment.PostedAtUtc);
        Assert.Throws<DomainException>(() => adjustment.AddItem(Guid.NewGuid(), null, null, 1, null, null));
        Assert.Throws<DomainException>(() => adjustment.MarkPosted(Now, null));
        Assert.Throws<DomainException>(adjustment.Cancel);
    }

    [Fact]
    public void An_empty_adjustment_cannot_be_posted_and_a_draft_can_be_cancelled()
    {
        var adjustment = NewAdjustment();

        Assert.Throws<DomainException>(() => adjustment.MarkPosted(Now, null));
        adjustment.Cancel();
        Assert.Equal(StockAdjustmentStatus.Cancelled, adjustment.Status);
    }

    [Fact]
    public void Adjustment_lines_are_signed_and_only_additions_carry_a_cost()
    {
        var adjustment = NewAdjustment();

        Assert.Throws<DomainException>(() => adjustment.AddItem(Guid.NewGuid(), null, null, 0, null, null));
        Assert.Throws<DomainException>(() => adjustment.AddItem(Guid.NewGuid(), null, null, -1, 2m, null));
        Assert.Equal(2m, adjustment.AddItem(Guid.NewGuid(), null, null, 5, 2m, null).UnitCost);
    }

    [Fact]
    public void A_transfer_goes_draft_dispatched_received_and_needs_two_different_warehouses()
    {
        var (from, to) = (Guid.NewGuid(), Guid.NewGuid());
        var transfer = StockTransfer.Create(Org, "TR-1", from, to, new DateOnly(2026, 11, 2), null);

        Assert.Throws<DomainException>(() => transfer.MarkDispatched(Now));               // nothing to send
        Assert.Throws<DomainException>(() => transfer.MarkReceived(Now));                 // not on its way
        var item = transfer.AddItem(Guid.NewGuid(), null, null, 10);
        transfer.MarkDispatched(Now);
        item.RecordDispatch(Guid.NewGuid(), 3m);
        Assert.Throws<DomainException>(() => transfer.AddItem(Guid.NewGuid(), null, null, 1));
        Assert.Throws<DomainException>(transfer.Cancel);

        transfer.MarkReceived(Now.AddHours(5));
        item.RecordReceipt(Guid.NewGuid(), 9);

        Assert.Equal(StockTransferStatus.Received, transfer.Status);
        Assert.Equal(9, item.ReceivedQuantity);
        Assert.Throws<DomainException>(() => StockTransfer.Create(Org, "TR-2", from, from, new DateOnly(2026, 11, 2), null));
        Assert.Throws<DomainException>(() => transfer.AddItem(Guid.NewGuid(), null, null, 0));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static InventoryBalance NewBalance() => InventoryBalance.Open(Org, Guid.NewGuid(), Guid.NewGuid(), null, null);

    private static StockMovement NewMovement(StockMovementType type, decimal quantity) =>
        StockMovement.Create(Org, Guid.NewGuid(), Guid.NewGuid(), null, null, type, quantity, 0, Now);

    private static StockAdjustment NewAdjustment() =>
        StockAdjustment.Create(Org, "ADJ-1", Guid.NewGuid(), new DateOnly(2026, 11, 2), StockAdjustmentReason.StockCount, null);

    /// <summary>Gives the entity the id EF Core would generate when it is added to the context.</summary>
    private static ProductAttributeDefinition Saved(ProductAttributeDefinition definition)
    {
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(definition, Guid.NewGuid());
        return definition;
    }

    private static ProductAttributeDefinition Define(AttributeDataType type, AttributeScope scope = AttributeScope.Product) =>
        ProductAttributeDefinition.Create(Org, "attr", "Attribute", type, scope);
}
