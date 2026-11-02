using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Organizations;

/// <summary>Errors returned by organization management. The codes are part of the API contract: do not rename them.</summary>
public static class OrganizationErrors
{
    /// <summary>
    /// The organization does not exist OR the caller may not see it (deliberately the same, so ids of other
    /// organizations reveal nothing).
    /// </summary>
    public static readonly Error NotFound =
        Error.NotFound("Organizations.NotFound", "No organization with this id is available to you.");

    /// <summary>The operation needs an organization and the request acts in none.</summary>
    public static readonly Error OrganizationRequired =
        Error.Validation("Organizations.OrganizationRequired", "Select the organization to act in with the X-Organization-Id header.");

    /// <summary>Creating, suspending and reactivating organizations is platform business.</summary>
    public static readonly Error PlatformOperatorRequired =
        Error.Forbidden("Organizations.PlatformOperatorRequired", "Only the platform operators (Super Admins and Platform Administrators) can create organizations or suspend and reactivate them.");

    /// <summary>Giving a new organization its first administrator creates an Owner account: Super Admins only.</summary>
    public static readonly Error SuperAdminRequired =
        Error.Forbidden("Organizations.SuperAdminRequired", "Only a Super Admin can create an organization together with its first administrator. Create the organization without one, or ask a Super Admin.");

    /// <summary>Someone else changed the organization at the same moment.</summary>
    public static readonly Error ConcurrentChange =
        Error.Conflict("Organizations.ConcurrentChange", "The organization was changed by someone else at the same time. Reload and try again.");

    /// <summary>The name is missing or too long.</summary>
    public static Error Invalid(string description) => Error.Validation("Organizations.Invalid", description);
}
