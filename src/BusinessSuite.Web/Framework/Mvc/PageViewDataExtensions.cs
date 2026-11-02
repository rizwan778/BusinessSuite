using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BusinessSuite.Web.Framework.Mvc;

/// <summary>A breadcrumb link. The last crumb (the current page) has no URL.</summary>
public sealed record Breadcrumb(string Title, string? Url = null);

/// <summary>
/// Typed helpers for page metadata used by the layout: title and breadcrumbs.
/// Call them at the top of a view:
/// <code>
/// @{
///     ViewData.SetPageTitle("Products");
///     ViewData.AddBreadcrumb("Inventory", Url.Action("Index", "Inventory"));
///     ViewData.AddBreadcrumb("Products");
/// }
/// </code>
/// </summary>
public static class PageViewDataExtensions
{
    private const string TitleKey = "Title";
    private const string BreadcrumbsKey = "BusinessSuite.Breadcrumbs";

    /// <summary>Sets the browser tab title (the product name is appended by the layout).</summary>
    public static void SetPageTitle(this ViewDataDictionary viewData, string title)
    {
        ArgumentNullException.ThrowIfNull(viewData);
        viewData[TitleKey] = title;
    }

    /// <summary>The page title, or an empty string.</summary>
    public static string GetPageTitle(this ViewDataDictionary viewData)
    {
        ArgumentNullException.ThrowIfNull(viewData);
        return viewData[TitleKey] as string ?? string.Empty;
    }

    /// <summary>Appends a breadcrumb. Omit <paramref name="url"/> for the current page.</summary>
    public static void AddBreadcrumb(this ViewDataDictionary viewData, string title, string? url = null)
    {
        ArgumentNullException.ThrowIfNull(viewData);

        if (viewData[BreadcrumbsKey] is not List<Breadcrumb> crumbs)
        {
            crumbs = [];
            viewData[BreadcrumbsKey] = crumbs;
        }

        crumbs.Add(new Breadcrumb(title, url));
    }

    /// <summary>Breadcrumbs added by the view, in order.</summary>
    public static IReadOnlyList<Breadcrumb> GetBreadcrumbs(this ViewDataDictionary viewData)
    {
        ArgumentNullException.ThrowIfNull(viewData);
        return viewData[BreadcrumbsKey] as List<Breadcrumb> ?? [];
    }
}
