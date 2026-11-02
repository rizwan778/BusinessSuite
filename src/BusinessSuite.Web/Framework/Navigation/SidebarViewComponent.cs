using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Framework.Navigation;

/// <summary>A sidebar item prepared for rendering.</summary>
/// <param name="Item">The menu definition.</param>
/// <param name="IsActive"><see langword="true"/> when the item belongs to the current page.</param>
public sealed record SidebarItemViewModel(NavigationItem Item, bool IsActive);

/// <summary>A sidebar section prepared for rendering.</summary>
public sealed record SidebarSectionViewModel(string Title, IReadOnlyList<SidebarItemViewModel> Items);

/// <summary>
/// Renders the sidebar: the items the user may see (<see cref="NavigationAuthorizer"/>),
/// with the current page highlighted.
/// View: <c>Views/Shared/Components/Sidebar/Default.cshtml</c>.
/// </summary>
public sealed class SidebarViewComponent(NavigationAuthorizer navigationAuthorizer) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var currentController = RouteData.Values["controller"] as string;
        var currentArea = RouteData.Values["area"] as string ?? string.Empty;

        var sections = await navigationAuthorizer.GetVisibleSectionsAsync(UserClaimsPrincipal);

        var model = sections
            .Select(section => new SidebarSectionViewModel(
                section.Title,
                section.Items
                    .Select(item => new SidebarItemViewModel(item, IsActive(item, currentController, currentArea)))
                    .ToList()))
            .ToList();

        return View(model);
    }

    private static bool IsActive(NavigationItem item, string? currentController, string currentArea) =>
        item.Status == ModuleStatus.Available
        && string.Equals(item.Controller, currentController, StringComparison.OrdinalIgnoreCase)
        && string.Equals(item.Area ?? string.Empty, currentArea, StringComparison.OrdinalIgnoreCase);
}
