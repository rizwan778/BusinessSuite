using BusinessSuite.Application.Common.Paging;

namespace BusinessSuite.Tests.Unit.Application.Paging;

public sealed class PagingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(3, 3)]
    public void Page_number_is_at_least_one(int requested, int expected)
    {
        Assert.Equal(expected, new PageRequest(requested).Page);
    }

    [Theory]
    [InlineData(0, PageRequest.DefaultPageSize)]
    [InlineData(-1, PageRequest.DefaultPageSize)]
    [InlineData(10, 10)]
    [InlineData(5000, PageRequest.MaxPageSize)]
    public void Page_size_is_normalised(int requested, int expected)
    {
        Assert.Equal(expected, new PageRequest(1, requested).PageSize);
    }

    [Fact]
    public void Skip_is_computed_from_page_and_size()
    {
        Assert.Equal(40, new PageRequest(3, 20).Skip);
    }

    [Fact]
    public void Huge_page_numbers_cannot_overflow_skip()
    {
        var request = new PageRequest(int.MaxValue, PageRequest.MaxPageSize);

        Assert.True(request.Skip >= 0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(25, 1)]
    [InlineData(26, 2)]
    [InlineData(100, 4)]
    public void Total_pages_rounds_up(int totalCount, int expectedPages)
    {
        var page = new PagedResult<string>([], new PageRequest(1, 25), totalCount);

        Assert.Equal(expectedPages, page.TotalPages);
    }

    [Fact]
    public void Navigation_flags_reflect_position()
    {
        var middle = new PagedResult<string>(["a"], new PageRequest(2, 10), 30);

        Assert.True(middle.HasPreviousPage);
        Assert.True(middle.HasNextPage);

        var last = new PagedResult<string>(["a"], new PageRequest(3, 10), 30);
        Assert.False(last.HasNextPage);
    }

    [Fact]
    public void Empty_page_has_no_items()
    {
        var page = PagedResult<string>.Empty(new PageRequest());

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalPages);
        Assert.False(page.HasNextPage);
    }
}
