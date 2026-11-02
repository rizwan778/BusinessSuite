using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Inventory;

/// <summary>
/// A place stock is held: warehouse, shop floor, van or shelf (<c>inventory.Warehouses</c>). A business with one shop has one
/// warehouse; one with several branches has several, optionally tied to a <see cref="Organizations.Branch"/>.
/// </summary>
public sealed class Warehouse : TenantEntity
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 20;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Address"/>.</summary>
    public const int AddressMaxLength = 300;

    private Warehouse()
    {
    }

    /// <summary>The branch it belongs to, if any.</summary>
    public Guid? BranchId { get; private set; }

    /// <summary>Short code, unique within the organization.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display name, unique within the organization.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Where it is. Optional.</summary>
    public string? Address { get; private set; }

    /// <summary><see langword="false"/> once retired: no new stock can be put in it.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>The organization's default warehouse (at most one): preselected where a warehouse is needed.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>A warehouse of <paramref name="organizationId"/>.</summary>
    /// <exception cref="DomainException">No organization, or a missing or too long value.</exception>
    public static Warehouse Create(Guid organizationId, Guid? branchId, string? code, string? name, string? address)
    {
        var warehouse = new Warehouse();
        warehouse.BelongTo(organizationId);
        warehouse.Describe(branchId, code, name, address);
        return warehouse;
    }

    /// <summary>Replaces the branch, code, name and address.</summary>
    public void Describe(Guid? branchId, string? code, string? name, string? address)
    {
        Code = DomainGuard.Text(code, CodeMaxLength, "The warehouse code");
        Name = DomainGuard.Text(name, NameMaxLength, "The warehouse name");
        Address = DomainGuard.OptionalText(address, AddressMaxLength, "The address");
        BranchId = branchId;
    }

    /// <summary>Activates or retires the warehouse.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;

    /// <summary>Makes this the default warehouse, or not. The Application layer keeps it to one per organization.</summary>
    public void SetDefault(bool isDefault) => IsDefault = isDefault;
}

/// <summary>
/// A lot of a product with a batch number and (optionally) an expiry date (<c>inventory.ProductBatches</c>). Only products
/// with <c>TracksBatches</c> have batches; each batch's stock is its own <see cref="InventoryBalance"/>.
/// </summary>
public sealed class ProductBatch : TenantEntity
{
    /// <summary>Maximum length of <see cref="BatchNumber"/>.</summary>
    public const int BatchNumberMaxLength = 50;

    private ProductBatch()
    {
    }

    /// <summary>The product.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>The variant, for products with variants.</summary>
    public Guid? ProductVariantId { get; private set; }

    /// <summary>The supplier's or own lot number, unique per product (and variant).</summary>
    public string BatchNumber { get; private set; } = string.Empty;

    /// <summary>When it was made. Optional.</summary>
    public DateOnly? ManufactureDate { get; private set; }

    /// <summary>When it expires. Optional unless the product requires it.</summary>
    public DateOnly? ExpiryDate { get; private set; }

    /// <summary><see langword="false"/> once closed (recalled, finished): it cannot receive stock.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>A batch of <paramref name="productId"/>.</summary>
    /// <param name="requiresExpiry">The product's <c>RequiresExpiry</c>: an expiry date is then mandatory.</param>
    /// <exception cref="DomainException">No organization or product, a missing batch number, a missing required expiry date, or expiry before manufacture.</exception>
    public static ProductBatch Create(
        Guid organizationId, Guid productId, Guid? productVariantId, string? batchNumber,
        DateOnly? manufactureDate, DateOnly? expiryDate, bool requiresExpiry)
    {
        DomainGuard.Present(productId, "A batch needs a product.");
        DomainGuard.Require(!requiresExpiry || expiryDate is not null, "This product requires an expiry date on every batch.");
        DomainGuard.Require(manufactureDate is null || expiryDate is null || expiryDate >= manufactureDate, "The expiry date cannot be before the manufacture date.");

        var batch = new ProductBatch
        {
            ProductId = productId,
            ProductVariantId = productVariantId,
            BatchNumber = DomainGuard.Text(batchNumber, BatchNumberMaxLength, "The batch number"),
            ManufactureDate = manufactureDate,
            ExpiryDate = expiryDate,
        };
        batch.BelongTo(organizationId);
        return batch;
    }

    /// <summary>Whether the batch is expired on <paramref name="today"/>.</summary>
    public bool IsExpiredOn(DateOnly today) => ExpiryDate is { } expiry && expiry < today;

    /// <summary>Activates or closes the batch.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;
}
