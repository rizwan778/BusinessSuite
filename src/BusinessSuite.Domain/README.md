# BusinessSuite.Domain

The business model in plain C#: entities, value objects, enums and the rules that keep them valid.

| Belongs here | Never here |
|---|---|
| Entities (`Product`, `Invoice`, ...) and their rule-enforcing methods | EF Core, SQL, `DbContext`, attributes such as `[Table]` |
| Value objects and enums (`InvoiceStatus`, `StockMovementType`) | ASP.NET Core types (`HttpContext`, `Controller`) |
| Domain exceptions | Logging, configuration, e-mail, files |
| Interfaces that describe the model (`IAuditable`) | References to any other BusinessSuite project |

## Folder map

```text
Common/            Building blocks shared by every module
  Entity.cs           Base class with a Guid Id
  AuditableEntity.cs  Entity + Created/Updated stamps (filled automatically)
  IAuditable.cs       Contract used by the auditing interceptor
  DomainException.cs  Thrown when a business rule would be broken
<Module>/          One folder per business module, added from Phase 2 onwards
                   (Organizations/, Catalog/, Inventory/, Sales/, Purchasing/, ...)
```

## Rules of thumb

- Change state through methods (`invoice.Confirm()`), not public setters, whenever a rule applies.
- Money and quantities are `decimal`. Never `float` or `double`.
- Store instants as `DateTimeOffset` in UTC and name them `...AtUtc`.
- The architecture tests in `tests/BusinessSuite.Tests.Unit/Architecture` fail the build if this
  project starts depending on Application, Infrastructure, Web, EF Core or ASP.NET Core.
