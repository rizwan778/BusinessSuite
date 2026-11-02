using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Organizations;
using BusinessSuite.Application.Features.Roles;
using BusinessSuite.Application.Features.Users;
using BusinessSuite.Domain.Organizations;

namespace BusinessSuite.Web.Models.Api.Organizations;

/// <summary>Query string of <c>GET /api/v1/organizations</c>, e.g. <c>?search=traders&amp;status=active&amp;page=2</c>.</summary>
public sealed class OrganizationListRequest
{
    /// <summary>Part of the organization name (case-insensitive).</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary><c>active</c> or <c>suspended</c>.</summary>
    public OrganizationStatusFilter? Status { get; set; }

    /// <summary>1-based page number.</summary>
    [Range(1, PageRequest.MaxPage)]
    public int Page { get; set; } = 1;

    /// <summary>Rows per page, 1 to 100.</summary>
    [Range(1, PageRequest.MaxPageSize)]
    public int PageSize { get; set; } = PageRequest.DefaultPageSize;

    public OrganizationListQuery ToQuery() => new() { Search = Search, Status = Status, Page = Page, PageSize = PageSize };
}

/// <summary>JSON body of <c>POST /api/v1/organizations</c>.</summary>
public sealed class CreateOrganizationRequest
{
    /// <summary>Display name, e.g. "Al-Noor Traders".</summary>
    [Required(ErrorMessage = "Enter the organization name.")]
    [StringLength(Organization.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// <b>Super Admins only.</b> The organization's first administrator, created together with it and made its Owner.
    /// Leave it out to create an empty organization.
    /// </summary>
    public InitialAdminRequest? InitialAdmin { get; set; }

    public CreateOrganizationCommand ToCommand() => new(Name, InitialAdmin?.ToCommand());
}

/// <summary>The first administrator of a new organization (<c>initialAdmin</c> of <c>POST /api/v1/organizations</c>).</summary>
public sealed class InitialAdminRequest
{
    /// <summary>E-mail address and sign-in name. Must not belong to an existing account.</summary>
    [Required(ErrorMessage = "Enter the e-mail address of the first administrator.")]
    [EmailAddress(ErrorMessage = "Enter a valid e-mail address.")]
    [StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [StringLength(100)]
    public string? DisplayName { get; set; }

    [Phone(ErrorMessage = "Enter a valid phone number.")]
    [StringLength(32)]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// Optional initial password. Leave it out (recommended) and the administrator is e-mailed a code to choose their own
    /// (<c>POST /api/v1/auth/reset-password</c>), so nobody else ever knows it.
    /// </summary>
    [StringLength(128)]
    public string? Password { get; set; }

    public InitialAdminCommand ToCommand() => new(Email, DisplayName, PhoneNumber, Password);
}

/// <summary>JSON body of <c>PUT /api/v1/organizations/{id}</c>.</summary>
public sealed class UpdateOrganizationRequest
{
    /// <summary>The new display name.</summary>
    [Required(ErrorMessage = "Enter the organization name.")]
    [StringLength(Organization.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    public UpdateOrganizationCommand ToCommand() => new(Name);
}

/// <summary>JSON body of <c>PATCH /api/v1/organizations/{id}/status</c>.</summary>
public sealed class ChangeOrganizationStatusRequest
{
    /// <summary><see langword="true"/> to reactivate, <see langword="false"/> to suspend.</summary>
    [Required(ErrorMessage = "Say whether the organization is active.")]
    public bool? IsActive { get; set; }
}

/// <summary>
/// Query string of <c>GET /api/v1/organizations/{id}/users</c>: the filters of <c>GET /api/v1/users</c>, without
/// <c>organizationId</c> (the organization is in the path).
/// </summary>
public sealed class OrganizationUsersRequest
{
    /// <summary>Part of the e-mail address or display name (case-insensitive).</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary>Only users with this role: its id or name (see <c>GET /api/v1/organizations/{id}/roles</c>).</summary>
    [StringLength(100)]
    public string? Role { get; set; }

    /// <summary>Only users in this department or group (exact name, case-insensitive).</summary>
    [StringLength(100)]
    public string? Department { get; set; }

    /// <summary><c>active</c> or <c>inactive</c>: account AND membership active, or not.</summary>
    public UserStatusFilter? Status { get; set; }

    /// <summary><c>email</c> (default), <c>displayName</c>, <c>createdAt</c>, <c>role</c> or <c>department</c>.</summary>
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
        Role = Role,
        Department = Department,
        Status = Status,
        SortBy = SortBy,
        SortDirection = SortDirection,
        Page = Page,
        PageSize = PageSize,
    };
}

/// <summary>Query string of <c>GET /api/v1/organizations/{id}/roles</c>.</summary>
public sealed class OrganizationRolesRequest
{
    /// <summary>Part of the role name (case-insensitive).</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    public RoleListQuery ToQuery() => new() { Search = Search };
}
