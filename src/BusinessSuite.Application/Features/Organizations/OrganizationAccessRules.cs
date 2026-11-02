using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;

namespace BusinessSuite.Application.Features.Organizations;

/// <summary>
/// Who may see and change which organizations. Pure and deterministic, so every rule is unit-tested.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Platform operators</b> (<c>platform.organizations.manage</c>: Super Admins and Platform Administrators) see
/// every organization and are the only ones who create, suspend or reactivate them.</item>
/// <item><b>Organization scope:</b> everyone else reaches only the organization the request acts in
/// (<c>ICurrentUser.OrganizationId</c>, resolved on the server). Any other id, however it is reached, is
/// <see cref="OrganizationErrors.NotFound"/>. A client-supplied id never widens this.</item>
/// </list>
/// What a caller needs to be allowed through the door (<c>organization.view</c>, <c>organization.manage</c>, ...) is
/// checked by the controllers' permission attributes; these rules decide <i>which</i> organizations that reaches.
/// </remarks>
public static class OrganizationAccessRules
{
    /// <summary>
    /// <see langword="true"/> for the people who operate the platform. Super Admins hold every permission, so they
    /// qualify too.
    /// </summary>
    public static bool IsPlatformOperator(ICurrentUser actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.IsSuperAdmin || actor.HasPermission(Permissions.Platform.OrganizationsManage);
    }

    /// <summary>
    /// The organization a list is limited to: <see langword="null"/> means every organization (platform operators).
    /// </summary>
    public static Result<Guid?> ListScope(ICurrentUser actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (IsPlatformOperator(actor))
        {
            return (Guid?)null;
        }

        return actor.OrganizationId is { } organizationId
            ? organizationId
            : OrganizationErrors.OrganizationRequired;
    }

    /// <summary>May <paramref name="actor"/> see (and, with the right permission, change) <paramref name="organizationId"/>?</summary>
    public static Result CheckAccess(ICurrentUser actor, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (IsPlatformOperator(actor))
        {
            return Result.Success();
        }

        if (actor.OrganizationId is not { } acting)
        {
            return OrganizationErrors.OrganizationRequired;
        }

        return acting == organizationId ? Result.Success() : OrganizationErrors.NotFound;
    }

    /// <summary>May <paramref name="actor"/> create organizations? Platform operators only.</summary>
    public static Result CheckCreate(ICurrentUser actor) => CheckPlatformOperator(actor);

    /// <summary>
    /// May <paramref name="actor"/> create an organization together with its first administrator? Super Admins only:
    /// this hands out an Owner account, and Platform Administrators have no access inside organizations.
    /// </summary>
    public static Result CheckCreateInitialAdmin(ICurrentUser actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.IsSuperAdmin ? Result.Success() : OrganizationErrors.SuperAdminRequired;
    }

    /// <summary>
    /// May <paramref name="actor"/> suspend or reactivate organizations? Platform operators only: an organization must
    /// not be able to suspend (or un-suspend) itself.
    /// </summary>
    public static Result CheckChangeStatus(ICurrentUser actor) => CheckPlatformOperator(actor);

    private static Result CheckPlatformOperator(ICurrentUser actor) =>
        IsPlatformOperator(actor) ? Result.Success() : OrganizationErrors.PlatformOperatorRequired;
}
