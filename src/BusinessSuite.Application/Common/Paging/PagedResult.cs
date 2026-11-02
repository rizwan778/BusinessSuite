namespace BusinessSuite.Application.Common.Paging;

/// <summary>
/// One page of results plus the information a pager control needs.
/// </summary>
/// <typeparam name="T">Item type, normally a DTO (never an EF Core entity).</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Creates a page of results.</summary>
    /// <param name="items">Rows on this page.</param>
    /// <param name="request">The normalised request that produced the page.</param>
    /// <param name="totalCount">Total number of rows across all pages.</param>
    public PagedResult(IReadOnlyList<T> items, PageRequest request, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);

        Items = items;
        Page = request.Page;
        PageSize = request.PageSize;
        TotalCount = totalCount;
    }

    /// <summary>Rows on this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>1-based page number.</summary>
    public int Page { get; }

    /// <summary>Rows per page.</summary>
    public int PageSize { get; }

    /// <summary>Total rows across all pages.</summary>
    public int TotalCount { get; }

    /// <summary>Total number of pages (0 when there are no rows).</summary>
    public int TotalPages => (int)(((long)TotalCount + PageSize - 1) / PageSize);

    /// <summary><see langword="true"/> when a previous page exists.</summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary><see langword="true"/> when a next page exists.</summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>An empty page.</summary>
    public static PagedResult<T> Empty(PageRequest request) => new([], request, 0);
}
