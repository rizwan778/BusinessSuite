using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Catalog;

/// <summary>
/// A unit stock is counted in, e.g. "PCS" (0 decimals), "KG" (3), "M" (2) (<c>catalog.UnitsOfMeasure</c>).
/// <see cref="DecimalPlaces"/> says how fine a quantity may be, so a business selling loose goods and one selling only whole
/// items use the same model.
/// </summary>
public sealed class UnitOfMeasure : TenantEntity
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 10;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 50;

    /// <summary>Most decimal places a quantity can have.</summary>
    public const int MaxDecimalPlaces = 4;

    private UnitOfMeasure()
    {
    }

    /// <summary>Short code, unique within the organization.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display name, unique within the organization.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>How many decimal places a quantity in this unit may have (0 for whole items).</summary>
    public int DecimalPlaces { get; private set; }

    /// <summary><see langword="false"/> once retired.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>A unit of <paramref name="organizationId"/>.</summary>
    /// <exception cref="DomainException">No organization, a missing or too long value, or decimal places outside 0 to 4.</exception>
    public static UnitOfMeasure Create(Guid organizationId, string? code, string? name, int decimalPlaces)
    {
        var unit = new UnitOfMeasure();
        unit.BelongTo(organizationId);
        unit.Describe(code, name, decimalPlaces);
        return unit;
    }

    /// <summary>Replaces the code, name and decimal places.</summary>
    public void Describe(string? code, string? name, int decimalPlaces)
    {
        DomainGuard.Require(decimalPlaces is >= 0 and <= MaxDecimalPlaces, $"Decimal places must be between 0 and {MaxDecimalPlaces}.");
        Code = DomainGuard.Text(code, CodeMaxLength, "The unit code");
        Name = DomainGuard.Text(name, NameMaxLength, "The unit name");
        DecimalPlaces = decimalPlaces;
    }

    /// <summary>Activates or retires the unit.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;
}

/// <summary>
/// "1 <see cref="FromUnitId"/> = <see cref="Factor"/> x <see cref="ToUnitId"/>" (<c>catalog.UnitConversions</c>). General
/// (1 kg = 1000 g) when <see cref="ProductId"/> is empty, or for one product (1 carton of this biscuit = 24 pcs).
/// </summary>
public sealed class UnitConversion : TenantEntity
{
    /// <summary>Precision of <see cref="Factor"/> (decimal(18,6)).</summary>
    public const int FactorScale = 6;

    private UnitConversion()
    {
    }

    /// <summary>The product this applies to; <see langword="null"/> for a conversion that holds for every product.</summary>
    public Guid? ProductId { get; private set; }

    /// <summary>The unit converted from.</summary>
    public Guid FromUnitId { get; private set; }

    /// <summary>The unit converted to.</summary>
    public Guid ToUnitId { get; private set; }

    /// <summary>How many <see cref="ToUnitId"/> one <see cref="FromUnitId"/> is. Greater than zero.</summary>
    public decimal Factor { get; private set; }

    /// <summary>A conversion of <paramref name="organizationId"/>.</summary>
    /// <exception cref="DomainException">No organization or unit, the same unit twice, or a factor that is not positive.</exception>
    public static UnitConversion Create(Guid organizationId, Guid? productId, Guid fromUnitId, Guid toUnitId, decimal factor)
    {
        DomainGuard.Present(fromUnitId, "Choose the unit to convert from.");
        DomainGuard.Present(toUnitId, "Choose the unit to convert to.");
        DomainGuard.Require(fromUnitId != toUnitId, "A unit cannot be converted to itself.");
        DomainGuard.Require(factor > 0 && factor <= DomainGuard.MaxAmount, "The conversion factor must be greater than zero.");

        var conversion = new UnitConversion
        {
            ProductId = productId,
            FromUnitId = fromUnitId,
            ToUnitId = toUnitId,
            Factor = Math.Round(factor, FactorScale, MidpointRounding.AwayFromZero),
        };
        DomainGuard.Require(conversion.Factor > 0, "The conversion factor is too small.");
        conversion.BelongTo(organizationId);
        return conversion;
    }

    /// <summary>Changes the factor (the units and product are the conversion's identity and stay).</summary>
    /// <exception cref="DomainException">A factor that is not positive.</exception>
    public void SetFactor(decimal factor)
    {
        DomainGuard.Require(factor > 0 && factor <= DomainGuard.MaxAmount, "The conversion factor must be greater than zero.");
        var rounded = Math.Round(factor, FactorScale, MidpointRounding.AwayFromZero);
        DomainGuard.Require(rounded > 0, "The conversion factor is too small.");
        Factor = rounded;
    }

    /// <summary>Converts <paramref name="quantity"/> of the "from" unit into the "to" unit.</summary>
    public decimal ToBase(decimal quantity) => quantity * Factor;
}
