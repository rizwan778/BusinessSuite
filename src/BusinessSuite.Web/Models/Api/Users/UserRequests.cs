using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Users;

namespace BusinessSuite.Web.Models.Api.Users;

/// <summary>
/// Query string of <c>GET /api/v1/users</c>, e.g. <c>?search=ali&amp;role=SalesOperator&amp;status=active&amp;sortBy=createdAt&amp;sortDirection=desc&amp;page=2</c>.
/// </summary>
public sealed class UserListRequest
{
    /// <summary>Part of the e-mail address or display name (case-insensitive).</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary>
    /// Only users of this organization. <b>Super Admins only</b>: organization users always see their own
    /// organization, and naming another one answers 403 <c>Users.OrganizationFilterNotAllowed</c>.
    /// </summary>
    public Guid? OrganizationId { get; set; }

    /// <summary>Only users with this role: its id or name (see <c>GET /api/v1/roles</c>), e.g. <c>SalesOperator</c>.</summary>
    [StringLength(100)]
    public string? Role { get; set; }

    /// <summary>Only users in this department or group (exact name, case-insensitive).</summary>
    [StringLength(100)]
    public string? Department { get; set; }

    /// <summary><c>active</c> or <c>inactive</c>. In an organization, active means account AND membership are active.</summary>
    public UserStatusFilter? Status { get; set; }

    /// <summary><c>email</c> (default), <c>displayName</c>, <c>createdAt</c>, <c>role</c> or <c>department</c> (the last two need an organization).</summary>
    public UserSortField SortBy { get; set; } = UserSortField.Email;

    /// <summary><c>asc</c> (default) or <c>desc</c>.</summary>
    public SortDirection SortDirection { get; set; } = SortDirection.Asc;

    /// <summary>1-based page number.</summary>
    [Range(1, PageRequest.MaxPage)]
    public int Page { get; set; } = 1;

    /// <summary>Rows per page, 1 to 100.</summary>
    [Range(1, PageRequest.MaxPageSize)]
    public int PageSize { get; set; } = PageRequest.DefaultPageSize;

    public UserListQuery ToQuery() => new()
    {
        Search = Search,
        OrganizationId = OrganizationId,
        Role = Role,
        Department = Department,
        Status = Status,
        SortBy = SortBy,
        SortDirection = SortDirection,
        Page = Page,
        PageSize = PageSize,
    };
}

/// <summary>
/// JSON body of <c>POST /api/v1/users</c>. The user joins the organization the request acts in; there is
/// deliberately no organization field.
/// </summary>
public sealed class CreateUserRequest
{
    [Required(ErrorMessage = "Enter the e-mail address.")]
    [EmailAddress(ErrorMessage = "Enter a valid e-mail address.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [StringLength(100)]
    public string? DisplayName { get; set; }

    [Phone(ErrorMessage = "Enter a valid phone number.")]
    [StringLength(32)]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Role id or name, among the roles available to the organization (<c>GET /api/v1/roles</c>). Only an Owner (or a
    /// Super Admin) can create an Owner, and you can only assign roles whose permissions you hold.
    /// </summary>
    [Required(ErrorMessage = "Choose a role.")]
    [StringLength(100)]
    public string Role { get; set; } = string.Empty;

    /// <summary>Department or group, e.g. "Sales".</summary>
    [StringLength(100)]
    public string? Department { get; set; }

    /// <summary>
    /// Optional initial password. Leave it out (recommended) and the user is e-mailed a code to choose their own.
    /// </summary>
    [StringLength(128)]
    public string? Password { get; set; }

    public CreateUserCommand ToCommand() => new(Email, DisplayName, PhoneNumber, Role, Department, Password);
}

/// <summary>
/// JSON body of <c>PUT /api/v1/users/{id}</c>: the profile fields an administrator may change. Both are replaced;
/// <see langword="null"/> clears a field. E-mail, password, role, department and status have their own endpoints.
/// </summary>
public sealed class UpdateUserRequest
{
    [StringLength(100)]
    public string? DisplayName { get; set; }

    [Phone(ErrorMessage = "Enter a valid phone number.")]
    [StringLength(32)]
    public string? PhoneNumber { get; set; }

    public UpdateUserCommand ToCommand() => new(DisplayName, PhoneNumber);
}

/// <summary>JSON body of <c>PATCH /api/v1/users/{id}/role</c>.</summary>
public sealed class ChangeUserRoleRequest
{
    /// <summary>Role id or name, among the roles available to the organization (<c>GET /api/v1/roles</c>).</summary>
    [Required(ErrorMessage = "Choose a role.")]
    [StringLength(100)]
    public string Role { get; set; } = string.Empty;
}

/// <summary>JSON body of <c>PATCH /api/v1/users/{id}/group</c>.</summary>
public sealed class ChangeUserGroupRequest
{
    /// <summary>Department or group; <see langword="null"/> or empty removes it.</summary>
    [StringLength(100)]
    public string? Department { get; set; }
}

/// <summary>JSON body of <c>PATCH /api/v1/users/{id}/status</c>.</summary>
public sealed class ChangeUserStatusRequest
{
    /// <summary><see langword="true"/> to activate, <see langword="false"/> to deactivate.</summary>
    [Required(ErrorMessage = "Say whether the user is active.")]
    public bool? IsActive { get; set; }
}
