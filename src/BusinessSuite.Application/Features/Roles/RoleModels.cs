using System.Text.Json.Serialization;

namespace BusinessSuite.Application.Features.Roles;

/// <summary>Where a role lives.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoleScope>))]
public enum RoleScope
{
    /// <summary>A custom role owned by one organization.</summary>
    Organization = 0,

    /// <summary>A shared role (the system roles and Super Admin-defined ones), available to organizations as configured.</summary>
    Platform = 1,
}

/// <summary>A role as returned by the roles API.</summary>
/// <param name="Id">The role's id; use it to assign the role (<c>PATCH /api/v1/users/{id}/role</c>).</param>
/// <param name="Name">Display name, unique among the roles an organization can use.</param>
/// <param name="Description">What the role is for.</param>
/// <param name="Scope"><see cref="RoleScope.Platform"/> (shared) or <see cref="RoleScope.Organization"/> (custom role of one organization).</param>
/// <param name="OrganizationId">The owning organization of an organization role.</param>
/// <param name="IsSystem">Built-in role: cannot be renamed or deleted.</param>
/// <param name="PermissionsLocked">The permissions cannot be changed (the Owner always holds every organization permission).</param>
/// <param name="CanManage">The caller may change this role (name, permissions, deletion where otherwise allowed).</param>
/// <param name="Permissions">The permissions the role grants, sorted.</param>
/// <param name="MemberCount">Memberships holding the role that the caller can see: in their organization, or everywhere for a Super Admin.</param>
/// <param name="Availability">Platform roles, for Super Admins only: which organizations may use the role.</param>
/// <param name="CreatedAtUtc">When the role was created.</param>
/// <param name="UpdatedAtUtc">When it was last changed, if ever.</param>
public sealed record RoleDetails(
    Guid Id,
    string Name,
    string? Description,
    RoleScope Scope,
    Guid? OrganizationId,
    bool IsSystem,
    bool PermissionsLocked,
    bool CanManage,
    IReadOnlyList<string> Permissions,
    int MemberCount,
    RoleAvailabilityDetails? Availability,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>Which organizations may use a platform role.</summary>
/// <param name="AllOrganizations">Every organization, present and future.</param>
/// <param name="OrganizationIds">When not all: the organizations that may use it.</param>
public sealed record RoleAvailabilityDetails(bool AllOrganizations, IReadOnlyList<Guid> OrganizationIds);

/// <summary>A role's permissions (<c>GET/PUT /api/v1/roles/{id}/permissions</c>).</summary>
/// <param name="RoleId">The role.</param>
/// <param name="RoleName">The role's name.</param>
/// <param name="PermissionsLocked">The permissions cannot be changed (Owner).</param>
/// <param name="Permissions">The permissions granted, sorted.</param>
public sealed record RolePermissionsDetails(Guid RoleId, string RoleName, bool PermissionsLocked, IReadOnlyList<string> Permissions);

/// <summary>Filters for <see cref="IRoleService.ListAsync"/>.</summary>
public sealed record RoleListQuery
{
    /// <summary>Part of the role name (case-insensitive).</summary>
    public string? Search { get; init; }

    /// <summary>
    /// Only the roles available to this organization. SUPER ADMINS ONLY: organization users always see the roles of
    /// their own organization.
    /// </summary>
    public Guid? OrganizationId { get; init; }
}

/// <summary>Input for <see cref="IRoleService.CreateAsync"/>.</summary>
/// <param name="Name">Display name; system role names are reserved.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Scope">
/// <see cref="RoleScope.Organization"/> (default): a custom role of the organization the request acts in.
/// <see cref="RoleScope.Platform"/>: a shared role (Super Admins only), available to no organization until configured.
/// </param>
/// <param name="Permissions">Initial permissions; <see langword="null"/> or empty for none.</param>
public sealed record CreateRoleCommand(string Name, string? Description, RoleScope Scope, IReadOnlyList<string>? Permissions);

/// <summary>Input for <see cref="IRoleService.UpdateAsync"/>: name and description (both replaced).</summary>
public sealed record UpdateRoleCommand(string Name, string? Description);

/// <summary>Input for <see cref="IRoleService.SetAvailabilityAsync"/>.</summary>
/// <param name="AllOrganizations">Available to every organization (the list is then ignored).</param>
/// <param name="OrganizationIds">Otherwise, the organizations that may use the role (replaces the current list).</param>
public sealed record SetRoleAvailabilityCommand(bool AllOrganizations, IReadOnlyList<Guid> OrganizationIds);
