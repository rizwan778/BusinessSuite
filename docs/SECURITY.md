# Security

Security is part of the foundation, not a later feature. This document says what is implemented
(Phase 1), what is designed for later phases, and the rules every change must follow.

## 1. Threats and mitigations

| Threat | Mitigation | Status |
|---|---|---|
| Password guessing / credential stuffing | Account lockout (5 failures → 15 min), per-IP rate limit on sign-in (10/min), strong password policy (12+ chars, mixed) | Implemented |
| Session theft | `__Host-` cookies, `HttpOnly`, `Secure`, `SameSite=Lax`, HTTPS + HSTS, 8 h sliding session, security-stamp re-validation every 5 min | Implemented |
| Cross-site request forgery (CSRF) | Anti-forgery token required on every POST/PUT/PATCH/DELETE (global filter), `SameSite` cookies. The `/api` controllers are exempt because they authenticate with a bearer token in the `Authorization` header; only read-only GET actions (e.g. `/api/auth/me`) also accept the cookie, enforced by `ControllerConventionTests` | Implemented |
| Cross-site scripting (XSS) | Razor HTML-encodes output; strict Content-Security-Policy (no inline scripts/styles, no external sources) | Implemented |
| Clickjacking | `X-Frame-Options: DENY` + CSP `frame-ancestors 'none'` | Implemented |
| SQL injection | EF Core parameterised queries; raw SQL only via `FromSql`/`SqlQuery` with interpolated parameters | Rule |
| Insecure direct object references (IDOR) | Every query filtered by the server-resolved organization; another organization's id answers `404` exactly like an unknown id (users, roles, groups, organizations, audit entries); cross-tenant integration tests per API | Implemented for the Phase 2 APIs |
| Cross-tenant data leaks | Server-resolved tenant, query filters + explicit checks + write guard, cross-tenant tests | Explicit checks + tests implemented; query filters and write guard planned |
| Privilege escalation | Separate platform vs organization roles; permissions defined in code, checked per request from the database, never read from tokens or the client; nobody changes their own role or status; only Owners touch Owners; roles can only grant (and users only receive) permissions the actor holds; platform accounts changed by Super Admins only | Implemented + tested (`UserAccessRules`, `RoleAccessRules`) |
| Organization left without an administrator | The last active Owner cannot be demoted or deactivated, nor their account deactivated while an active organization depends on them; row locks serialize concurrent attempts | Implemented + tested |
| Untraceable administrative changes (repudiation) | Append-only audit log written in the same transaction as each change, with actor, organization, values, correlation id and IP (§7) | Implemented + tested |
| Over-posting | Forms bind to view models containing only editable fields; never bind entities | Rule + implemented for login |
| Open redirect | `returnUrl` followed only when `Url.IsLocalUrl` is true | Implemented + tested |
| Information disclosure | Generic error pages with a reference id; details only in server logs; no `Server` header; same message for unknown user and wrong password | Implemented + tested |
| Secrets in source control | Secrets only in user-secrets/environment variables; `.gitignore` rules; no default passwords | Implemented |
| Account enumeration | Identical sign-in failure message and response for all failure reasons | Implemented + tested |

## 2. Authentication (implemented)

* ASP.NET Core Identity with SQL Server stores (`identity.*` tables), MVC sign-in pages (no Razor Pages UI).
* Sign-in name is the e-mail address; `User.RequireUniqueEmail = true`.
* Password policy, lockout and sign-in options are set in `InfrastructureServiceCollectionExtensions`
  and can be tightened (not weakened without review) in the `Identity` configuration section.
* Deactivated users (`ApplicationUser.IsActive = false`) cannot sign in by any route
  (`ApplicationSignInManager.CanSignInAsync`). Deactivation (`PATCH /api/v1/users/{id}/status` by a Super Admin)
  changes the security stamp and revokes every API session in one transaction: bearer tokens stop at once, and a
  website cookie, until its next stamp check (≤ 5 minutes), authenticates but gets no role, organization or permission
  (`UserAccessService` ignores inactive accounts).
