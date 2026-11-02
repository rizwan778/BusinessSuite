using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Inventory;

namespace BusinessSuite.Application.Features.Inventory;

/// <summary>Pure stock rules (no database), unit-tested.</summary>
public static class StockRules
{
    /// <summary>
    /// Checks a signed or unsigned stock quantity against the unit it is counted in: not zero, within range and no finer than
    /// the unit allows (a unit counted in whole items refuses 2.5).
    /// </summary>
    /// <returns>A message, or <see langword="null"/> when the quantity is acceptable.</returns>
    public static string? CheckQuantity(decimal quantity, int unitDecimalPlaces)
    {
        if (quantity == 0)
        {
            return "The quantity cannot be zero.";
        }

        if (Math.Abs(quantity) > Domain.Common.DomainGuard.MaxAmount)
        {
            return "The quantity is too large.";
        }

        if (Math.Round(quantity, unitDecimalPlaces, MidpointRounding.ToZero) != quantity)
        {
            return unitDecimalPlaces == 0
                ? "This product is counted in whole units. Enter a whole quantity."
                : $"This product's unit allows at most {unitDecimalPlaces} decimal place(s).";
        }

        return null;
    }

    /// <summary>Low stock: a reorder level is set and the stock on hand is at or below it (out of stock is low too).</summary>
    public static bool IsLow(decimal onHand, decimal? reorderLevel) => reorderLevel is { } level && onHand <= level;

    /// <summary>Out of stock: nothing on hand.</summary>
    public static bool IsOutOfStock(decimal onHand) => onHand <= 0;

    /// <summary>The movement type of an adjustment line: a positive difference adds stock, a negative one removes it.</summary>
    public static StockMovementType AdjustmentType(decimal quantityDifference) =>
        quantityDifference > 0 ? StockMovementType.AdjustmentIncrease : StockMovementType.AdjustmentDecrease;

    /// <summary>Days from <paramref name="today"/> to <paramref name="expiry"/> (negative once expired); <see langword="null"/> without an expiry date.</summary>
    public static int? DaysToExpiry(DateOnly? expiry, DateOnly today) => expiry is { } date ? date.DayNumber - today.DayNumber : null;
}
