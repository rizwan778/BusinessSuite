using BusinessSuite.Application.Common.Abstractions;
using BusinessSuite.Application.Common.Results;

namespace BusinessSuite.Application.Features.Groups;

/// <summary>
/// Which organization's groups a caller works with. Pure and deterministic, so it is unit-tested.
/// </summary>
/// <remarks>
/// Groups always belong to the organization the request acts in (<c>ICurrentUser.OrganizationId</c>, resolved and
/// verified on the server): organization users act in their own, Super Admins in the one named by
/// <c>X-Organization-Id</c>. There is no organization id in any request, so a group of another organization can only be
/// "not found". Whether the caller may view or change groups is decided by the controllers' permission attributes
/// (<c>users.view</c>, <c>users.manage</c>).
/// </remarks>
public static class GroupAccessRules
{
    /// <summary>The organization whose groups the caller works with.</summary>
    public static Result<Guid> ActingOrganization(ICurrentUser actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.OrganizationId is { } organizationId
            ? organizationId
            : GroupErrors.OrganizationRequired;
    }
}
