namespace BusinessSuite.Domain.Common;

/// <summary>
/// Marks a record that belongs to exactly one organization (tenant). Every such table has a non-null
/// <c>OrganizationId</c>, every index starts with it, and references to other tenant-owned tables are composite
/// <c>(OrganizationId, Id)</c> foreign keys, so the database itself refuses a reference across organizations.
/// </summary>
public interface ITenantOwned
{
    /// <summary>The owning organization.</summary>
    Guid OrganizationId { get; }
}

/// <summary>Base class of audited, tenant-owned entities.</summary>
public abstract class TenantEntity : AuditableEntity, ITenantOwned
{
    /// <inheritdoc />
    public Guid OrganizationId { get; private set; }

    /// <summary>Sets the owner; called once by the concrete entity's factory.</summary>
    /// <exception cref="DomainException">No organization.</exception>
    protected void BelongTo(Guid organizationId)
    {
        DomainGuard.Present(organizationId, "A record needs an organization.");
        OrganizationId = organizationId;
    }
}

/// <summary>Small argument checks that throw <see cref="DomainException"/> with a user-safe message.</summary>
public static class DomainGuard
{
    /// <summary>Largest quantity, cost or price the database can hold (decimal(18,4)) with room to add.</summary>
    public const decimal MaxAmount = 99_999_999_999_999m;

    /// <summary>Throws when <paramref name="id"/> is empty.</summary>
    public static void Present(Guid id, string message)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException(message);
        }
    }

    /// <summary>Throws unless <paramref name="condition"/> holds.</summary>
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new DomainException(message);
        }
    }

    /// <summary>A required text, trimmed.</summary>
    /// <exception cref="DomainException">Blank or longer than <paramref name="maxLength"/>.</exception>
    public static string Text(string? value, int maxLength, string label)
    {
        var trimmed = value?.Trim();
        Require(!string.IsNullOrEmpty(trimmed), $"{label} is required.");
        Require(trimmed!.Length <= maxLength, $"{label} cannot be longer than {maxLength} characters.");
        return trimmed;
    }

    /// <summary>An optional text, trimmed; blank becomes <see langword="null"/>.</summary>
    /// <exception cref="DomainException">Longer than <paramref name="maxLength"/>.</exception>
    public static string? OptionalText(string? value, int maxLength, string label)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Require(trimmed is null || trimmed.Length <= maxLength, $"{label} cannot be longer than {maxLength} characters.");
        return trimmed;
    }

    /// <summary>An optional amount that must be zero or more (and fit the database).</summary>
    public static decimal? OptionalAmount(decimal? value, string label)
    {
        Require(value is null or (>= 0 and <= MaxAmount), $"{label} must be zero or more.");
        return value;
    }
}
