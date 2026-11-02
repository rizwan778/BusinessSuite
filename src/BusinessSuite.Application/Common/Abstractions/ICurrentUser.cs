namespace BusinessSuite.Application.Common.Abstractions;

/// <summary>
/// The user making the current request.
/// </summary>
/// <remarks>
/// <para>
/// Application services depend on this interface instead of on ASP.NET Core's
/// <c>HttpContext</c>, so they can be unit-tested and reused by background jobs.
/// The Web layer implements it from the authentication cookie (<c>HttpContextCurrentUser</c>).
/// </para>
/// <para>
/// The organization, permissions and Super Admin flag are resolved and authorized on the server for
/// every request (from the database); they are never taken from form fields, query strings or token contents.
/// </para>
/// </remarks>
public interface ICurrentUser
{
    /// <summary><see langword="true"/> when a user is signed in.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The signed-in user's id, or <see langword="null"/> for anonymous requests and system processes.</summary>
    Guid? UserId { get; }

    /// <summary>The sign-in name (the e-mail address in BusinessSuite), or <see langword="null"/>.</summary>
    string? UserName { get; }

    /// <summary>Friendly name for the UI, or <see langword="null"/> if the user has none.</summary>
    string? DisplayName { get; }

    /// <summary>The organization this request acts in, or <see langword="null"/> when none is selected.</summary>
    Guid? OrganizationId { get; }

    /// <summary>
    /// The user's role in <see cref="OrganizationId"/> (one of <c>OrganizationRoles.All</c>), or <see langword="null"/>
    /// when no organization is selected or a Super Admin acts in an organization they are not a member of.
    /// </summary>
    string? OrganizationRole { get; }

    /// <summary><see langword="true"/> for a Super Admin (every permission everywhere).</summary>
    bool IsSuperAdmin { get; }

    /// <summary><see langword="true"/> when the user holds <paramref name="permission"/> in this request.</summary>
    bool HasPermission(string permission);
}
