namespace BusinessSuite.Application.Common.Security;

/// <summary>
/// Names of BusinessSuite-specific claims.
/// </summary>
/// <remarks>
/// <para>
/// Stored in the cookie / tokens: <see cref="DisplayName"/> and <see cref="SessionId"/> only (plus Identity's
/// user id, name, security stamp and platform roles). Keep them small: they travel with every request.
/// </para>
/// <para>
/// Added on the server for each request, never stored in a cookie or token (so they are always current
/// and cannot be forged): <see cref="OrganizationId"/>, <see cref="OrganizationRole"/>,
/// <see cref="Permission"/> and <see cref="SuperAdmin"/>.
/// </para>
/// </remarks>
public static class AppClaimTypes
{
    /// <summary>The user's display name, shown in the top bar.</summary>
    public const string DisplayName = "bs:display_name";

    /// <summary>
    /// Id of the API session (<c>identity.UserSessions</c>) inside access and refresh tokens.
    /// Logout revokes the session, which invalidates every token carrying this id.
    /// </summary>
    public const string SessionId = "bs:sid";

    /// <summary>The organization this request acts in (per request; see <see cref="AppClaimTypes"/> remarks).</summary>
    public const string OrganizationId = "bs:org_id";

    /// <summary>The user's role in <see cref="OrganizationId"/>; absent for a Super Admin who is not a member.</summary>
    public const string OrganizationRole = "bs:org_role";

    /// <summary>One claim per permission the user holds for this request.</summary>
    public const string Permission = "bs:permission";

    /// <summary>Present with value "true" when the user is a Super Admin.</summary>
    public const string SuperAdmin = "bs:super_admin";

    /// <summary>Authentication type of the per-request identity that carries the claims above.</summary>
    public const string AccessIdentityType = "BusinessSuite.Access";
}
