using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;
using BusinessSuite.Application.Common.Security;

namespace BusinessSuite.Application.Features.Audit;

/// <summary>
/// Who may read which audit entries. Pure and deterministic, so every rule is unit-tested.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Platform operators</b> (Super Admins, and holders of <c>platform.audit.view</c>: Platform Administrators) read
/// every entry, including platform-wide ones (no organization), and may filter by any organization.</item>
/// <item><b>Organization isolation:</b> everyone else reads only the entries of the organization the request acts in
/// (<c>ICurrentUser.OrganizationId</c>, resolved on the server). Naming another organization is refused; an entry of
/// another organization, or a platform-wide one, is <see cref="AuditErrors.NotFound"/>, exactly like a missing id.</item>
/// </list>
/// The permission to come through the door (<c>audit.view</c> or <c>platform.audit.view</c>) is checked by the controller.
/// </remarks>
public static class AuditAccessRules
{
    /// <summary><see langword="true"/> for the people who may read every organization's audit log.</summary>
    public static bool IsPlatformOperator(ICurrentUser actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.IsSuperAdmin || actor.HasPermission(Permissions.Platform.AuditView);
    }

    /// <summary>
    /// The organization a list is limited to: <see langword="null"/> means every entry, platform-wide ones included
    /// (platform operators only).
    /// </summary>
    public static Result<Guid?> ListScope(ICurrentUser actor, Guid? requestedOrganizationId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (IsPlatformOperator(actor))
        {
            return requestedOrganizationId;
        }

        if (actor.OrganizationId is not { } organizationId)
        {
            return AuditErrors.OrganizationRequired;
        }

        if (requestedOrganizationId is { } requested && requested != organizationId)
        {
            return AuditErrors.OrganizationFilterNotAllowed;
        }

        return organizationId;
    }

    /// <summary>May <paramref name="actor"/> read an entry of <paramref name="entryOrganizationId"/> (<see langword="null"/>: platform-wide)?</summary>
    public static bool CanRead(ICurrentUser actor, Guid? entryOrganizationId)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (IsPlatformOperator(actor))
        {
            return true;
        }

        return entryOrganizationId is { } entryOrganization && actor.OrganizationId == entryOrganization;
    }
}
