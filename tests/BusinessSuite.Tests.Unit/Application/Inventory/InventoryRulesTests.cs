using BusinessSuite.Application.Features.Catalog;
using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Catalog;
using BusinessSuite.Domain.Inventory;
using BusinessSuite.Tests.Unit.TestDoubles;

namespace BusinessSuite.Tests.Unit.Application.Inventory;

/// <summary>The pure rules of the catalogue and inventory APIs: attribute validation, conversions, stock quantities, error codes.</summary>
public sealed class InventoryRulesTests
{
    private static readonly Guid Samsung = Guid.NewGuid();
    private static readonly Guid Apple = Guid.NewGuid();
    private static readonly Guid Wireless = Guid.NewGuid();

    // ---- Which organization ----

    [Fact]
    public void Inventory_data_belongs_to_the_organization_the_request_acts_in()
    {
        var organization = Guid.NewGuid();
        var member = new FakeCurrentUser { IsAuthenticated = true, UserId = Guid.NewGuid(), OrganizationId = organization };
        var superAdminWithoutOrganization = new FakeCurrentUser { IsAuthenticated = true, UserId = Guid.NewGuid(), IsSuperAdmin = true };

        Assert.Equal(organization, InventoryAccessRules.ActingOrganization(member).Value);
        Assert.Equal(CatalogErrors.OrganizationRequired, InventoryAccessRules.ActingOrganization(superAdminWithoutOrganization).Error);
        Assert.Equal(CatalogErrors.OrganizationRequired, InventoryAccessRules.ActingOrganization(FakeCurrentUser.SignedIn()).Error);
    }

    // ---- Dynamic attribute values ----

    private static List<AttributeRule> Schema() =>
    [
        Rule("brand", AttributeDataType.Dropdown, required: true, options: [Samsung, Apple]),
        Rule("tags", AttributeDataType.MultiSelect, options: [Wireless]),
        Rule("model", AttributeDataType.Text),
        Rule("ram_gb", AttributeDataType.Number),
        Rule("volume_ml", AttributeDataType.Decimal),
        Rule("launched", AttributeDataType.Date),
        Rule("waterproof", AttributeDataType.Boolean),
    ];

    [Fact]
    public void Valid_values_of_every_data_type_are_accepted_and_a_multiselect_becomes_one_value_per_option()
    {
        var validation = AttributeValueValidator.Validate(Schema(),
        [
            new AttributeValueCommand("brand", null, null, null, null, Samsung, null),
            new AttributeValueCommand("tags", null, null, null, null, null, [Wireless]),
            new AttributeValueCommand("model", "Galaxy S", null, null, null, null, null),
            new AttributeValueCommand("ram_gb", null, 8, null, null, null, null),
            new AttributeValueCommand("volume_ml", null, 330.5m, null, null, null, null),
            new AttributeValueCommand("launched", null, null, new DateOnly(2026, 2, 25), null, null, null),
            new AttributeValueCommand("waterproof", null, null, null, true, null, null),
        ]);

        Assert.True(validation.IsValid, string.Join("; ", validation.Problems));
        Assert.Equal(7, validation.Values.Count);
    }

    [Fact]
    public void A_multiselect_with_several_options_stores_each_of_them()
    {
        var second = Guid.NewGuid();
        var rules = new List<AttributeRule> { Rule("tags", AttributeDataType.MultiSelect, options: [Wireless, second]) };

        var validation = AttributeValueValidator.Validate(rules, [new AttributeValueCommand("tags", null, null, null, null, null, [Wireless, second, Wireless])]);

        Assert.True(validation.IsValid);
        Assert.Equal([Wireless, second], validation.Values.Select(value => value.Input.OptionId!.Value).Order(Comparer<Guid>.Default).Order(Comparer<Guid>.Create((a, b) => a == Wireless ? -1 : 1)));
    }

    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var validation = AttributeValueValidator.Validate(Schema(),
        [
            new AttributeValueCommand("ram_gb", null, 8.5m, null, null, null, null),          // not a whole number
            new AttributeValueCommand("brand", null, null, null, null, Guid.NewGuid(), null),  // not an option of brand
            new AttributeValueCommand("colour", "Red", null, null, null, null, null),          // unknown attribute
            new AttributeValueCommand("model", "A", null, null, null, null, null),
            new AttributeValueCommand("MODEL", "B", null, null, null, null, null),            // given twice
            new AttributeValueCommand("launched", "2026-02-25", null, null, null, null, null), // wrong slot
        ]);

