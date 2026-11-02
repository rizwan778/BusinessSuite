# Implementation roadmap

Five phases, each ending with a buildable, tested, documented solution and an approval checkpoint
before the next phase starts.

| Phase | Name | Outcome | Status |
|---|---|---|---|
| 1 | **Foundation** | Solution structure, security baseline, sign-in, layout, tests, CI, docs | ✅ Complete (see open action below) |
| 2 | **Tenancy** | Organizations, memberships, tenant isolation, roles/permissions, audit log, subscriptions & limits, user management | ⏳ Next — awaiting approval |
| 3 | **Operations** | Catalogue, customers/suppliers, branches & locations, inventory ledger, purchasing, sales/invoices/payments, returns, tax, FBR integration adapter | Planned |
| 4 | **Insight** | Dashboards, analytics, reports, CSV/Excel export, PDF invoices, reporting indexes | Planned |
| 5 | **Launch** | Payment provider, renewals, e-mail, backups, security testing, deployment pipeline, monitoring | Planned |

## Phase 1 · Foundation — delivered

- [x] Solution, projects, references, central package management, SDK pin
- [x] Dependency injection per layer (`AddApplication`, `AddInfrastructure`, `AddWebPresentation`)
- [x] Validated configuration (options classes + `ValidateOnStart`)
- [x] SQL Server + EF Core `AppDbContext`, `identity` schema, audit stamps interceptor
- [x] ASP.NET Core Identity: MVC sign-in/out, lockout, rate limit, deactivated users blocked
- [x] Secure-by-default authorization, security headers, CSRF, secure cookies, HTTPS/HSTS
- [x] MVC layout (sidebar, top bar, breadcrumbs, alerts), dashboard, error pages, offline front-end libraries
- [x] Correlation ids, structured logging, `/health/live`, `/health/ready`
- [x] Framework building blocks and developer guide
- [x] Unit, architecture, web integration and SQL Server integration tests
- [x] GitHub Actions CI, `.gitignore`, `.gitattributes`, `.editorconfig`, VS Code configuration
- [x] Documentation set
- [ ] **Your action:** generate and commit the initial migration (`./scripts/db.ps1 add InitialCreate`)
      on your machine, then confirm `dotnet build` and `dotnet test` pass there.

### Phase 1 known limitations

- The initial EF Core migration is not included; it must be generated with the real EF Core tooling (one command).
- No organization/tenant concept yet: any signed-in user sees the same (empty) dashboard.
- No user registration, invitation, password reset, e-mail confirmation or MFA screens.
  (Sign-up, sign-in and token refresh exist as a JSON API: see [API.md](API.md).)
- Data-protection keys use the default local key store (fine for one development machine).
- The organization/branch selector in the top bar is a disabled placeholder.

## Phase 2 · Tenancy — proposed scope

Order matters: each step builds on the previous one.

1. **Data access contract:** `IAppDbContext` in Application (decision D-05).
2. **Organizations & branches:** entities, configuration, platform-admin CRUD, organization profile settings
   (legal/display name, tax ids such as NTN/STRN, currency, time zone, date/number formats, fiscal year, invoice numbering).
   *API delivered (`/api/v1/organizations`, see API.md §7): list/search/status filter, view, create, rename,
   suspend/reactivate (platform operators), create with a first administrator (Super Admins), an organization's users
   and usable roles; groups/departments (`/api/v1/groups`, API.md §8).
   Still open: branches, profile settings (tax ids, currency, time zone, formats, fiscal year, numbering), MVC screens.*
3. **Memberships & current organization:** `OrganizationMembership`, organization switcher, server-side
   validation per request, `ICurrentUser.OrganizationId` / `BranchId`.
4. **Tenant isolation:** `ITenantOwned`, global query filters, write guard interceptor, cross-tenant integration tests.
5. **Roles & permissions:** organization roles (Owner, Administrator, Manager, Inventory Manager, Sales Operator,
   Accountant, Read-Only), permission policies, escalation rules, last-owner protection.
   *Delivered: roles in the database with an API (`/api/v1/roles`, `/api/v1/permissions`, see API.md §6): custom roles
   per organization, Super Admin shared roles and availability per organization, permission catalogue.*
