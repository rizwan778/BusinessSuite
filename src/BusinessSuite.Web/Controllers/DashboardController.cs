using BusinessSuite.Application.Features.Dashboard;
using BusinessSuite.Web.Framework.Hosting;
using BusinessSuite.Web.Framework.Mvc;
using BusinessSuite.Web.Framework.Navigation;
using BusinessSuite.Web.Models.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace BusinessSuite.Web.Controllers;

/// <summary>
/// Home page after sign-in. A reference example of a thin controller:
/// call the Application service, map the DTO to a view model, return the view.
/// </summary>
public sealed class DashboardController(
    IDashboardService dashboardService,
    NavigationAuthorizer navigationAuthorizer,
    IHostEnvironment environment) : AppController
{
    // GET /  and  GET /Dashboard
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var overview = await dashboardService.GetOverviewAsync(cancellationToken);
        var modules = await navigationAuthorizer.GetVisibleSectionsAsync(User);

        var model = DashboardViewModel.From(overview, modules, environment.EnvironmentName, AppInfo.Version);
        return View(model);
    }
}
