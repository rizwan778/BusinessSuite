using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Organizations;

/// <summary>
/// A department or group inside one organization, e.g. "Sales" or "Warehouse" (<c>platform.Groups</c>).
/// </summary>
/// <remarks>
/// <para>
/// A group is the managed form of <see cref="OrganizationMembership.Department"/>: a member belongs to the group whose
/// <see cref="Name"/> equals their department. Groups grant no permissions. Names are unique per organization.
/// </para>
/// <para>
/// Renaming a group must rename the department of its members, and a group with members must not be deleted; the
/// Application layer does both (the link is by name, so there is no foreign key to enforce them).
/// </para>
/// </remarks>
public sealed class Group : AuditableEntity
{
    /// <summary>Maximum length of <see cref="Name"/> (the same as a membership's department).</summary>
    public const int NameMaxLength = OrganizationMembership.DepartmentMaxLength;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 500;

    private Group()
    {
    }

    /// <summary>The owning organization.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Display name, unique within the organization.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>What the group is for. Optional.</summary>
    public string? Description { get; private set; }

    /// <summary>A group of <paramref name="organizationId"/>.</summary>
    /// <exception cref="DomainException">No organization, or a name or description that is too long.</exception>
    /// <exception cref="ArgumentException">The name is missing or blank.</exception>
    public static Group Create(Guid organizationId, string name, string? description)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException("A group needs an organization.");
        }

        var group = new Group { OrganizationId = organizationId };
        group.Describe(name, description);
        return group;
    }

    /// <summary>Replaces the name and description (both trimmed; a blank description removes it).</summary>
    /// <exception cref="DomainException">A name or description that is too long.</exception>
    /// <exception cref="ArgumentException">The name is missing or blank.</exception>
    public void Describe(string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmedName = name.Trim();
        if (trimmedName.Length > NameMaxLength)
        {
            throw new DomainException($"A group name cannot be longer than {NameMaxLength} characters.");
        }

        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription?.Length > DescriptionMaxLength)
        {
            throw new DomainException($"A group description cannot be longer than {DescriptionMaxLength} characters.");
        }

        Name = trimmedName;
        Description = trimmedDescription;
    }
}