6. **User management:** list/search/paging, invite (token link), activate/deactivate, assign roles and branches.
   *API delivered (`/api/v1/users`, see API.md §5): list/search/filter/sort/paging, create (password set-up by
   e-mailed code), profile update, role, department/group, membership/account status, password reset.
   Still open: invitations of existing accounts, branches, MVC screens.*
7. **Account flows:** `IEmailSender` (development: writes e-mails to a local folder), password reset,
   e-mail confirmation, optional authenticator-app MFA.
8. **Audit log:** `audit.AuditLogs`, recorded for user, role, setting and subscription changes.
   *Delivered (`/api/v1/audit-logs`, see API.md §9): append-only table, entries written in the same transaction as
   user, role, permission, group, status, password-reset and organization changes; organization-isolated reads
   (`audit.view`) and platform-wide reads (`platform.audit.view`). Also: last-Owner protection on account deactivation,
   no access for deactivated accounts on any request. Still open: settings and subscription changes (when those features
   exist), retention policy, MVC screen.*
9. **Subscriptions:** plans, entitlements, organization subscriptions, history, `IEntitlementService`,
   enforcement of user/branch limits, platform-admin subscription screens (manual billing only).
10. **Platform administration area** with explicit, audited cross-tenant services.

## Phase 3 · Operations — outline

*Started: the catalogue and inventory database is in place (`AddInventory`, see [INVENTORY_DATABASE.md](INVENTORY_DATABASE.md)):
products with variants and organization-defined attributes, units and conversions, warehouses and branches, batches and
expiry, balances, the append-only ledger, adjustments and transfers. Next: services, API and screens on top of it.*

Catalogue (categories, brands, units of measure with conversions, products, prices, tax configuration),
customers (incl. walk-in) and suppliers, stock locations, **inventory ledger** (receipts, issues, adjustments,
transfers, opening stock, optional batch/expiry, costing method: moving weighted average), purchase orders and
goods receipts with partial receipts and supplier invoices, sales orders, **invoices** with server-side totals,
discounts, tax, numbering, lifecycle (Draft → Confirmed → Partially Paid → Paid / Cancelled / Overdue),
payments, returns and credit notes, expenses. All operations transactional, tenant-scoped and audited.

**FBR digital invoicing (Pakistan):** an `IFiscalInvoiceGateway` interface in Application and an adapter in
Infrastructure that submits confirmed invoices and stores the fiscal invoice number/QR data returned. The exact
API, authentication and sandbox process must be confirmed against FBR's current technical documentation when the
phase starts; the design keeps it optional per organization and replaceable for other countries.

## Phase 4 · Insight — outline

Role-aware dashboard figures, reports (sales by date/product/category/customer/branch, purchases, stock on hand,
movements, low stock, expiry, receivables, payables, collections, expenses, subscription usage), filters and
paging, CSV/Excel export, printable views and PDF invoices, charts, reporting indexes. Licence check before
choosing PDF/Excel libraries (some are free only below a revenue threshold).

## Phase 5 · Launch — outline

Payment provider integration with verified, idempotent webhooks; renewal and dunning workflows; transactional
e-mail provider; backup/restore runbook; security testing; deployment pipeline (manual approval) to the chosen
host; monitoring and alerting; data-protection key persistence; forwarded headers; production readiness review.

## Services that will cost money (none needed for development)

| Service | When | Notes |
|---|---|---|
| Hosting (VM, container platform or app service) | Phase 5 | Cheapest start: one small Linux or Windows VM |
| SQL Server for production | Phase 5 | SQL Server Express is free (size/CPU limits); managed databases cost monthly |
| E-mail sending provider | Phase 2 (dev uses a local folder) / Phase 5 | Usually free tier, then per e-mail |
| Payment gateway | Phase 5 | Per-transaction fees |
| Domain name and TLS certificate | Phase 5 | Free certificates are available (e.g. Let's Encrypt) |
| GitHub Actions | Now | Free for public repos; private repos get monthly free minutes |
