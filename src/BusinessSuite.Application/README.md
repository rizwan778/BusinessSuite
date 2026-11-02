# BusinessSuite.Application

The use cases: what the system does, independent of how it is shown (Web) or stored (Infrastructure).

| Belongs here | Never here |
|---|---|
| Application services (`IDashboardService`, later `IProductService`, `IInvoiceService`) | SQL Server or EF Core provider details (`UseSqlServer`, migrations) |
| DTOs returned to the Web layer | Controllers, Razor views, `HttpContext` |
| Interfaces implemented by outer layers (`ICurrentUser`, later `IEmailSender`) | Hard-coded tenant ids, prices or tax rates |
| `Result` / `Error` for expected failures | Swallowed exceptions |
| Options classes for application settings | References to Infrastructure or Web |

## Folder map

```text
Common/
  Abstractions/  Interfaces implemented elsewhere (ICurrentUser)
  Options/       Settings classes bound from appsettings.json (validated at start-up)
  Paging/        PageRequest + PagedResult<T> for every list screen
  Results/       Result, Result<T>, Error, ErrorType
  Security/      Claim type and platform role names
Features/
  Dashboard/     One folder per feature: interface, implementation, DTOs
ApplicationServiceCollectionExtensions.cs   AddApplication(): the registration list
```

## How a feature is laid out

```text
Features/Products/
  IProductService.cs     public interface (what the Web layer calls)
  ProductService.cs      internal sealed implementation
  ProductDto.cs          data returned to the caller
  CreateProductCommand.cs input for a change (validated here, on the server)
```

Data access from Phase 2 onwards: services use an `IAppDbContext` interface defined in this
project and implemented by Infrastructure. See `docs/ARCHITECTURE.md` (decision D-05).
