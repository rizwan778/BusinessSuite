# Changelog

All notable changes are recorded here. Versions follow [Semantic Versioning](https://semver.org/);
the current version is set in `Directory.Build.props`.

## [Unreleased] - Phase 2 · Tenancy

### Added
- Inventory business logic and security (API.md §10): granular permissions `inventory.product.manage`, `inventory.attribute.manage`,
  `inventory.stock.adjust`, `inventory.stock.transfer` and `inventory.reports.view` next to `inventory.view` and `inventory.manage`
  (warehouses); migration `SplitInventoryPermissions` moves every stored role (system, shared and custom) to them and rolls back.
- `IStockPostingService` (Application): the contract through which Sales, Purchases and returns will change stock. `StageAsync`
  joins the caller's transaction (the invoice and its stock movements commit together), `PostAsync` uses its own; a source line
  posts once (`Stock.AlreadyPosted`); `StageReversalAsync`/`ReverseAsync` correct a cancelled document with opposite movements
  that point at the originals, also for products or warehouses retired since.
- Audit entries for catalogue and stock changes, in the same transaction as the change: products, variants, attributes and
  options, categories, units and conversions, warehouses, batches, and the creation, posting and cancellation of stock
  adjustments and transfers (`product.*`, `variant.*`, `attribute.*`, `category.*`, `unit.*`, `warehouse.*`, `batch.*`,
  `stock_adjustment.*`, `stock_transfer.*`). Updates record the changed fields as `{ from, to }`.
- Database integrity (migration `AddStockPostingIntegrity`): unique index `IX_StockMovements_SourceLine` (a source line posts a
  movement once) and triggers that keep posted adjustments and transfers, and their lines, from being changed or reopened
  (errors 51013 to 51016), on top of the append-only ledger.
- Integration tests for the inventory and catalogue API: tenant isolation and ownership across every endpoint, permission
  wiring in both directions, posting atomicity, concurrency, duplicate posting, immutability against direct SQL, audit coverage,
  the posting contract, and a check that balances always equal their ledger. Architecture tests: every action requires an
  inventory permission and no request can carry an organization id.
- Inventory & catalogue database (Phase 3 start; design in `docs/INVENTORY_DATABASE.md`, migration `AddInventory`):
  `catalog` schema (product categories tree, units of measure and conversions, products, variants, organization-defined
  attributes with Text/Number/Decimal/Date/Boolean/Dropdown/MultiSelect values in typed columns), `inventory` schema
  (warehouses, batches with expiry, balances per warehouse/product/variant/batch, append-only stock ledger, stock
  adjustments, stock transfers) and `platform.Branches`. Tenant isolation is enforced by the database: every reference
  between organization-owned tables is a composite `(OrganizationId, Id)` foreign key. Triggers refuse changes to stock
  movements and deletion of non-draft adjustments/transfers; balances and documents carry row versions. Schema only:
  services, API and screens follow.
- User management API `/api/v1/users`: list (search, paging, sorting; role, department, status filters;
  organization filter for Super Admins), details, create, update profile, change role, change department/group,
  activate/deactivate, start a password reset. Tenant-isolated: the organization always comes from the server-side
  access check, never from the request. Escalation rules: no self role/status changes, Owner-only Owner changes,
  last active Owner protected, platform accounts managed by Super Admins only.
- `POST /api/v1/auth/reset-password`: completes a password reset or first password set-up with the e-mailed code.
- Department/group on organization memberships (`platform.OrganizationMemberships.Department`, migration
  `AddMembershipDepartment`).
- `IEmailSender` with a development pickup-folder implementation (`Email:PickupDirectory`).
- `ICurrentUser.OrganizationRole`.
- Swagger UI: optional `X-Organization-Id` header on every token-protected operation.
- Role and permission API: `/api/v1/roles` (list, details, create, rename, delete when unused, view/configure
  permissions, Super Admin availability per organization) and `GET /api/v1/permissions` (catalogue grouped into User
  Management, Product Management, Inventory, Sales, Purchases, Accounts, Reports, Organization Management).
- Organization roles in the database (`platform.RoleDefinitions`, `platform.RoleAvailability`, migration
  `AddRolesAndPermissions`): the seven system roles with fixed ids, Super Admin-defined shared roles, custom roles per
  organization. Existing memberships are converted from role names to role ids (and back on rollback).
- New permissions: `roles.view`, `roles.manage`, `accounts.view`, `accounts.manage`, `reports.export`.
- Organization API `/api/v1/organizations`: list (search, status filter, paging), details, create, rename,
  suspend/reactivate, and an organization's users (`/{id}/users`) and usable roles (`/{id}/roles`). Platform operators
  (Super Admins, Platform Administrators) reach every organization and alone create, suspend and reactivate them;
  organization users reach only the organization they act in (any other id is `404 Organizations.NotFound`) and may
  rename it with `organization.manage`. Suspensions and reactivations are logged as warnings. No schema change.
- `POST /api/v1/organizations` accepts `initialAdmin` (Super Admins only): the organization, the administrator's account
  and their Owner membership are created in one transaction (all or nothing); the administrator is e-mailed a code to
  choose their password unless one is given. Organization Administrators still cannot create or change other organizations.
- Group API `/api/v1/groups`: list (search, paging), details, create, update, delete where safe. A group is a named
  department: renaming moves its members in the same transaction, and a group with members cannot be deleted
  (`Groups.InUse`). Tenant-isolated; permissions `users.view` / `users.manage`.
- `platform.Groups` (migration `AddGroups`): departments already typed on memberships become groups; rolling back drops
  only the table.
- `[RequireAnyPermission(...)]`: an endpoint is open to holders of any one of several permissions (checked in the
  Application service for which data they reach).
- Test support: `TestLogSink` captures the application's log lines so integration tests can assert what was logged.
- Audit log: `audit.AuditLogs` (migration `AddAuditLog`), append-only (a trigger refuses UPDATE and DELETE). Records
  user creation, profile, role, group and status changes, password-reset initiation, account activation/deactivation,
  role creation/rename/deletion, permission and availability changes, group changes, and organization creation, rename,
  suspension and reactivation, each in the same transaction as the change, with actor, organization, values changed,
  correlation id and IP address (never e-mail addresses, phone numbers, passwords or codes).
- Audit API `/api/v1/audit-logs` (list with action, entity, actor and time filters; detail). Organization-isolated:
  organization users read only their own organization's log; other entries are `404 Audit.NotFound`.
- New permissions `audit.view` (Owner, and Administrator through the migration) and `platform.audit.view`
  (Platform Administrators: every organization's log and platform-wide entries).
- `IRequestInfo` (correlation id and IP address of the current request) for the Application layer.

### Changed
- **Permissions:** `catalog.view` and `catalog.manage` no longer exist. Reading the catalogue needs `inventory.view`; products,
  variants, categories and units need `inventory.product.manage`; attributes need `inventory.attribute.manage`; stock adjustments
  and batches need `inventory.stock.adjust` (was `inventory.manage`); transfers need `inventory.stock.transfer`; the inventory
  dashboard and reports need `inventory.reports.view` (was `inventory.view`). `inventory.manage` now means warehouses. The
  SalesOperator default no longer includes the reports, which show costs and values.
- Stock documents can no longer be given lines after they were posted (the database refuses it); setup code that inserted
  already-posted documents must insert drafts and post them.
- Permission-denied answers for `platform.*` permissions no longer mention organizations or `X-Organization-Id`.
- Role permissions are read from the database on every request; the Owner always holds every organization permission.
- `OrganizationMembership.Role` (name) is replaced by `RoleId`; the CHECK constraint on role names is gone.
- User management resolves roles by id or name among the roles available to the organization, and a non-Super Admin can
  only assign (or change holders of) roles whose permissions they hold (`Users.RoleExceedsYourPermissions`).
  User responses add `memberships[].roleId`.
- Default permissions: Manager and Accountant can export reports; Accountant manages accounts.
- `audit.view` is not part of the "all `*.view`" defaults (ReadOnly, Manager).

### Security
- A Super Admin can no longer deactivate the account of the last active Owner of an active organization
  (`409 Users.LastOwner`); account deactivation, session revocation and its audit entry are one transaction.
- A deactivated account receives no platform role, organization or permission on any request, closing the window in
  which a website cookie stays valid until its next security-stamp check (up to 5 minutes).

## [0.1.0] - Phase 1 · Foundation

### Added
- Solution with Domain, Application, Infrastructure and Web projects plus unit and integration test projects.
- Central package management (`Directory.Packages.props`), shared build settings, SDK pin (`global.json`).
- ASP.NET Core Identity on SQL Server (`identity` schema) with MVC sign-in/sign-out, lockout,
  sign-in rate limiting, deactivated-user blocking and display-name claim.
- Secure-by-default authorization (fallback policy), `PlatformAdministrator` policy.
- Security headers (CSP, X-Frame-Options, Referrer-Policy, Permissions-Policy, COOP), HSTS,
  `__Host-` cookies, global anti-forgery validation.
- Correlation ids on requests, responses and logs; friendly error pages without internal details.
- Health endpoints `/health/live` and `/health/ready` (SQL Server check).
- Automatic created/updated audit stamps (`IAuditable`, `AuditableEntityInterceptor`).
- Framework building blocks: `Entity`, `AuditableEntity`, `Result`/`Error`, paging types,
  `AppController`, alerts, breadcrumbs, `NavigationMenu`, form error partial.
- Responsive Bootstrap 5.3.8 layout and dashboard; vendored front-end libraries (offline, no CDN).
- Development-only database initializer (migrations + optional seeded account from user-secrets).
- Unit tests, architecture tests, in-memory web tests and SQL Server integration tests.
- GitHub Actions CI with a disposable SQL Server container.
- Documentation set in `docs/`, scripts in `scripts/`.
