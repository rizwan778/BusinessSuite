using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BusinessSuite.Web.Framework.Navigation;

/// <summary>
/// Filters <see cref="NavigationMenu"/> down to the items the current user is allowed to see.
/// Used by the sidebar AND the dashboard, so both always show the same, authorized set.
/// </summary>
/// <remarks>
/// Visibility is cosmetic; the controllers behind the items still enforce the same policies.
/// </remarks>
public sealed class NavigationAuthorizer(IAuthorizationService authorizationService)
{
    /// <summary>Returns the sections and items visible to <paramref name="user"/>; empty sections are removed.</summary>
    public async Task<IReadOnlyList<NavigationSection>> GetVisibleSectionsAsync(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var visibleSections = new List<NavigationSection>();
        foreach (var section in NavigationMenu.Sections)
        {
            var visibleItems = new List<NavigationItem>();
            foreach (var item in section.Items)
            {
                if (item.Policy is null || (await authorizationService.AuthorizeAsync(user, item.Policy)).Succeeded)
                {
                    visibleItems.Add(item);
                }
            }

            if (visibleItems.Count > 0)
            {
                visibleSections.Add(section with { Items = visibleItems });
            }
        }

        return visibleSections;
    }
}
