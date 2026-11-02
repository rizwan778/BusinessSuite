namespace BusinessSuite.Application.Features.Groups;

/// <summary>A group (department) as returned by the groups API.</summary>
/// <param name="Id">The group's id.</param>
/// <param name="Name">Display name, unique within the organization; it is the <c>department</c> of the members.</param>
/// <param name="Description">What the group is for.</param>
/// <param name="MemberCount">Members whose department is this group, active or not.</param>
/// <param name="CreatedAtUtc">When the group was created.</param>
/// <param name="UpdatedAtUtc">When it was last changed, if ever.</param>
public sealed record GroupDetails(
    Guid Id,
    string Name,
    string? Description,
    int MemberCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>Filters and paging for <see cref="IGroupService.ListAsync"/>. Every value is optional.</summary>
public sealed record GroupListQuery
{
    /// <summary>Part of the group name (case-insensitive).</summary>
    public string? Search { get; init; }

    /// <summary>1-based page number.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Rows per page (capped at <c>PageRequest.MaxPageSize</c>).</summary>
    public int PageSize { get; init; } = Common.Paging.PageRequest.DefaultPageSize;
}

/// <summary>Input for <see cref="IGroupService.CreateAsync"/>.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Description">Optional description.</param>
public sealed record CreateGroupCommand(string Name, string? Description);

/// <summary>Input for <see cref="IGroupService.UpdateAsync"/>: name and description (both replaced).</summary>
/// <param name="Name">The new display name; members are moved to it.</param>
/// <param name="Description">The new description; <see langword="null"/> or blank removes it.</param>
public sealed record UpdateGroupCommand(string Name, string? Description);
