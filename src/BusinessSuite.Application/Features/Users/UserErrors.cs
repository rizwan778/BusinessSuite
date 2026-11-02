using BusinessSuite.Application.Common.Results;
using BusinessSuite.Domain.Organizations;

namespace BusinessSuite.Application.Features.Users;

/// <summary>
/// Errors returned by user management. The codes are part of the API contract: do not rename them.
/// </summary>
public static class UserErrors
{
    /// <summary>
    /// The user does not exist OR is not visible to the caller (not a member of the caller's organization).
    /// Deliberately the same, so ids from other organizations reveal nothing.
    /// </summary>
    public static readonly Error NotFound =
        Error.NotFound("Users.NotFound", "No user with this id exists in your organization.");

    /// <summary>The operation needs an organization and the request acts in none.</summary>
    public static readonly Error OrganizationRequired =
        Error.Validation("Users.OrganizationRequired", "Select the organization to act in with the X-Organization-Id header.");

    /// <summary>An organization user tried to filter by another organization.</summary>
    public static readonly Error OrganizationFilterNotAllowed =
        Error.Forbidden("Users.OrganizationFilterNotAllowed", "Only a Super Admin can list users of other organizations.");

    /// <summary>Sorting by role or department needs an organization.</summary>
    public static readonly Error SortNeedsOrganization =
        Error.Validation("Users.InvalidSort", "Sorting by role or department needs an organization: filter by organizationId or send the X-Organization-Id header.");

    /// <summary>Nobody may change their own role or status (prevents self-promotion and locking yourself out).</summary>
    public static readonly Error CannotChangeOwnAccess =
        Error.Forbidden("Users.CannotChangeOwnAccess", "You cannot change your own role or status. Ask another administrator.");

    /// <summary>Platform accounts (Super Admin, Platform Administrator) are managed by Super Admins only.</summary>
    public static readonly Error PlatformAccountProtected =
        Error.Forbidden("Users.PlatformAccountProtected", "This is a platform account; only a Super Admin can change it.");

    /// <summary>Only an Owner can make someone an Owner, or change an Owner.</summary>
    public static readonly Error OwnerRequired =
        Error.Forbidden("Users.OwnerRequired", $"Only an {OrganizationRoles.Owner} can assign the {OrganizationRoles.Owner} role or change an {OrganizationRoles.Owner}.");

    /// <summary>The change would leave the organization without an active Owner.</summary>
    public static readonly Error LastOwner =
        Error.Conflict("Users.LastOwner", $"This is the organization's last active {OrganizationRoles.Owner}. Make someone else an {OrganizationRoles.Owner} first.");

    /// <summary>
    /// Deactivating the ACCOUNT would leave organizations without an active Owner (same code as <see cref="LastOwner"/>:
    /// the same rule, reached through the account instead of one membership).
    /// </summary>
    public static Error LastOwnerAccount(int organizations) =>
        Error.Conflict("Users.LastOwner", $"This account is the last active {OrganizationRoles.Owner} of {organizations} active organization(s). Make someone else an {OrganizationRoles.Owner} there, or suspend the organization, first.");

    /// <summary>The e-mail address already belongs to a member of this organization.</summary>
    public static readonly Error AlreadyMember =
        Error.Conflict("Users.AlreadyMember", "A user with this e-mail address is already a member of this organization.");

    /// <summary>An existing account cannot be added by e-mail (that needs the user's consent: invitations, planned).</summary>
    public static readonly Error EmailBelongsToExistingAccount =
        Error.Conflict("Account.DuplicateEmail", "An account with this e-mail address already exists. Existing accounts cannot be added to an organization yet (invitations are planned).");

    /// <summary>Someone else changed the user at the same moment.</summary>
    public static readonly Error ConcurrentChange =
        Error.Conflict("Users.ConcurrentChange", "The user was changed by someone else at the same time. Reload and try again.");

    /// <summary>A non-Super Admin tried to assign, or to change a holder of, a role granting more than they hold.</summary>
    public static readonly Error RoleExceedsYourPermissions =
        Error.Forbidden("Users.RoleExceedsYourPermissions", "That role grants permissions you do not hold, so you cannot assign it or change its holders.");

    /// <summary>The role was deleted while it was being assigned.</summary>
    public static readonly Error RoleNoLongerExists =
        Error.Conflict("Users.RoleNoLongerExists", "The role was deleted at the same moment. Choose another role.");

    /// <summary>No role with this name or id is available to the organization.</summary>
    public static Error InvalidRole(string? role) =>
        Error.Validation("Users.InvalidRole", $"'{role}' is not a role available in this organization. See GET /api/v1/roles.");

    /// <summary>A field is too long or otherwise invalid.</summary>
    public static Error Invalid(string description) => Error.Validation("Users.Invalid", description);
}