* Development accounts come only from user-secrets (`DevelopmentSeed:*`) and are never created outside Development.
* **JSON API** (`/api/auth/*`, see [API.md](API.md)): self-service sign-up, sign-in and token refresh with
  ASP.NET Core Identity bearer tokens (opaque, encrypted with the data-protection keys; no signing secret to manage).
  Same lockout, deactivation and generic-failure rules as the sign-in page. Sign-in and sign-up are rate-limited per IP.
  Every login is an API session (`identity.UserSessions`, 14-day sliding / 30-day absolute limit). Each request checks
  the session, the security stamp and that the account is active, so logout, "sign out everywhere", a password change
  or deactivation invalidates the affected tokens immediately (API.md §2).
  Sign-up answers 409 for an existing e-mail address (account enumeration, mitigated by the rate limit); this
  becomes a neutral "check your e-mail" response once e-mail confirmation exists (Phase 2).
* **Planned (Phase 2):** invitation flow, password reset, e-mail confirmation, optional two-factor
  authentication (authenticator app), "sign out everywhere", per-user activity view.

## 3. Authorization

Implemented: permission-based (`[RequirePermission]`), organization-role-based (`[RequireOrganizationRole]`) and
Super Admin authorization, with organization claims resolved from the database per request.
How to use it: [AUTHORIZATION.md](AUTHORIZATION.md).

* **Secure by default:** a fallback policy requires a signed-in user for every endpoint.
  Public endpoints must be marked `[AllowAnonymous]` and added to
  `ControllerConventionTests.ApprovedAnonymousActions` (the test fails otherwise, forcing a review).
* **Policies** live in `Framework/Security/AuthorizationPolicies.cs`. Use the same policy in
  `[Authorize(Policy = ...)]` and in `NavigationMenu` (menu visibility is cosmetic only).
* **Two kinds of roles (from Phase 2):**

| Kind | Examples | Stored as | Scope |
|---|---|---|---|
| Platform roles | `PlatformAdministrator` | Identity role (`identity.UserRoles`) | Whole platform; cross-tenant operations only through dedicated, audited services |
| Organization roles | Owner, Administrator, Manager, Inventory Manager, Sales Operator, Accountant, Read-Only | `OrganizationMembership.Role` (+ permissions) | One organization (optionally limited to branches) |

* An organization Administrator never gains platform access. Role changes are checked on the server:
  a user can only grant roles lower than their own; the last active Owner cannot be removed or deactivated.
* Fine-grained permissions (e.g. `invoices.cancel`) are checked by policies backed by a permission
  service, so new roles can be added later without code changes in controllers.

## 4. Tenant isolation (designed; implemented in Phase 2)

1. **Resolution:** after sign-in the user selects an organization from their active memberships. The selected
   `OrganizationId` is stored in the auth cookie and **re-validated against the database on each request**
   (membership still active, organization not suspended). It is exposed as `ICurrentUser.OrganizationId`.
2. **Never trusted from the client:** no action accepts an `OrganizationId`/`TenantId` from forms, routes,
   query strings or JSON. Commands get the tenant from `ICurrentUser`.
3. **Reads:** entities implementing `ITenantOwned` get an EF Core global query filter
   (`e.OrganizationId == currentOrganizationId`). This is a safety net; services still load records with an
   explicit organization condition and return `Error.NotFound` (not 403) for other tenants' ids.
4. **Writes:** a `SaveChanges` interceptor sets `OrganizationId` on new tenant-owned entities and throws if an
   entity of another organization is modified.
5. **Database:** composite indexes and unique constraints start with `OrganizationId`
   (e.g. unique `(OrganizationId, Sku)`); foreign keys to other tenant rows are validated in services.
6. **Background jobs, exports, reports:** receive an explicit organization id and run under a system identity;
   no "current request" assumptions.
7. **Platform administration:** separate services that use `IgnoreQueryFilters()` deliberately, require the
   `PlatformAdministrator` policy, and write to the audit log.
8. **Tests:** for every tenant-owned feature, an integration test signs in as organization A and tries to read,
   update and delete organization B's record by changing ids in URLs and forms; the expected result is 404/denied.

