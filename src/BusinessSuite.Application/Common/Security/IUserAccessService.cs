namespace BusinessSuite.Application.Common.Security;

/// <summary>Loads a user's roles and memberships and returns their <see cref="UserAccess"/>.</summary>
/// <remarks>Implemented in Infrastructure (database); the rules live in <see cref="UserAccessCalculator"/>.</remarks>
public interface IUserAccessService
{
    /// <param name="userId">The signed-in user.</param>
    /// <param name="requestedOrganizationId">The organization the client asked to act in (e.g. the <c>X-Organization-Id</c> header).</param>
    /// <param name="cancellationToken">Cancels the database queries.</param>
    Task<UserAccess> GetAccessAsync(Guid userId, Guid? requestedOrganizationId, CancellationToken cancellationToken = default);
}
