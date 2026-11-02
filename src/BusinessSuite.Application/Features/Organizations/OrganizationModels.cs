using System.Text.Json.Serialization;

namespace BusinessSuite.Application.Features.Organizations;

/// <summary>An organization (a customer business, or tenant) as returned by the organizations API.</summary>
/// <param name="Id">The organization's id; send it as <c>X-Organization-Id</c> to act in it.</param>
/// <param name="Name">Display name.</param>
/// <param name="IsActive">
/// <see langword="false"/> when suspended: its members have no access to it (no permissions, no organization
/// claims) from their next request.
/// </param>
/// <param name="MemberCount">Members, active or not (the <c>totalCount</c> of <c>GET /api/v1/organizations/{id}/users</c>).</param>
/// <param name="CreatedAtUtc">When the organization was created.</param>
/// <param name="UpdatedAtUtc">When it was last changed, if ever.</param>
public sealed record OrganizationDetails(
    Guid Id,
    string Name,
    bool IsActive,
    int MemberCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>Status filter for <see cref="OrganizationListQuery"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OrganizationStatusFilter>))]
public enum OrganizationStatusFilter
{
    /// <summary>Organizations whose members have access.</summary>
    Active = 1,

    /// <summary>Suspended organizations.</summary>
    Suspended = 2,
}

/// <summary>Filters and paging for <see cref="IOrganizationService.ListAsync"/>. Every value is optional.</summary>
public sealed record OrganizationListQuery
{
    /// <summary>Part of the organization name (case-insensitive).</summary>
    public string? Search { get; init; }

    /// <summary>Only active or only suspended organizations.</summary>
    public OrganizationStatusFilter? Status { get; init; }

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page (capped at <c>PageRequest.MaxPageSize</c>).</summary>
    public int PageSize { get; init; } = Common.Paging.PageRequest.DefaultPageSize;
}

/// <summary>Input for <see cref="IOrganizationService.CreateAsync"/>.</summary>
/// <param name="Name">Display name.</param>
/// <param name="InitialAdmin">
/// Optional first administrator, created together with the organization (Super Admins only). They become the
/// organization's Owner. <see langword="null"/>: the organization starts without members.
/// </param>
public sealed record CreateOrganizationCommand(string Name, InitialAdminCommand? InitialAdmin = null);

/// <summary>The person who administers a new organization from the start.</summary>
/// <param name="Email">E-mail address, also the sign-in name. Must not belong to an existing account.</param>
/// <param name="DisplayName">Optional friendly name.</param>
/// <param name="PhoneNumber">Optional phone number.</param>
/// <param name="Password">
/// Optional initial password (checked against the password policy). When omitted, the person is e-mailed a code to choose
/// their own password, which is the recommended way: nobody else ever knows it.
/// </param>
public sealed record InitialAdminCommand(string Email, string? DisplayName, string? PhoneNumber, string? Password)
{
    /// <summary>Leaves the password out (a record's default <c>ToString()</c> prints every property).</summary>
    public override string ToString() =>
        $"{nameof(InitialAdminCommand)} {{ Email = {Email}, DisplayName = {DisplayName} }}";
}

/// <summary>Input for <see cref="IOrganizationService.UpdateAsync"/>.</summary>
/// <param name="Name">The new display name.</param>
public sealed record UpdateOrganizationCommand(string Name);
