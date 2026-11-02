using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Organizations;

/// <summary>
/// A shop or office of an organization (<c>platform.Branches</c>). Warehouses may belong to a branch; a business with one
/// location needs no branch at all.
/// </summary>
public sealed class Branch : TenantEntity
{
    /// <summary>Maximum length of <see cref="Code"/>.</summary>
    public const int CodeMaxLength = 20;

    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    private Branch()
    {
    }

    /// <summary>Short code, unique within the organization.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display name, unique within the organization.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary><see langword="false"/> once closed.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>A branch of <paramref name="organizationId"/>.</summary>
    /// <exception cref="DomainException">No organization, or a missing or too long code or name.</exception>
    public static Branch Create(Guid organizationId, string? code, string? name)
    {
        var branch = new Branch();
        branch.BelongTo(organizationId);
        branch.Describe(code, name);
        return branch;
    }

    /// <summary>Replaces the code and name.</summary>
    public void Describe(string? code, string? name)
    {
        Code = DomainGuard.Text(code, CodeMaxLength, "The branch code");
        Name = DomainGuard.Text(name, NameMaxLength, "The branch name");
    }

    /// <summary>Opens or closes the branch.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;
}
