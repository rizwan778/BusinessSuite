using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Roles;

/// <summary>Errors returned by role management. The codes are part of the API contract: do not rename them.</summary>
public static class RoleErrors
{
    /// <summary>
    /// The role does not exist OR is not available to the caller's organization (deliberately the same, so other
    /// organizations' custom roles reveal nothing).
    /// </summary>
    public static readonly Error NotFound =
        Error.NotFound("Roles.NotFound", "No role with this id is available in your organization.");

    /// <summary>The operation needs an organization and the request acts in none.</summary>
    public static readonly Error OrganizationRequired =
        Error.Validation("Roles.OrganizationRequired", "Select the organization to act in with the X-Organization-Id header.");

    /// <summary>An organization user tried to list another organization's roles.</summary>
    public static readonly Error OrganizationFilterNotAllowed =
        Error.Forbidden("Roles.OrganizationFilterNotAllowed", "Only a Super Admin can list the roles of other organizations.");

    /// <summary>Platform (shared) roles and role availability are managed by Super Admins only.</summary>
    public static readonly Error SuperAdminRequired =
        Error.Forbidden("Roles.SuperAdminRequired", "Only a Super Admin can change shared (platform) roles and which roles organizations may use.");

    /// <summary>A non-Super Admin tried to grant a permission they do not hold themselves.</summary>
    public static Error CannotGrantPermission(IEnumerable<string> permissions) =>
        Error.Forbidden("Roles.CannotGrantPermission", $"You can only grant permissions you hold yourself. Not held: {string.Join(", ", permissions)}.");

    /// <summary>A non-Super Admin tried to change a role that grants more than they hold.</summary>
    public static readonly Error RoleExceedsYourPermissions =
        Error.Forbidden("Roles.RoleExceedsYourPermissions", "This role grants permissions you do not hold, so you cannot change it.");

    /// <summary>System roles keep their name and cannot be deleted.</summary>
    public static readonly Error SystemRoleLocked =
        Error.Conflict("Roles.SystemRoleLocked", "System roles cannot be renamed or deleted.");

    /// <summary>The Owner's permissions are fixed.</summary>
    public static readonly Error OwnerPermissionsLocked =
        Error.Conflict("Roles.OwnerPermissionsLocked", "The Owner role always holds every organization permission; its permissions cannot be changed.");

    /// <summary>The Owner must stay available everywhere.</summary>
    public static readonly Error OwnerAvailabilityLocked =
        Error.Conflict("Roles.OwnerAvailabilityLocked", "The Owner role must stay available to every organization.");

    /// <summary>Availability only applies to platform roles.</summary>
    public static readonly Error NotAPlatformRole =
        Error.Validation("Roles.NotAPlatformRole", "Only shared (platform) roles have an availability; an organization role belongs to its organization.");

    /// <summary>Another role the organization can use has this name.</summary>
    public static readonly Error DuplicateName =
        Error.Conflict("Roles.DuplicateName", "A role with this name already exists for this organization or as a shared role. Choose another name.");

    /// <summary>Someone else changed the role at the same moment.</summary>
    public static readonly Error ConcurrentChange =
        Error.Conflict("Roles.ConcurrentChange", "The role was changed by someone else at the same time. Reload and try again.");

    /// <summary>Members still hold the role (deletion, or availability removed from their organization).</summary>
    public static Error InUse(int memberships) =>
        Error.Conflict("Roles.InUse", $"{memberships} member(s) still hold this role. Give them another role first.");

    /// <summary>The role was assigned to someone while it was being deleted.</summary>
    public static readonly Error AssignedConcurrently =
        Error.Conflict("Roles.InUse", "The role was just given to someone. Give them another role first.");

    /// <summary>Not a permission organization roles can grant.</summary>
    public static Error UnknownPermissions(IEnumerable<string> permissions) =>
        Error.Validation("Roles.UnknownPermission", $"Unknown or non-assignable permission(s): {string.Join(", ", permissions)}. See GET /api/v1/permissions.");

    /// <summary>Availability names organizations that do not exist.</summary>
    public static Error UnknownOrganizations(IEnumerable<Guid> organizationIds) =>
        Error.Validation("Roles.UnknownOrganization", $"Unknown organization(s): {string.Join(", ", organizationIds)}.");

    /// <summary>Invalid name or description.</summary>
    public static Error Invalid(string description) => Error.Validation("Roles.Invalid", description);
}
