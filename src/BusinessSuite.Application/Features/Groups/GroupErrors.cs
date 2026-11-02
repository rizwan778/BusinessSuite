using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Groups;

/// <summary>Errors returned by group management. The codes are part of the API contract: do not rename them.</summary>
public static class GroupErrors
{
    /// <summary>
    /// The group does not exist OR belongs to another organization (deliberately the same, so other organizations'
    /// groups reveal nothing).
    /// </summary>
    public static readonly Error NotFound =
        Error.NotFound("Groups.NotFound", "No group with this id exists in your organization.");

    /// <summary>Groups belong to an organization and the request acts in none.</summary>
    public static readonly Error OrganizationRequired =
        Error.Validation("Groups.OrganizationRequired", "Select the organization to act in with the X-Organization-Id header.");

    /// <summary>The organization already has a group with this name.</summary>
    public static readonly Error DuplicateName =
        Error.Conflict("Groups.DuplicateName", "This organization already has a group with this name. Choose another name.");

    /// <summary>Someone else changed or deleted the group at the same moment.</summary>
    public static readonly Error ConcurrentChange =
        Error.Conflict("Groups.ConcurrentChange", "The group was changed by someone else at the same time. Reload and try again.");

    /// <summary>Members still belong to the group, so deleting it is not safe.</summary>
    public static Error InUse(int members) =>
        Error.Conflict("Groups.InUse", $"{members} member(s) still belong to this group. Move them to another group first.");

    /// <summary>The name or description is missing or too long.</summary>
    public static Error Invalid(string description) => Error.Validation("Groups.Invalid", description);
}