## 5. Browser protections (implemented)

| Header / feature | Value |
|---|---|
| Content-Security-Policy | `default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; img-src 'self' data:; font-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'` (Development adds `ws: wss:` for hot reload) |
| X-Content-Type-Options | `nosniff` |
| X-Frame-Options | `DENY` |
| Referrer-Policy | `strict-origin-when-cross-origin` |
| Permissions-Policy | camera, microphone, geolocation, payment, USB, sensors disabled |
| Cross-Origin-Opener-Policy | `same-origin` |
| HSTS | 180 days, production only (`IncludeSubDomains` off until all sub-domains use HTTPS) |
| Cookies | `__Host-BusinessSuite.Auth`, `.Antiforgery`, `.TempData`: Secure, HttpOnly (auth), SameSite |

**Consequence for developers:** no `<script>` blocks, no `onclick="..."`, no `style="..."` in views,
no CDN links. Put code in `wwwroot/js` and `wwwroot/css`.

## 6. Secrets and configuration

* Never commit passwords, API keys, connection strings with credentials, certificates or `.env` files.
  `.gitignore` blocks the common file names; review `git status` before every commit.
* Development: `dotnet user-secrets` (stored in `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json`, outside the repo).
* Production: environment variables (`ConnectionStrings__DefaultConnection`) or the hosting platform's secret store.
* If a secret is ever committed: treat it as leaked. Rotate it immediately, then remove it from history.

## 7. Logging and privacy

* Never log passwords, tokens, cookies, full card or bank numbers, or connection strings.
* Log user **ids**, not e-mail addresses or names, unless needed for support.
* `EnableSensitiveDataLogging` (EF parameter values) is allowed only in Development.
* Every log line carries `CorrelationId`; users see it on error pages as "Reference".
* Operational logs (console/JSON) are separate from the business audit log.

**Audit log** (`audit.AuditLogs`, API.md §9):

* Every administrative change made through the users, roles, groups and organizations APIs records an entry through
  `AuditTrail`, in the **same transaction** as the change: no change without its entry, no entry for a refused change.
* **Append-only:** no update or delete code exists, and the trigger `TR_AuditLogs_AppendOnly` refuses `UPDATE` and
  `DELETE` for every login. In production, run the application with a login that does not have `ALTER` on the `audit`
  schema, so it cannot drop the trigger.
* **Isolation:** organization users read only their organization's entries (`audit.view`); platform-wide entries and
  other organizations' entries are `404`. Platform operators need `platform.audit.view` (or Super Admin).
* **Content:** ids, role and group names, and the values that changed. Never passwords, tokens, reset codes, e-mail
  addresses or phone numbers. The IP address is personal data: include it in the privacy notice and the retention policy.
* Changes made outside the APIs (SQL scripts, the development seeder) are not recorded.

## 8. Production hardening checklist (Phase 5)

* [ ] `AllowedHosts` set to the real domain(s).
* [ ] Behind a reverse proxy: configure `ForwardedHeadersOptions` with known proxies so client IPs (rate limiting, logs, audit log) and HTTPS detection are correct.
* [ ] Application database login without `ALTER`/`CONTROL` on the `audit` schema (the append-only trigger must stay); audit-log retention period decided.
* [ ] Data-protection keys persisted to shared, protected storage (several servers or containers), otherwise sign-in cookies become invalid after restarts.
* [ ] `/health/ready` restricted to the monitoring network (`RequireHost` or firewall) if desired.
* [ ] Backups and restore test (see DATABASE_DESIGN.md §7).
* [ ] Dependency vulnerability check: `dotnet list package --vulnerable --include-transitive`.
* [ ] Dynamic scan of a staging environment (e.g. OWASP ZAP baseline).

## 9. Known gaps in Phase 1

No organizations or tenant isolation yet, no user-management UI, no MFA, no e-mail confirmation or password
reset, no business audit log, default data-protection key storage. These are scheduled in the roadmap and
must not be described as available until implemented and tested.
