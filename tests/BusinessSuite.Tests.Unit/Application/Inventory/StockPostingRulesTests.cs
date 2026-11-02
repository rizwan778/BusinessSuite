using BusinessSuite.Application.Features.Inventory;
using BusinessSuite.Domain.Inventory;

namespace BusinessSuite.Tests.Unit.Application.Inventory;

/// <summary>The checks of a stock posting request that need no database (<see cref="StockPostingRules"/>).</summary>
public sealed class StockPostingRulesTests
{
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Warehouse = Guid.NewGuid();

    private static StockPostingLine Line(decimal quantity = 1, decimal? cost = null, Guid? sourceLine = null, Guid? product = null) =>
        new(sourceLine ?? Guid.NewGuid(), product ?? Guid.NewGuid(), null, null, quantity, cost);

    private static StockPostingRequest Request(params StockPostingLine[] lines) =>
        new("SalesInvoice", Source, Warehouse, StockPostingKind.Issue, lines.Length == 0 ? [Line()] : lines);

    [Fact]
    public void A_well_formed_request_is_accepted()
    {
        Assert.Null(StockPostingRules.Validate(Request(Line(2.5m), Line(1, cost: 3m))));
    }

    [Theory]
    [InlineData("SalesInvoice", true)]
    [InlineData("Purchase_Receipt2", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("2Invoice", false)]
    [InlineData("Sales Invoice", false)]
    [InlineData("Sales-Invoice", false)]
    public void Source_types_are_short_identifiers(string? sourceType, bool valid)
    {
        Assert.Equal(valid, StockPostingRules.IsValidSourceType(sourceType));
    }

    [Fact]
    public void Source_types_are_limited_in_length_to_what_the_ledger_stores()
    {
        Assert.True(StockPostingRules.IsValidSourceType(new string('x', StockMovement.SourceTypeMaxLength)));
        Assert.False(StockPostingRules.IsValidSourceType(new string('x', StockMovement.SourceTypeMaxLength + 1)));
    }

    [Theory]
    [InlineData("StockAdjustment")]
    [InlineData("stockadjustment")]
    [InlineData("StockTransfer")]
    [InlineData("MANUAL")]
    public void Other_modules_cannot_post_as_the_inventory_modules_own_documents(string sourceType)
    {
        Assert.False(StockPostingRules.IsValidSourceType(sourceType));
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request() with { SourceType = sourceType })!.Code);
    }

    [Fact]
    public void Missing_ids_and_lines_are_refused()
    {
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request() with { SourceId = Guid.Empty })!.Code);
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request() with { WarehouseId = Guid.Empty })!.Code);
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request() with { Lines = [] })!.Code);
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request() with { Kind = (StockPostingKind)99 })!.Code);
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request(new StockPostingLine(Guid.Empty, Guid.NewGuid(), null, null, 1)))!.Code);
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request(new StockPostingLine(Guid.NewGuid(), Guid.Empty, null, null, 1)))!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantities_must_be_greater_than_zero(double quantity)
    {
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request(Line((decimal)quantity)))!.Code);
    }

    [Fact]
    public void A_negative_cost_is_refused()
    {
        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request(Line(1, cost: -0.01m)))!.Code);
    }

    [Fact]
    public void The_same_source_line_twice_is_refused()
    {
        var line = Guid.NewGuid();
        var product = Guid.NewGuid();

        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request(Line(sourceLine: line, product: product), Line(sourceLine: line, product: product)))!.Code);

        // The same source line may split across different products or batches: those are different facts.
        Assert.Null(StockPostingRules.Validate(Request(Line(sourceLine: line), Line(sourceLine: line))));
    }

    [Fact]
    public void The_number_of_lines_is_limited()
    {
        var tooMany = Enumerable.Range(0, StockPostingRules.MaxLines + 1).Select(_ => Line()).ToArray();

        Assert.Equal("Stock.PostingInvalid", StockPostingRules.Validate(Request(tooMany))!.Code);
        Assert.Null(StockPostingRules.Validate(Request(tooMany.Take(StockPostingRules.MaxLines).ToArray())));
    }

    [Fact]
    public void A_posting_kind_maps_to_the_ledger_movement_type()
    {
        Assert.Equal(StockMovementType.Receipt, StockPostingRules.MovementType(StockPostingKind.Receipt));
        Assert.Equal(StockMovementType.Issue, StockPostingRules.MovementType(StockPostingKind.Issue));
    }
}