        Assert.False(validation.IsValid);
        Assert.Equal(5, validation.Problems.Count);
        Assert.Contains(validation.Problems, problem => problem.Contains("whole number", StringComparison.Ordinal));
        Assert.Contains(validation.Problems, problem => problem.Contains("'colour' does not exist", StringComparison.Ordinal));
        Assert.Contains(validation.Problems, problem => problem.Contains("more than once", StringComparison.Ordinal));
        Assert.Contains(validation.Problems, problem => problem.Contains("'launched'", StringComparison.Ordinal) || problem.Contains("Attribute 'launched'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_required_attribute_must_be_given_and_a_retired_one_cannot_be_set()
    {
        var retired = Rule("legacy", AttributeDataType.Text, active: false);
        var rules = new List<AttributeRule> { Rule("brand", AttributeDataType.Dropdown, required: true, options: [Samsung]), retired };

        var missing = AttributeValueValidator.Validate(rules, []);
        var setRetired = AttributeValueValidator.Validate(rules,
        [
            new AttributeValueCommand("brand", null, null, null, null, Samsung, null),
            new AttributeValueCommand("legacy", "x", null, null, null, null, null),
        ]);

        Assert.Equal(["Attribute 'brand' is required."], missing.Problems);
        Assert.Contains("retired", Assert.Single(setRetired.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_retired_required_attribute_is_not_demanded()
    {
        var rules = new List<AttributeRule> { Rule("legacy", AttributeDataType.Text, required: true, active: false) };

        Assert.True(AttributeValueValidator.Validate(rules, null).IsValid);
    }

    [Theory]
    [InlineData("text", true)]
    [InlineData("  ", false)]
    public void A_text_value_cannot_be_blank(string text, bool valid) =>
        Assert.Equal(valid, AttributeValueValidator.Validate([Rule("model", AttributeDataType.Text)], [new AttributeValueCommand("model", text, null, null, null, null, null)]).IsValid);

    [Fact]
    public void A_dropdown_takes_exactly_one_option_and_a_multiselect_none_outside_its_options()
    {
        var dropdown = Schema();

        var two = AttributeValueValidator.Validate(dropdown, [new AttributeValueCommand("brand", null, null, null, null, null, [Samsung, Apple])]);
        var none = AttributeValueValidator.Validate([Rule("tags", AttributeDataType.MultiSelect, options: [Wireless])], [new AttributeValueCommand("tags", null, null, null, null, null, [])]);

        Assert.Contains("exactly one option", Assert.Single(two.Problems), StringComparison.Ordinal);
        Assert.Contains("needs one or more options", Assert.Single(none.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_without_a_code_is_refused()
    {
        var validation = AttributeValueValidator.Validate(Schema(), [new AttributeValueCommand(" ", "x", null, null, null, null, null)]);

        Assert.Contains(validation.Problems, problem => problem.Contains("code", StringComparison.Ordinal));
    }

    // ---- Unit conversion lookup ----

    private static readonly Guid Carton = Guid.NewGuid();
    private static readonly Guid Piece = Guid.NewGuid();
    private static readonly Guid Kilo = Guid.NewGuid();
    private static readonly Guid Gram = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    [Fact]
    public void A_products_own_conversion_wins_over_a_general_one()
    {
        StoredConversion[] stored = [new(null, Carton, Piece, 12m), new(Product, Carton, Piece, 24m)];

        Assert.Equal((24m, "direct", true), UnitConversionRules.FindFactor(stored, Carton, Piece, Product));
        Assert.Equal((12m, "direct", false), UnitConversionRules.FindFactor(stored, Carton, Piece, null));
        Assert.Equal((12m, "direct", false), UnitConversionRules.FindFactor(stored, Carton, Piece, Guid.NewGuid()));
    }

    [Fact]
    public void A_conversion_also_works_backwards_and_the_same_unit_is_one()
    {
        StoredConversion[] stored = [new(null, Kilo, Gram, 1000m)];

        Assert.Equal((0.001m, "inverse", false), UnitConversionRules.FindFactor(stored, Gram, Kilo, null));
        Assert.Equal((1m, "direct", false), UnitConversionRules.FindFactor(stored, Kilo, Kilo, null));
        Assert.Null(UnitConversionRules.FindFactor(stored, Kilo, Piece, null));
        Assert.Null(UnitConversionRules.FindFactor([new StoredConversion(Product, Carton, Piece, 24m)], Carton, Piece, null)); // another product's
    }

    // ---- Stock quantity rules ----

    [Theory]
    [InlineData(5, 0, null)]
    [InlineData(-5, 0, null)]
    [InlineData(2.5, 3, null)]
    [InlineData(2.5, 0, "whole")]
    [InlineData(1.2345, 3, "at most 3")]
    [InlineData(0, 2, "zero")]
    [InlineData(100000000000000.0, 0, "too large")]
    public void A_quantity_must_fit_the_unit_it_is_counted_in(double quantity, int decimals, string? problem)
    {
        var message = StockRules.CheckQuantity((decimal)quantity, decimals);

        if (problem is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.Contains(problem, message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(10, 10, true)]
    [InlineData(3, 5, true)]
    [InlineData(6, 5, false)]
    [InlineData(0, 5, true)]
    public void Low_stock_means_at_or_below_the_reorder_level(double onHand, double level, bool low) =>
        Assert.Equal(low, StockRules.IsLow((decimal)onHand, (decimal)level) || (decimal)onHand <= 0 && low);

    [Fact]
    public void Items_without_a_reorder_level_are_never_low_and_nothing_on_hand_is_out_of_stock()
    {
        Assert.False(StockRules.IsLow(0, null));
        Assert.True(StockRules.IsOutOfStock(0));
        Assert.True(StockRules.IsOutOfStock(-2));
        Assert.False(StockRules.IsOutOfStock(0.0001m));
    }

    [Fact]
    public void An_adjustments_sign_decides_the_movement_type_and_expiry_is_counted_in_days()
    {
        Assert.Equal(StockMovementType.AdjustmentIncrease, StockRules.AdjustmentType(3));
        Assert.Equal(StockMovementType.AdjustmentDecrease, StockRules.AdjustmentType(-3));

        var today = new DateOnly(2026, 11, 2);
        Assert.Equal(10, StockRules.DaysToExpiry(new DateOnly(2026, 11, 12), today));
        Assert.Equal(-1, StockRules.DaysToExpiry(new DateOnly(2026, 11, 1), today));
        Assert.Null(StockRules.DaysToExpiry(null, today));
    }

    [Fact]
    public void Reserved_stock_cannot_be_taken_out()
    {
        var balance = InventoryBalance.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null);
        balance.Increase(10, 2m, DateTimeOffset.UtcNow);
        typeof(InventoryBalance).GetProperty(nameof(InventoryBalance.QuantityReserved))!.SetValue(balance, 4m);

        Assert.Equal(6, balance.QuantityAvailable);
        Assert.True(balance.CanDecrease(6, allowNegative: false));
        Assert.False(balance.CanDecrease(7, allowNegative: false));
        Assert.True(balance.CanDecrease(7, allowNegative: true));
    }

    // ---- Error contract ----

    [Fact]
    public void Error_codes_are_part_of_the_api_contract()
    {
        Assert.Equal("Inventory.OrganizationRequired", CatalogErrors.OrganizationRequired.Code);
        Assert.Equal("Categories.NotFound", CatalogErrors.CategoryNotFound.Code);
        Assert.Equal("Categories.InUse", CatalogErrors.CategoryInUse.Code);
        Assert.Equal("Units.ConversionUnavailable", CatalogErrors.ConversionUnavailable.Code);
        Assert.Equal("Attributes.Duplicate", CatalogErrors.AttributeDuplicate.Code);
        Assert.Equal("Attributes.OptionInUse", CatalogErrors.OptionInUse.Code);
        Assert.Equal("Products.DuplicateSku", CatalogErrors.ProductDuplicateSku.Code);
        Assert.Equal("Products.HasStock", CatalogErrors.ProductHasStock.Code);
        Assert.Equal("Products.AttributeValuesInvalid", CatalogErrors.AttributeValuesInvalid(["x"]).Code);
        Assert.Equal("Variants.NotAllowed", CatalogErrors.VariantsNotAllowed.Code);
        Assert.Equal("Warehouses.NotFound", InventoryErrors.WarehouseNotFound.Code);
        Assert.Equal("Warehouses.DefaultMustBeActive", InventoryErrors.DefaultWarehouseMustBeActive.Code);
        Assert.Equal("Stock.InsufficientStock", InventoryErrors.InsufficientStock("SKU", 2).Code);
        Assert.Equal("Stock.ConcurrentChange", InventoryErrors.StockConcurrentChange.Code);
        Assert.Equal("Adjustments.NotDraft", InventoryErrors.AdjustmentNotDraft(StockAdjustmentStatus.Posted).Code);
        Assert.Equal("Transfers.NotDraft", InventoryErrors.TransferNotDraft(StockTransferStatus.Received).Code);
        Assert.Equal("Batches.Duplicate", InventoryErrors.BatchDuplicate.Code);
    }

    [Fact]
    public void Not_found_errors_are_404_and_duplicates_are_409()
    {
        Assert.All(new[] { CatalogErrors.ProductNotFound, CatalogErrors.CategoryNotFound, InventoryErrors.WarehouseNotFound, InventoryErrors.AdjustmentNotFound, InventoryErrors.BatchNotFound },
            error => Assert.Equal(BusinessSuite.Application.Common.Results.ErrorType.NotFound, error.Type));
        Assert.All(new[] { CatalogErrors.ProductDuplicateSku, InventoryErrors.WarehouseDuplicate, InventoryErrors.BatchDuplicate, InventoryErrors.StockConcurrentChange, CatalogErrors.ProductInUse },
            error => Assert.Equal(BusinessSuite.Application.Common.Results.ErrorType.Conflict, error.Type));
        Assert.Contains("0.5", InventoryErrors.InsufficientStock("SKU-1", 0.5m).Description, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static AttributeRule Rule(string code, AttributeDataType type, bool required = false, bool active = true, Guid[]? options = null) =>
        new(Guid.NewGuid(), code, code, type, required, active, (options ?? []).ToHashSet());
}
