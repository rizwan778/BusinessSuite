using System.Text.Json.Serialization;

namespace BusinessSuite.Application.Features.Users;

/// <summary>A user as seen by user management (list rows and details).</summary>
/// <param name="Id">The user's id.</param>
/// <param name="Email">Sign-in e-mail address.</param>
/// <param name="DisplayName">Friendly name, if set.</param>
/// <param name="PhoneNumber">Phone number, if set.</param>
/// <param name="IsActive">The ACCOUNT can sign in. A deactivated account cannot sign in anywhere.</param>
/// <param name="IsLockedOut">Temporarily locked after too many failed sign-ins.</param>
/// <param name="EmailConfirmed">The user has proved they own the e-mail address (e.g. by completing a password reset).</param>
/// <param name="PlatformRoles">Platform roles (<c>SuperAdmin</c>, <c>PlatformAdministrator</c>); shown to Super Admins only, otherwise empty.</param>
/// <param name="CreatedAtUtc">When the account was created.</param>
/// <param name="UpdatedAtUtc">When the account was last changed, if ever.</param>
/// <param name="Memberships">
/// The user's organizations. Organization users see only the membership in their own organization;
/// Super Admins see every membership (or only the filtered organization's, when listing with <c>organizationId</c>).
/// </param>
public sealed record UserDetails(
    Guid Id,
    string Email,
    string? DisplayName,
    string? PhoneNumber,
    bool IsActive,
    bool IsLockedOut,
    bool EmailConfirmed,
    IReadOnlyList<string> PlatformRoles,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    IReadOnlyList<UserMembershipDetails> Memberships);

/// <summary>A user's place in one organization.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="OrganizationName">The organization's display name.</param>
/// <param name="Role">Name of the role in that organization.</param>
/// <param name="Department">Department or group, if assigned.</param>
/// <param name="IsActive">The membership gives access. Inactive memberships give no access to that organization.</param>
/// <param name="RoleId">Id of the role (<c>/api/v1/roles/{id}</c>).</param>
public sealed record UserMembershipDetails(
    Guid OrganizationId,
    string OrganizationName,
    string Role,
    string? Department,
    bool IsActive,
    Guid RoleId);

/// <summary>Filters, sorting and paging for <see cref="IUserManagementService.ListAsync"/>. Every value is optional.</summary>
public sealed record UserListQuery
{
    /// <summary>Part of the e-mail address or display name (case-insensitive).</summary>
    public string? Search { get; init; }

    /// <summary>
    /// Only users of this organization. SUPER ADMINS ONLY: organization users are always limited to their own
    /// organization, and naming another one is refused.
    /// </summary>
    public Guid? OrganizationId { get; init; }

    /// <summary>Only users with this role: its id, or its name (case-insensitive).</summary>
    public string? Role { get; init; }

    /// <summary>Only users in this department (exact name, case-insensitive).</summary>
    public string? Department { get; init; }

    /// <summary>Only active or only inactive users (see <see cref="UserStatusFilter"/>).</summary>
    public UserStatusFilter? Status { get; init; }

    /// <summary>Sort column; default <see cref="UserSortField.Email"/>.</summary>
    public UserSortField SortBy { get; init; } = UserSortField.Email;

    /// <summary>Sort direction; default ascending.</summary>
    public SortDirection SortDirection { get; init; } = SortDirection.Asc;

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page (capped at <c>PageRequest.MaxPageSize</c>).</summary>
    public int PageSize { get; init; } = Common.Paging.PageRequest.DefaultPageSize;
}

/// <summary>
/// Status filter. Inside an organization a user is <see cref="Active"/> when both the account and the membership
/// are active; across all organizations (Super Admin, no organization filter) only the account counts.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserStatusFilter>))]
public enum UserStatusFilter
{
    Active = 1,
    Inactive = 2,
}

/// <summary>Columns users can be sorted by. <see cref="Role"/> and <see cref="Department"/> need an organization.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserSortField>))]
public enum UserSortField
{
    Email = 0,
    DisplayName = 1,
    CreatedAt = 2,
    Role = 3,
    Department = 4,
}

/// <summary>Sort direction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SortDirection>))]
public enum SortDirection
{
    Asc = 0,
    Desc = 1,
}

/// <summary>Input for <see cref="IUserManagementService.CreateAsync"/>.</summary>
/// <param name="Email">E-mail address, also the sign-in name. Must not belong to an existing account.</param>
/// <param name="DisplayName">Optional friendly name.</param>
/// <param name="PhoneNumber">Optional phone number.</param>
/// <param name="Role">Role in the organization the request acts in: its id or its name; must be available there.</param>
/// <param name="Department">Optional department or group.</param>
/// <param name="Password">
/// Optional initial password (checked against the password policy). When omitted, the user is e-mailed a code to
/// choose their own password, which is the recommended way: nobody else ever knows it.
/// </param>
public sealed record CreateUserCommand(
    string Email,
    string? DisplayName,
    string? PhoneNumber,
    string Role,
    string? Department,
    string? Password)
{
    /// <summary>Leaves the password out (a record's default <c>ToString()</c> prints every property).</summary>
    public override string ToString() =>
        $"{nameof(CreateUserCommand)} {{ Email = {Email}, DisplayName = {DisplayName}, Role = {Role}, Department = {Department} }}";
}

/// <summary>Input for <see cref="IUserManagementService.UpdateAsync"/>: the profile fields an administrator may change.</summary>
/// <param name="DisplayName">Friendly name; <see langword="null"/> or blank removes it.</param>
/// <param name="PhoneNumber">Phone number; <see langword="null"/> or blank removes it.</param>
public sealed record UpdateUserCommand(string? DisplayName, string? PhoneNumber);
