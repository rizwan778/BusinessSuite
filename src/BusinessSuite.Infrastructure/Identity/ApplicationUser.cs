using BusinessSuite.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace BusinessSuite.Infrastructure.Identity;

/// <summary>
/// A person who can sign in. Extends ASP.NET Core Identity's user with BusinessSuite fields.
/// </summary>
/// <remarks>
/// <para>
/// This class lives in Infrastructure because it inherits from an ASP.NET Core Identity type.
/// Business code never uses it directly: it refers to users by id through
/// <c>ICurrentUser.UserId</c>.
/// </para>
/// <para>
/// The sign-in name (<c>UserName</c>) is always the e-mail address.
/// Which organizations a user belongs to, and with which role, is modelled separately
/// (organization memberships, Phase 2) because one person may work for several businesses.
/// </para>
/// </remarks>
public sealed class ApplicationUser : IdentityUser<Guid>, IAuditable
{
    /// <summary>Maximum length of <see cref="DisplayName"/> (also used by the database column).</summary>
    public const int DisplayNameMaxLength = 100;

    /// <summary>Friendly name shown in the UI. Optional.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Deactivated users cannot sign in. Deactivate users instead of deleting them so that
    /// historical records (invoices, stock movements) keep a valid author.
    /// </summary>
    public bool IsActive { get; private set; } = true;

    /// <inheritdoc />
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <inheritdoc />
    public Guid? CreatedByUserId { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    /// <inheritdoc />
    public Guid? UpdatedByUserId { get; private set; }

    /// <summary>Allows the user to sign in again.</summary>
    public void Activate() => IsActive = true;

    /// <summary>
    /// Blocks future sign-ins. Callers must also update the security stamp
    /// (<c>UserManager.UpdateSecurityStampAsync</c>) so existing sessions end promptly.
    /// </summary>
    public void Deactivate() => IsActive = false;
}
