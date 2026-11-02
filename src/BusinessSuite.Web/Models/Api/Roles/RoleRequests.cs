using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Domain.Organizations;

namespace BusinessSuite.Web.Models.Api.Roles;

/// <summary>Query string of <c>GET /api/v1/roles</c>.</summary>
public sealed class RoleListRequest
{
    /// <summary>Part of the role name (case-insensitive).</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary>
    /// Only the roles available to this organization. <b>Super Admins only</b>: organization users always see their own
    /// organization's roles, and naming another one answers 403 <c>Roles.OrganizationFilterNotAllowed</c>.
    /// </summary>
    public Guid? OrganizationId { get; set; }

    public RoleListQuery ToQuery() => new() { Search = Search, OrganizationId = OrganizationId };
}

/// <summary>JSON body of <c>POST /api/v1/roles</c>.</summary>
public sealed class CreateRoleRequest
{
    /// <summary>Display name. System role names (Owner, Administrator, ...) are reserved.</summary>
    [Required(ErrorMessage = "Enter a role name.")]
    [StringLength(RoleDefinition.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [StringLength(RoleDefinition.DescriptionMaxLength)]
    public string? Description { get; set; }

    /// <summary>
    /// <c>Organization</c> (default): a custom role of the organization the request acts in.
    /// <c>Platform</c>: a shared role, Super Admins only; no organization can use it until its availability is set.
    /// </summary>
    public RoleScope Scope { get; set; } = RoleScope.Organization;

    /// <summary>Initial permissions (see <c>GET /api/v1/permissions</c>). You can only grant permissions you hold.</summary>
    public List<string>? Permissions { get; set; }

    public CreateRoleCommand ToCommand() => new(Name, Description, Scope, Permissions);
}

/// <summary>JSON body of <c>PUT /api/v1/roles/{id}</c>: name and description, both replaced. Custom roles only.</summary>
public sealed class UpdateRoleRequest
{
    [Required(ErrorMessage = "Enter a role name.")]
    [StringLength(RoleDefinition.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [StringLength(RoleDefinition.DescriptionMaxLength)]
    public string? Description { get; set; }

    public UpdateRoleCommand ToCommand() => new(Name, Description);
}

/// <summary>JSON body of <c>PUT /api/v1/roles/{id}/permissions</c>: the complete new list (replaces the current one).</summary>
public sealed class SetRolePermissionsRequest
{
    /// <summary>Permission keys, e.g. <c>["inventory.view", "inventory.product.manage"]</c>. An empty list removes every permission.</summary>
    [Required(ErrorMessage = "Send the list of permissions (it may be empty).")]
    [MaxLength(200)]
    public List<string>? Permissions { get; set; }
}

/// <summary>JSON body of <c>PUT /api/v1/roles/{id}/availability</c> (Super Admins, platform roles only).</summary>
public sealed class SetRoleAvailabilityRequest
{
    /// <summary><see langword="true"/>: every organization may use the role; <c>organizationIds</c> is then ignored.</summary>
    [Required(ErrorMessage = "Say whether the role is available to all organizations.")]
    public bool? AllOrganizations { get; set; }

    /// <summary>Otherwise: the organizations that may use the role (replaces the current list; empty: none).</summary>
    [MaxLength(10_000)]
    public List<Guid>? OrganizationIds { get; set; }

    public SetRoleAvailabilityCommand ToCommand() => new(AllOrganizations!.Value, OrganizationIds ?? []);
}
