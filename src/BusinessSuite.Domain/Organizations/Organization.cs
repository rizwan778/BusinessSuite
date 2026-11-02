using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Organizations;

/// <summary>
/// A customer business (tenant). People work in it through <see cref="OrganizationMembership"/>s.
/// </summary>
public sealed class Organization : AuditableEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 200;

    private Organization()
    {
    }

    /// <summary>Display name, e.g. "Al-Noor Traders".</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Suspended organizations give their members no access (no organization claims, no permissions).</summary>
    public bool IsActive { get; private set; } = true;

    public static Organization Create(string name)
    {
        var organization = new Organization();
        organization.Rename(name);
        return organization;
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        if (trimmed.Length > NameMaxLength)
        {
            throw new DomainException($"An organization name cannot be longer than {NameMaxLength} characters.");
        }

        Name = trimmed;
    }

    public void Activate() => IsActive = true;

    public void Suspend() => IsActive = false;
}
