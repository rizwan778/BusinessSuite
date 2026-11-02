namespace BusinessSuite.Application.Common.Paging;

/// <summary>
/// A page of data requested by the user (page number and size), normalised to safe values.
/// </summary>
/// <remarks>
/// Values arrive from the query string, so they cannot be trusted: the constructor clamps
/// them instead of throwing. List screens and reports must always page their queries.
/// <code>
/// var page = new PageRequest(query.Page, query.PageSize);
/// var items = await db.Products.OrderBy(p => p.Name).Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
/// </code>
/// </remarks>
public sealed record PageRequest
{
    /// <summary>Page size used when none (or an invalid one) is requested.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Largest page size a client may request; protects the database from huge reads.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Highest page number accepted; keeps <see cref="Skip"/> from overflowing.</summary>
    public const int MaxPage = int.MaxValue / MaxPageSize;

    /// <summary>Creates a normalised page request.</summary>
    /// <param name="page">1-based page number. Values below 1 become 1.</param>
    /// <param name="pageSize">Rows per page. Values below 1 become <see cref="DefaultPageSize"/>; values above <see cref="MaxPageSize"/> are capped.</param>
    public PageRequest(int page = 1, int pageSize = DefaultPageSize)
    {
        Page = Math.Clamp(page, 1, MaxPage);
        PageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
    }

    /// <summary>1-based page number.</summary>
    public int Page { get; }

    /// <summary>Number of rows per page.</summary>
    public int PageSize { get; }

    /// <summary>Number of rows to skip in the query (<c>(Page - 1) * PageSize</c>).</summary>
    public int Skip => (Page - 1) * PageSize;
}
