using System.ComponentModel.DataAnnotations;
using BusinessSuite.Application.Common.Paging;
using BusinessSuite.Application.Features.Groups;
using BusinessSuite.Domain.Organizations;

namespace BusinessSuite.Web.Models.Api.Groups;

/// <summary>Query string of <c>GET /api/v1/groups</c>, e.g. <c>?search=sales&amp;page=2</c>.</summary>
public sealed class GroupListRequest
{
    /// <summary>Part of the group name (case-insensitive).</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    /// <summary>1-based page number.</summary>
    [Range(1, PageRequest.MaxPage)]
    public int Page { get; set; } = 1;

    /// <summary>Rows per page, 1 to 100.</summary>
    [Range(1, PageRequest.MaxPageSize)]
    public int PageSize { get; set; } = PageRequest.DefaultPageSize;

    public GroupListQuery ToQuery() => new() { Search = Search, Page = Page, PageSize = PageSize };
}

/// <summary>
/// JSON body of <c>POST /api/v1/groups</c>. The group belongs to the organization the request acts in; there is
/// deliberately no organization field.
/// </summary>
public sealed class CreateGroupRequest
{
    /// <summary>Display name, e.g. "Sales". Unique within the organization.</summary>
    [Required(ErrorMessage = "Enter a group name.")]
    [StringLength(Group.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [StringLength(Group.DescriptionMaxLength)]
    public string? Description { get; set; }

    public CreateGroupCommand ToCommand() => new(Name, Description);
}

/// <summary>
/// JSON body of <c>PUT /api/v1/groups/{id}</c>: name and description, both replaced. Renaming moves the members.
/// </summary>
public sealed class UpdateGroupRequest
{
    [Required(ErrorMessage = "Enter a group name.")]
    [StringLength(Group.NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    [StringLength(Group.DescriptionMaxLength)]
    public string? Description { get; set; }

    public UpdateGroupCommand ToCommand() => new(Name, Description);
}
