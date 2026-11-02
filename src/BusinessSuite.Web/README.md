# BusinessSuite.Web

ASP.NET Core MVC host and composition root: controllers, Razor views, view models, middleware,
and the place where all layers are wired together (`Program.cs`).

| Belongs here | Never here |
|---|---|
| Thin controllers (call one Application service per action) | Business rules, calculations, SQL / `AppDbContext` |
| View models (`Models/<Feature>/...ViewModel.cs`) | EF Core entities passed to views |
| Razor views, partials, view components | Inline `<script>`, `onclick=`, `style="..."` (blocked by CSP) |
| Middleware, filters, authorization policies | Secrets or connection strings in committed files |

## Folder map

```text
Program.cs                      Start here: services -> pipeline -> endpoints (numbered)
Controllers/                    Root controllers: Dashboard, Account, Error
Areas/<Module>/                 Business modules from Phase 2 (Controllers/ + Views/ per area)
Models/<Feature>/               View models
Views/
  Shared/_Layout.cshtml         App shell (sidebar, top bar, breadcrumbs, alerts)
  Shared/_MinimalLayout.cshtml  Sign-in and error pages
  Shared/_FormErrors.cshtml     Form-level error summary
  Shared/Components/Sidebar/    Sidebar view component markup
Framework/                      Reusable web building blocks
  Diagnostics/                  Correlation id middleware, health endpoints
  Hosting/                      AddWebPresentation(), AppInfo (version)
  Mvc/                          AppController base class, alerts, page title/breadcrumbs
  Navigation/                   NavigationMenu (single source for sidebar + dashboard)
  RateLimiting/                 Sign-in throttling policy
  Security/                     Cookies, authorization policies, security headers, ICurrentUser
wwwroot/                        Static files; lib/ holds vendored Bootstrap/jQuery (offline)
```

## Request pipeline (see Program.cs)

Correlation id → exception handler (production) → status-code pages → security headers →
HTTPS redirection → routing → rate limiter → authentication → authorization → endpoint.
Every endpoint requires a signed-in user unless marked `[AllowAnonymous]`.
