using BusinessSuite.Domain.Common;

namespace BusinessSuite.Domain.Organizations;

/// <summary>
/// A role people can hold in an organization: a name plus the permissions it grants (<c>platform.RoleDefinitions</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>System roles</b> (<see cref="SystemKey"/> set: Owner, Administrator, ...) are created by migration with the
/// fixed ids in <see cref="OrganizationRoles.Ids"/>. They cannot be renamed or deleted. The Owner's permissions are
/// fixed (every organization permission); the other system roles' permissions can be changed by a Super Admin.</item>
/// <item><b>Platform roles</b> (<see cref="OrganizationId"/> <see langword="null"/>) are shared templates managed by
/// Super Admins: available to every organization (<see cref="AvailableToAllOrganizations"/>) or to selected ones
/// (<see cref="RoleAvailability"/>).</item>
/// <item><b>Organization roles</b> (<see cref="OrganizationId"/> set) are custom roles of one organization.</item>
/// </list>
/// Permission names are validated against the permission catalogue by the Application layer before they get here.
/// </remarks>
public sealed class RoleDefinition : AuditableEntity
{
    /// <summary>Maximum length of <see cref="Name"/>.</summary>
    public const int NameMaxLength = 100;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int DescriptionMaxLength = 500;

    private readonly List<string> _permissions = [];

    private RoleDefinition()
    {
    }

    /// <summary>The organization owning this custom role; <see langword="null"/> for platform (and system) roles.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>For system roles, one of <see cref="OrganizationRoles.All"/>; <see langword="null"/> for custom roles.</summary>
    public string? SystemKey { get; private set; }

    /// <summary>Display name, unique among the roles an organization can use.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>What the role is for. Optional.</summary>
    public string? Description { get; private set; }

    /// <summary>
    /// Platform roles only: usable by every organization. When <see langword="false"/>, only by the organizations
    /// listed in <see cref="RoleAvailability"/>. Always <see langword="true"/> for the Owner.
    /// </summary>
    public bool AvailableToAllOrganizations { get; private set; }

    /// <summary>
    /// The permissions stored for this role, sorted. For the Owner this list is ignored: the Owner always holds every
    /// organization permission.
    /// </summary>
    public IReadOnlyList<string> Permissions => _permissions;

    /// <summary>A built-in role (see <see cref="OrganizationRoles"/>).</summary>
    public bool IsSystem => SystemKey is not null;

    /// <summary>The Owner system role.</summary>
    public bool IsOwner => string.Equals(SystemKey, OrganizationRoles.Owner, StringComparison.Ordinal);

    /// <summary>Shared by organizations (system or Super Admin-defined) rather than owned by one.</summary>
    public bool IsPlatformRole => OrganizationId is null;

    /// <summary>A custom role of one organization.</summary>
    public static RoleDefinition CreateForOrganization(Guid organizationId, string name, string? description)
    {
        if (organizationId == Guid.Empty)
        {
            throw new DomainException("An organization role needs an organization.");
        }

        var role = new RoleDefinition { OrganizationId = organizationId };
        role.Describe(name, description);
        return role;
    }

    /// <summary>A custom platform role. Available to no organization until the Super Admin says so.</summary>
    public static RoleDefinition CreateForPlatform(string name, string? description)
    {
        var role = new RoleDefinition();
        role.Describe(name, description);
        return role;
    }

    /// <summary>Renames the role and replaces its description. System roles keep their name and description.</summary>
    public void Describe(string name, string? description)
    {
        if (IsSystem)
        {
            throw new DomainException("System roles cannot be renamed.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmedName = name.Trim();
        if (trimmedName.Length > NameMaxLength)
        {
            throw new DomainException($"A role name cannot be longer than {NameMaxLength} characters.");
        }

        if (OrganizationRoles.IsReservedName(trimmedName))
        {
            throw new DomainException($"'{trimmedName}' is the name of a system role and cannot be used for a custom role.");
        }

        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription?.Length > DescriptionMaxLength)
        {
            throw new DomainException($"A role description cannot be longer than {DescriptionMaxLength} characters.");
        }

        Name = trimmedName;
        Description = trimmedDescription;
    }

    /// <summary>Replaces the permissions (duplicates removed, sorted). Not allowed for the Owner.</summary>
    public void SetPermissions(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (IsOwner)
        {
            throw new DomainException("The Owner role always holds every organization permission; it cannot be changed.");
        }

        var distinct = permissions
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        _permissions.Clear();
        _permissions.AddRange(distinct);
    }

    /// <summary>Platform roles only: available to every organization, or only to those listed in <see cref="RoleAvailability"/>.</summary>
    public void SetAvailableToAllOrganizations(bool availableToAll)
    {
        if (!IsPlatformRole)
        {
            throw new DomainException("Only platform roles have an availability; an organization role belongs to its organization.");
        }

        if (IsOwner && !availableToAll)
        {
            throw new DomainException("The Owner role must stay available to every organization.");
        }

        AvailableToAllOrganizations = availableToAll;
    }
}
