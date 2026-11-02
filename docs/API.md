# JSON API

Routes under `/api` are JSON endpoints for non-browser clients (mobile apps, integrations, a future SPA).
The MVC pages keep using the sign-in cookie; the API uses bearer tokens.
Authorization (roles, permissions, organizations, Super Admin) is described in [AUTHORIZATION.md](AUTHORIZATION.md).

Code: `Controllers/Api/AuthController.cs`, building blocks in `Framework/Api/` and `Framework/Security/`.

**Version:** the current version is `v1` (`/api/v1/...`). The earlier unversioned paths `/api/auth/*` still work
(they are served by `/api/v1/auth/*`) but answer with `Deprecation: true` and a `Link` to the new path; move clients
to `/api/v1`.

**Interactive documentation (Development only):** run the app and open `https://localhost:7180/swagger`
(OpenAPI document: `/openapi/v1.json`). Use **Authorize** with the `accessToken` from login.

**Sign-up page:** `/Account/Register` (linked from the sign-in page) sends its form to `POST /api/v1/auth/register`
with `wwwroot/js/register.js`, shows the API's errors next to the fields, and on success returns to
`/Account/Login?signedUp=true`, which confirms the new account.

## 1. Authentication endpoints

| Method and path | Auth | Body | Success |
|---|---|---|---|
| `POST /api/v1/auth/register` | anonymous, rate-limited (`registration`) | `{ email, password, displayName? }` | `201` `{ userId, email, displayName }` |
| `POST /api/v1/auth/login` | anonymous, rate-limited (`login`) | `{ email, password }` | `200` `{ tokenType, accessToken, expiresIn, refreshToken }` |
| `POST /api/v1/auth/refresh` | anonymous (the refresh token is the credential) | `{ refreshToken }` | `200` new token pair, same shape as login |
| `GET /api/v1/auth/me` | Bearer token **or** the website's sign-in cookie | none | `200` `{ userId, email, displayName }` |
| `GET /api/v1/auth/me/permissions` | Bearer token **or** the website's sign-in cookie; optional `X-Organization-Id` header | none | `200` see below |
| `POST /api/v1/auth/reset-password` | anonymous, rate-limited (`login`); the e-mailed code is the credential | `{ email, code, newPassword }` | `204`; e-mail confirmed, lockout lifted, every session signed out |
| `POST /api/v1/auth/logout` | Bearer token | none | `204`; this device's access and refresh tokens stop working immediately |
| `POST /api/v1/auth/logout-all` | Bearer token | none | `204`; every device is signed out (all sessions revoked, security stamp changed) |

`GET /api/v1/auth/me/permissions` response:

```json
{
  "userId": "6a72ccb3-5e26-4141-75ed-08df37cba869",
  "email": "owner@example.com",
  "displayName": "Demo Owner",
  "isSuperAdmin": false,
  "platformRoles": [],
  "organization": { "id": "d2983d3d-e0b5-4339-478c-08df37cba889", "name": "Demo Business", "role": "Owner" },
  "organizations": [ { "id": "d2983d3d-e0b5-4339-478c-08df37cba889", "name": "Demo Business", "role": "Owner" } ],
  "permissions": [ "inventory.product.manage", "inventory.view", "invoices.cancel", "invoices.create", "..." ]
}
```

`organization` is the organization this request acts in: the one named by the `X-Organization-Id` header, or the
user's only organization. With several organizations and no header it is `null` (and organization permissions are
empty): send the header. A header naming an organization the user cannot act in answers `403 Organization.AccessDenied`.

* Sign-up does not sign the user in: call `login` afterwards.
* Request bodies may contain a trailing comma after the last property and `//` or `/* */` comments.

```bash
curl -X POST https://localhost:7180/api/v1/auth/register -H "Content-Type: application/json" \
     -d '{"email":"ali@example.com","password":"Str0ng-Passw0rd!","displayName":"Ali"}'

curl -X POST https://localhost:7180/api/v1/auth/login -H "Content-Type: application/json" \
     -d '{"email":"ali@example.com","password":"Str0ng-Passw0rd!"}'

curl https://localhost:7180/api/v1/auth/me/permissions -H "Authorization: Bearer <accessToken>" \
     -H "X-Organization-Id: <organization id>"
```

## 2. Tokens and sessions (expiration and revocation)

| Item | Lifetime | Setting |
|---|---|---|
| Access token | 60 min | `Authentication:ApiAccessTokenMinutes` |
| Refresh token / session idle limit | 14 days, sliding (each refresh restarts it) | `Authentication:ApiRefreshTokenDays` |
| Session absolute limit | 30 days after login, however often it is refreshed | `Authentication:ApiSessionMaxDays` |

Each login creates a row in `identity.UserSessions`; its id is inside both tokens. **Every API request** checks, in
one indexed query, that the session is not revoked or expired, that the account is active and that the token's
security stamp is still the user's. So an access token stops working **immediately** when:

| Event | Effect |
|---|---|
| `POST /logout` | This session revoked (other devices keep working) |
| `POST /logout-all` | All sessions revoked + security stamp changed (website sessions end within 5 min) |
| Password change | Security stamp changes: every token of the user |
| User deactivated | Every token of the user |
| Session past its idle or absolute limit | That session |

Refresh applies the same checks. Expired sessions of a user are deleted at their next login.
Tokens are opaque: do not try to decode them. Only this application, with its data-protection keys, can read them;
several servers must share those keys (SECURITY.md §8).

Organization, role and permission data are **not** inside tokens: they are read from the database for each request,
so a role change, removed membership or suspended organization applies on the next request, with no new token.

## 3. Errors

Every error is an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457) problem details document
(`Content-Type: application/problem+json`), never an HTML page or a redirect:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Unauthorized",
  "status": 401,
  "detail": "Invalid e-mail address or password.",
  "code": "Auth.InvalidCredentials",
  "correlationId": "80cd5510f496a8e8eadcaf0184a4801e",
  "traceId": "00-80cd5510f496a8e8eadcaf0184a4801e-990f2afbd848b011-00"
}
```

Clients should branch on `code` (stable) and show `detail` (user-safe). `correlationId` matches the
`X-Correlation-ID` response header and every server log line for the request.

| Status | `code` | When |
|---|---|---|
| 400 | `Request.ValidationFailed` | Missing/invalid fields or malformed JSON; `errors` lists messages per field (`$` = the JSON itself) |
| 400 | `Account.WeakPassword` | Password breaks the policy (12+ characters, upper, lower, digit, symbol); `detail` lists every rule broken |
| 400 | `Account.InvalidEmail`, `Account.RegistrationRejected` | Identity refused the account for another reason |
| 400 | `Account.InvalidResetCode` | Reset code wrong, used or expired, or unknown e-mail (deliberately identical) |
| 400 | `Users.OrganizationRequired` | The operation needs an organization: send `X-Organization-Id` |
| 400 | `Users.InvalidRole`, `Users.InvalidSort`, `Users.Invalid` | Unknown role; role/department sort without an organization; field too long |
| 403 | `Users.OrganizationFilterNotAllowed` | `organizationId` filter used by someone who is not a Super Admin |
| 403 | `Users.CannotChangeOwnAccess` | Changing your own role or status |
| 403 | `Users.OwnerRequired` | Only an Owner may assign the Owner role or change an Owner |
| 403 | `Users.PlatformAccountProtected` | Platform accounts are changed by Super Admins only |
| 404 | `Users.NotFound` | No such user in your organization (users of other organizations look exactly like this) |
| 409 | `Users.AlreadyMember` | The e-mail address belongs to a member of this organization |
| 409 | `Users.LastOwner` | The change would leave the organization without an active Owner |
| 409 | `Users.ConcurrentChange` | Someone else changed the user at the same moment; reload and retry |
| 403 | `Users.RoleExceedsYourPermissions` | Assigning a role, or changing a holder of a role, that grants permissions you do not hold |
| 409 | `Users.RoleNoLongerExists` | The role was deleted while being assigned |
| 400 | `Roles.OrganizationRequired`, `Roles.Invalid`, `Roles.UnknownPermission`, `Roles.UnknownOrganization`, `Roles.NotAPlatformRole` | No organization selected; bad/reserved name; permission not assignable; unknown organization id; availability of a custom role |
| 403 | `Roles.OrganizationFilterNotAllowed`, `Roles.SuperAdminRequired` | Another organization's roles; shared roles and availability are Super Admin business |
| 403 | `Roles.CannotGrantPermission`, `Roles.RoleExceedsYourPermissions` | Granting a permission you lack; changing a role that grants more than you hold |
| 404 | `Roles.NotFound` | No such role available to your organization (other organizations' roles look exactly like this) |
| 409 | `Roles.DuplicateName`, `Roles.InUse`, `Roles.SystemRoleLocked`, `Roles.OwnerPermissionsLocked`, `Roles.OwnerAvailabilityLocked`, `Roles.ConcurrentChange` | Name taken; members hold the role; system roles keep name and existence; Owner is fixed |
| 400 | `Organizations.OrganizationRequired`, `Organizations.Invalid` | No organization selected; organization name or first administrator's details missing or too long |
| 403 | `Organizations.PlatformOperatorRequired` | Creating, suspending or reactivating organizations is platform business |
| 403 | `Organizations.SuperAdminRequired` | `initialAdmin` sent by someone who is not a Super Admin |
| 400 | `Groups.OrganizationRequired`, `Groups.Invalid` | No organization selected; group name missing or too long |
| 404 | `Groups.NotFound` | No such group in your organization (other organizations' groups look exactly like this) |
| 409 | `Groups.DuplicateName`, `Groups.InUse`, `Groups.ConcurrentChange` | Name taken; members still belong to the group; changed by someone else at the same moment |
| 404 | `Organizations.NotFound` | No such organization, or not the one you act in (other organizations look exactly like this) |
| 409 | `Organizations.ConcurrentChange` | Someone else changed the organization at the same moment; reload and retry |
| 401 | `Auth.InvalidCredentials` | Wrong password, unknown e-mail, deactivated account (deliberately identical) |
| 401 | `Auth.LockedOut` | Too many failed sign-ins; try again after the lockout period (15 min) |
| 401 | `Auth.InvalidRefreshToken` | Refresh token forged or expired, or its session ended (see §2) |
| 401 | `Auth.Unauthorized` | Missing, expired or revoked access token, or an unknown `/api` route |
| 403 | `Auth.PermissionDenied` | Signed in but missing a permission; `detail` names it |
| 403 | `Auth.OrganizationRoleRequired` | Signed in but the role in this organization is not allowed |
| 403 | `Auth.SuperAdminRequired` | Only Super Admins may do this |
| 403 | `Organization.AccessDenied` | `X-Organization-Id` names an organization the user cannot act in |
| 409 | `Account.DuplicateEmail` | An account with this e-mail address already exists |
| 429 | `Request.RateLimited` | Too many requests from this IP; see the `Retry-After` header (seconds) |
| 500 | `Server.UnexpectedError` | Bug or outage. No details in the response; search the logs for the `correlationId` |

## 4. Exception handling and logging

| Situation | Handled by | Logged |
|---|---|---|
| Expected failure (duplicate, weak password, wrong password) | `Result`/`Error` → `AppApiController.ErrorResult` | Information/Warning with user id or Identity error codes |
| Invalid input | `[ApiController]` + `ApiExtensions.AddApi` | Information: method, path, names of invalid fields |
| Access denied (permission, role, Super Admin) | `AccessAuthorizationHandler` + `ApiAuthorizationResultHandler` | Information: user, organization, code, reason |
| Organization not accessible / Super Admin acting in a foreign organization | `UserAccessService` | Warning |
| Token rejected (revoked, expired, stamp changed, deactivated) | `ApiSessionValidator` | Information: user, session |
| Rate limit exceeded | `RateLimitingExtensions.OnRejectedAsync` | Warning: method, path |
| Unhandled exception (incl. database outage) | `ApiExceptionHandler` (`/api` only; pages keep the HTML error page) | Error with the full exception |
| Client disconnected | `ApiExceptionHandler` → status 499 | Information |

Never logged: passwords, tokens, e-mail addresses (SECURITY.md §7).

## 5. User management

Code: `Controllers/Api/UsersController.cs` → `IUserManagementService` (`Application/Features/Users`, rules in
`UserAccessRules`) → `UserManagementService` (Infrastructure). All endpoints need a bearer token.

| Method and path | Permission | Body | Success |
|---|---|---|---|
| `GET /api/v1/users` | `users.view` | query, see below | `200` page: `{ items, page, pageSize, totalCount, totalPages, hasPreviousPage, hasNextPage }` |
| `GET /api/v1/users/{id}` | `users.view` | none | `200` user |
| `POST /api/v1/users` | `users.manage` | `{ email, role, displayName?, phoneNumber?, department?, password? }` | `201` user, `Location` header |
| `PUT /api/v1/users/{id}` | `users.manage` | `{ displayName, phoneNumber }` (both replaced; `null` clears) | `200` user |
| `PATCH /api/v1/users/{id}/role` | `users.manage` | `{ role }` (id or name) | `200` user |
| `PATCH /api/v1/users/{id}/group` | `users.manage` | `{ department }` (`null` removes it) | `200` user |
| `PATCH /api/v1/users/{id}/status` | `users.manage` | `{ isActive }` | `200` user |
| `POST /api/v1/users/{id}/reset-password` | `users.manage` | none | `202`; the user is e-mailed a code |

**List query:** `search` (e-mail or display name), `role`, `department`, `status` (`active`/`inactive`),
`organizationId` (**Super Admins only**), `sortBy` (`email` default, `displayName`, `createdAt`, `role`, `department`),
`sortDirection` (`asc`/`desc`), `page` (from 1), `pageSize` (1–100, default 25).
Example: `GET /api/v1/users?search=ali&role=SalesOperator&status=active&sortBy=createdAt&sortDirection=desc&page=1`.

User shape:

```json
{
  "id": "7d1c…", "email": "ali@example.com", "displayName": "Ali", "phoneNumber": "+92 300 1234567",
  "isActive": true, "isLockedOut": false, "emailConfirmed": true, "platformRoles": [],
  "createdAtUtc": "2026-11-01T09:30:00+00:00", "updatedAtUtc": null,
  "memberships": [ { "organizationId": "d298…", "organizationName": "Demo Business", "role": "SalesOperator", "department": "Sales", "isActive": true, "roleId": "5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b05" } ]
}
```

`isActive` is the account (sign-in anywhere); `memberships[].isActive` is access to that organization. In an organization,
the `status` filter counts a user as active only when both are active.

**Tenant isolation (the most important rule).** Every operation runs in the organization the request acts in: the
`X-Organization-Id` header or the caller's only organization, verified against the caller's memberships on the server
for every request. No request body has an organization field (one sent anyway is ignored). An Organization
Administrator therefore only sees and changes members of their own organization: any other user id answers
`404 Users.NotFound`, exactly like an id that does not exist, and an `organizationId` filter naming another
organization answers `403`.

**Super Admins** list every user (or one organization with `organizationId`), see every membership and platform roles,
and create/change users in the organization named by `X-Organization-Id`. With no organization selected,
`PATCH …/status` changes the **account**: deactivation blocks sign-in and ends every session immediately.
For organization users (and Super Admins acting in an organization) it changes the **membership** only: the user keeps
their account but loses access to this organization on their next request.

**Who may change whom** (`UserAccessRules`, unit-tested):

| Rule | Error |
|---|---|
| Nobody changes their own role or status (profile, department and password reset of yourself are fine) | `403 Users.CannotChangeOwnAccess` |
| Only an Owner (or Super Admin) creates an Owner, promotes to Owner, or changes an Owner in any way | `403 Users.OwnerRequired` |
| Platform accounts (Super Admin, Platform Administrator) are changed by Super Admins only | `403 Users.PlatformAccountProtected` |
| The last active Owner cannot be demoted or deactivated (even by a Super Admin, inside the organization) | `409 Users.LastOwner` |
| A Super Admin cannot deactivate the **account** of the last active Owner of an active organization (promote another Owner, or suspend the organization, first) | `409 Users.LastOwner` |

Concurrent demotions are serialized with row locks, so two administrators cannot remove the last two Owners at once.
Every change is recorded in the audit log (§9).

**Creating users.** The e-mail address must not belong to any existing account (`409 Account.DuplicateEmail`, or
`Users.AlreadyMember` for a member); adding existing accounts needs an invitation with the person's consent (planned).
Leave `password` out (recommended): the user is e-mailed a code and chooses their own password with
`POST /api/v1/auth/reset-password`, so no administrator ever knows it. If the e-mail cannot be sent, nothing is created.

**Password reset.** `POST /api/v1/users/{id}/reset-password` e-mails a code; the response never contains it. The old
password keeps working until the user completes the reset. Codes work once and expire after 1 day. Completing the reset
confirms the e-mail address, lifts any lockout and signs the user out on every device. In Development, e-mails are
written to `src/BusinessSuite.Web/App_Data/emails/*.eml` (`Email:PickupDirectory`); no e-mail provider exists yet for
production (Phase 5), and sending fails with a logged error until one is configured.

Role, department and status changes apply on the user's next request (no new sign-in), like every access change.

**Roles** in `role` fields and the `role` filter are a role **id or name** (case-insensitive) among the roles available to
the organization (§6): system roles, shared roles the Super Admin enabled for it, and its own custom roles. Anything else
is `400 Users.InvalidRole`. Responses give both `memberships[].role` (name) and `memberships[].roleId`. Besides the rules
above, a non-Super Admin can only assign roles whose permissions they hold, and can only change users whose role grants
nothing beyond their own permissions (`403 Users.RoleExceedsYourPermissions`).

## 6. Roles and permissions

Code: `Controllers/Api/RolesController.cs`, `PermissionsController.cs` → `IRoleService` (`Application/Features/Roles`,
rules in `RoleAccessRules`) → `RoleService` (Infrastructure). All endpoints need a bearer token.

| Method and path | Who | Body | Success |
|---|---|---|---|
| `GET /api/v1/permissions` | `roles.view` | none | `200` groups: `[{ key, name, isPlatform, permissions: [{ key, name, description }] }]` |
| `GET /api/v1/roles` | `roles.view` | query `search`, `organizationId` (Super Admins) | `200` roles: system first, then shared, then custom |
| `GET /api/v1/roles/{id}` | `roles.view` | none | `200` role |
| `POST /api/v1/roles` | `roles.manage` | `{ name, description?, scope?, permissions? }` | `201` role, `Location` header |
| `PUT /api/v1/roles/{id}` | `roles.manage` | `{ name, description }` (both replaced) | `200` role |
| `DELETE /api/v1/roles/{id}` | `roles.manage` | none | `204` |
| `GET /api/v1/roles/{id}/permissions` | `roles.view` | none | `200` `{ roleId, roleName, permissionsLocked, permissions }` |
| `PUT /api/v1/roles/{id}/permissions` | `roles.manage` | `{ permissions: [...] }` (replaces; `[]` removes all) | `200` same shape |
| `PUT /api/v1/roles/{id}/availability` | Super Admin | `{ allOrganizations, organizationIds? }` | `200` role |

Role shape:

```json
{
  "id": "4f0c…", "name": "Cashier", "description": "Front counter", "scope": "Organization", "organizationId": "d298…",
  "isSystem": false, "permissionsLocked": false, "canManage": true,
  "permissions": [ "invoices.create", "sales.view" ], "memberCount": 3,
  "availability": null, "createdAtUtc": "2026-11-01T09:30:00+00:00", "updatedAtUtc": null
}
```

`availability` (`{ allOrganizations, organizationIds }`) is filled for shared roles and Super Admins only. `memberCount`
counts holders in your organization (Super Admins: everywhere). `canManage` tells a client whether to offer editing.

**Kinds of role.**

| Kind | `scope` | Managed by | Rename / delete | Permissions |
|---|---|---|---|---|
| System (Owner, Administrator, Manager, InventoryManager, SalesOperator, Accountant, ReadOnly) | `Platform` | Super Admin | Never | Super Admin may change, except the Owner's (always every organization permission) |
| Shared (Super Admin-defined) | `Platform` | Super Admin | Yes (delete when nobody holds it) | Super Admin |
| Custom | `Organization` | The organization (`roles.manage`) or a Super Admin | Yes (delete when nobody holds it) | The organization, within its own permissions |

System role names are reserved (any letter case). Names are unique among the roles one organization can use.

**Tenant isolation.** An organization user sees and assigns only the roles **available** to their organization: system
roles, shared roles the Super Admin enabled for it, and its own custom roles. Another organization's custom role, or a
shared role not enabled for yours, answers `404 Roles.NotFound` on every endpoint and `400 Users.InvalidRole` when
assigned. `organizationId` filters are for Super Admins (`403` otherwise).

**Super Admin controls availability.** System roles are available everywhere by default. A new shared role is available
to **no** organization until `PUT /roles/{id}/availability` sets `allOrganizations: true` or lists organizations. Taking a
role away from an organization where someone holds it is refused (`409 Roles.InUse`); reassign them first. The Owner
stays available everywhere.

**No escalation.** A non-Super Admin can grant only permissions they hold (`403 Roles.CannotGrantPermission`), cannot
change a role that grants more than they hold (`403 Roles.RoleExceedsYourPermissions`), and roles never grant `platform.*`
permissions (`400 Roles.UnknownPermission`).

**Effect.** Permissions are read from the database on every request, so a role's new permissions, a rename or a
reassignment apply on the holders' next request; no new sign-in.

**Logging.** Refusals are logged at Information with the user, role and error code; changes to custom roles at
Information; changes affecting every organization (shared/system role permissions, availability, platform roles) at
Warning. Concurrent conflicts caught by the database (duplicate name, role deleted while assigned) answer `409` and are
logged as warnings; anything else unexpected is a logged `500` with a `correlationId`.

## 7. Organizations

Code: `Controllers/Api/OrganizationsController.cs` → `IOrganizationService` (`Application/Features/Organizations`, rules in
`OrganizationAccessRules`) → `OrganizationService` (Infrastructure). All endpoints need a bearer token.

| Method and path | Permission (any one listed) | Body | Success |
|---|---|---|---|
| `GET /api/v1/organizations` | `organization.view`, `platform.organizations.manage` | query: `search`, `status` (`active`/`suspended`), `page`, `pageSize` | `200` page: `{ items, page, pageSize, totalCount, totalPages, hasPreviousPage, hasNextPage }` |
| `GET /api/v1/organizations/{id}` | `organization.view`, `platform.organizations.manage` | none | `200` organization |
| `POST /api/v1/organizations` | `platform.organizations.manage` | `{ name, initialAdmin?: { email, displayName?, phoneNumber?, password? } }` (`initialAdmin`: Super Admins only) | `201` organization, `Location` header |
| `PUT /api/v1/organizations/{id}` | `organization.manage`, `platform.organizations.manage` | `{ name }` | `200` organization |
| `PATCH /api/v1/organizations/{id}/status` | `platform.organizations.manage` | `{ isActive }` | `200` organization |
| `GET /api/v1/organizations/{id}/users` | `users.view` | the filters of `GET /users` except `organizationId` | `200` page of users (§5) |
| `GET /api/v1/organizations/{id}/roles` | `roles.view` | query `search` | `200` roles (§6) |

Organization shape:

```json
{
  "id": "d298…", "name": "Al-Noor Traders", "isActive": true, "memberCount": 4,
  "createdAtUtc": "2026-11-01T09:30:00+00:00", "updatedAtUtc": null
}
```

`isActive: false` means suspended. `memberCount` counts members whether active or not, and equals the `totalCount` of
`GET /organizations/{id}/users`.

**Who sees what.**

| Caller | Reaches | May |
|---|---|---|
| Platform operator: Super Admin, or Platform Administrator (`platform.organizations.manage`) | Every organization | List, view, **create**, rename, **suspend/reactivate** |
| Organization user (`organization.view` / `organization.manage`) | Only the organization the request acts in (`X-Organization-Id` or their only one) | View it; rename it with `organization.manage` (Owner, Administrator) |

**Tenant isolation.** An organization user's list holds exactly their own organization. Any other id, whether it exists
or not, answers `404 Organizations.NotFound` with the same text on every endpoint, including `/users` and `/roles`.
The organization is never taken from a request body, and an organization cannot create itself or suspend itself: those
two are platform business (`403 Auth.PermissionDenied`, naming `platform.organizations.manage`). Platform
Administrators hold no organization permissions, so they cannot read an organization's `/users` or `/roles`
(`403`); Super Admins can.

**Suspension.** `PATCH …/status` with `{ "isActive": false }` removes every member's access from their next request: no
organization claims and no permissions, and `X-Organization-Id` naming it is refused. Nothing is deleted; reactivating
restores everything. A Super Admin still sees the suspended organization and its members from the platform side but
cannot act inside it. Sending the current status changes nothing (and is not logged as a change).

**Creating an organization.** It starts active. Names are not unique; two businesses may share one.

**Creating it with its first administrator.** A Super Admin may add `initialAdmin` to `POST /organizations`. In ONE
transaction the organization, the administrator's account and their **Owner** membership are created (the Owner holds
every organization permission, so the organization is usable at once and its administrator can add everyone else). Without
a `password` (recommended) the administrator is e-mailed a code and chooses their own password with
`POST /api/v1/auth/reset-password`, so nobody else ever knows it; with one, it must satisfy the password policy and no
e-mail is sent. The e-mail address must not belong to an existing account (`409 Account.DuplicateEmail`; adopting existing
accounts needs an invitation with the person's consent, planned). It is **all or nothing**: a weak password, a taken
address or an e-mail that cannot be sent (`500`, logged with the `correlationId`) leaves no organization and no account
behind. Platform Administrators may create organizations but not the first administrator
(`403 Organizations.SuperAdminRequired`): that account is access inside the organization, which they do not have. To
add an administrator to an organization created without one, a Super Admin acts in it (`X-Organization-Id`) and calls
`POST /api/v1/users` (§5).

**An Organization Administrator never creates or changes other organizations.** Creating, suspending and reactivating need
`platform.organizations.manage`, which no organization role can hold. They may only rename their own organization;
every other id (and any `X-Organization-Id` they are not a member of) is refused and logged.

**Members and roles.** `/organizations/{id}/users` and `/organizations/{id}/roles` first check that the organization is
visible (404 otherwise), then answer exactly like `GET /users?organizationId={id}` and `GET /roles?organizationId={id}`,
with the same filters, paging and rules: organization users get their own organization's, Super Admins any organization's.

**Logging.** Refusals (not a platform operator, not a Super Admin, another organization's id) are logged at Information with
the user, organization and error code; renames and creations (also those with a first administrator, by user id) at
Information; **suspensions and reactivations at Warning**, since
they change access for a whole organization at once. Logs hold ids only: never organization names, e-mail addresses
or member details. A concurrent change that finds the row changed or gone answers `409 Organizations.ConcurrentChange`
(logged as a warning); anything else unexpected is a logged `500` with a `correlationId`.

## 8. Groups

Code: `Controllers/Api/GroupsController.cs` → `IGroupService` (`Application/Features/Groups`, organization from
`GroupAccessRules`) → `GroupService` (Infrastructure). Table `platform.Groups`. All endpoints need a bearer token.

| Method and path | Permission | Body | Success |
|---|---|---|---|
| `GET /api/v1/groups` | `users.view` | query: `search`, `page`, `pageSize` | `200` page of groups, by name |
| `POST /api/v1/groups` | `users.manage` | `{ name, description? }` | `201` group, `Location` header |
| `GET /api/v1/groups/{id}` | `users.view` | none | `200` group |
| `PUT /api/v1/groups/{id}` | `users.manage` | `{ name, description }` (both replaced) | `200` group |
| `DELETE /api/v1/groups/{id}` | `users.manage` | none | `204` |

Group shape:

```json
{ "id": "9a3e…", "name": "Sales", "description": "Front counter", "memberCount": 3,
  "createdAtUtc": "2026-11-01T09:30:00+00:00", "updatedAtUtc": null }
```

**What a group is.** The managed form of the `department` a member already has: a member belongs to the group whose **name
equals their department** (`PATCH /api/v1/users/{id}/group`, `GET /users?department=`). Groups grant no permissions and
are unique by name within an organization (any letter case; other organizations may reuse a name). `memberCount` counts
members whether active or not.

**Rename moves the members.** `PUT` with a new name renames the department of every member in the same transaction as
the group, so the two never disagree (a change of letter case only counts too). If the new name is taken, nothing changes.

**Delete only where safe.** A group someone belongs to cannot be deleted (`409 Groups.InUse`, naming how many); move them
with `PATCH /users/{id}/group` first. Nothing else depends on a group.

**Departments typed freely still work.** Assigning a user a department that is not a group is allowed (as before); it is
simply not listed as a group until someone creates one with that name. The migration that added groups turned every
department already in use into a group, so existing data shows up at once.

**Tenant isolation.** Groups always belong to the organization the request acts in (`X-Organization-Id` for Super Admins,
who must name one: `400 Groups.OrganizationRequired`). No request carries an organization id, so another organization's
group answers `404 Groups.NotFound`, with the same text as an id that does not exist. Platform Administrators hold no
organization permissions and cannot use these endpoints.

**Logging.** Creations, renames (with how many members moved), deletions and refusals (`InUse`, unknown or foreign id,
duplicate name) are logged at Information with user, organization and group ids, never group names. Concurrent conflicts
caught by the database (two groups created with the same name at once, a group changed or deleted by someone else) answer
`409` and are logged as warnings; anything else unexpected is a logged `500` with a `correlationId`.

## 9. Audit logs

Code: `Controllers/Api/AuditLogsController.cs` → `IAuditLogService` (`Application/Features/Audit`, rules in
`AuditAccessRules`) → `AuditLogService` (Infrastructure, read side). Entries are written by `AuditTrail`
(`Infrastructure/Auditing`) from the services that make the changes. Table `audit.AuditLogs`. Bearer token required.

| Method and path | Permission (any one listed) | Body | Success |
|---|---|---|---|
| `GET /api/v1/audit-logs` | `audit.view`, `platform.audit.view` | query, see below | `200` page of entries, **newest first** |
| `GET /api/v1/audit-logs/{id}` | `audit.view`, `platform.audit.view` | none | `200` entry with details |

**List query:** `action` (e.g. `user.role_changed`), `entityType` (`User`, `Role`, `Group`, `Organization`), `entityId`
(e.g. a user's whole history), `actorUserId` (everything one person did), `from` / `to` (ISO 8601; `from` inclusive, `to`
exclusive; `400 Audit.InvalidRange` when reversed), `organizationId` (**platform operators only**), `page`, `pageSize`.

List items are `{ id, occurredAtUtc, organizationId, actorUserId, action, entityType, entityId }`. The detail adds what
changed, the request's correlation id (search the server logs for it) and the caller's IP address:

```json
{
  "id": "0f6b…", "occurredAtUtc": "2026-11-01T21:04:12+00:00", "organizationId": "d298…",
  "actorUserId": "7d1c…", "action": "user.role_changed", "entityType": "User", "entityId": "41aa…",
  "details": { "fromRoleId": "5b1f…b07", "fromRole": "ReadOnly", "toRoleId": "5b1f…b06", "toRole": "Accountant" },
  "correlationId": "4bf92f3577b34da6a3ce929d0e0e4736", "ipAddress": "203.0.113.7"
}
```

**What is recorded** (one entry per change; a change that alters nothing, or is refused, records nothing):

| Area | Actions (`details`) | Log |
|---|---|---|
| Users | `user.created` (role, department, set-up e-mail sent), `user.profile_updated` (which fields changed, never the values), `user.role_changed` (from/to role id and name), `user.group_changed` (from/to department), `user.activated` / `user.deactivated` (membership), `user.password_reset_initiated` | The organization's |
| Accounts | `user.account_activated` / `user.account_deactivated` (Super Admin, no organization selected) | Platform-wide |
| Roles and permissions | `role.created` (name, scope, permissions), `role.updated` (from/to name and description), `role.deleted`, `role.permissions_changed` (added, removed) | The organization's for custom roles; platform-wide for shared and system roles |
| | `role.availability_changed` (all organizations, listed organizations) | Platform-wide |
| Groups | `group.created`, `group.updated` (from/to name and description, members moved), `group.deleted` | The organization's |
| Organizations | `organization.created` (name, first administrator's user id), `organization.renamed` (from/to), `organization.suspended`, `organization.reactivated` | The organization's |

`details` never holds passwords, tokens, reset codes, e-mail addresses or phone numbers; people are referred to by id.

**Organization isolation.** Organization users with `audit.view` (Owners and Administrators by default) read only the
log of the organization the request acts in. Another organization's entry, or a platform-wide one, answers
`404 Audit.NotFound` with the same text as an unknown id; naming another organization in `organizationId` answers
`403 Audit.OrganizationFilterNotAllowed`, and in `X-Organization-Id` `403` as everywhere. **Platform operators** (Super
Admins, and Platform Administrators through `platform.audit.view`) read everything, platform-wide entries included.

**Guarantees.** Each entry is added to the same database transaction as its change, so a committed change always has its
entry and a rolled-back one has none. The table is **append-only**: a trigger refuses every `UPDATE` and `DELETE`
(`TR_AuditLogs_AppendOnly`), and the API has no write endpoints. The actor, organization, time, correlation id and IP
address come from the server, never from the request body.

**Logging.** Refused lists (`Audit.OrganizationRequired`, `Audit.OrganizationFilterNotAllowed`) and reads of entries
outside the caller's organization are logged at Information with user and entry ids (useful when someone probes ids).
Details that are not valid JSON (only possible by editing the database) are returned as `null` with a Warning. Writing an
entry is part of the change's `SaveChanges`: if it fails, the change fails with a logged `500` and a `correlationId`.

## 10. Inventory and catalogue

Products, variants, categories, units, organization-defined attributes, warehouses, batches, stock levels, the movement history,
stock adjustments and transfers, and the inventory reports. Code: `Controllers/Api/{Products,Categories,Units,ProductAttributes,
Warehouses,Batches,Stock,StockAdjustments,StockTransfers,InventoryReports}Controller.cs` → services in `Application/Features/{Catalog,
Inventory}` → `Infrastructure/{Catalog,Inventory}`. Database design: [INVENTORY_DATABASE.md](INVENTORY_DATABASE.md). Swagger
describes every endpoint, body and error.

| Resource | Routes (all under `/api/v1`) |
|---|---|
| Products, variants | `products`, `products/{id}`, `products/{id}/status`, `products/{id}/variants[/{variantId}[/status]]` |
| Categories | `categories`, `categories/tree`, `categories/{id}[/status]` |
| Units, conversions | `units[/{id}[/status]]`, `units/conversions[/{id}]`, `units/convert` |
| Attributes | `product-attributes[/{id}[/status]]`, `product-attributes/schema`, `product-attributes/{id}/options[/{optionId}]` |
| Warehouses | `warehouses[/{id}[/status]]`, `warehouses/default`, `warehouses/{id}/default` |
| Batches | `batches[/{id}[/status]]`, `batches/{id}/stock` |
| Stock | `stock`, `stock/low`, `stock/out-of-stock`, `stock-movements` (read-only) |
| Documents | `stock-adjustments[/{id}]`, `stock-adjustments/{id}/post`, `…/cancel`; the same for `stock-transfers` |
| Reports | `inventory/dashboard`, `inventory/reports/valuation`, `…/stock-by-warehouse`, `…/stock-by-product` |

### 10.1 Permissions

| Permission | Endpoints |
|---|---|
| `inventory.view` | Every GET above except the reports (products, categories, units, attributes, warehouses, batches, stock, movements, documents) |
| `inventory.product.manage` | Create/change/retire/delete products, variants, categories, units, conversions |
| `inventory.attribute.manage` | Create/change/retire/delete attributes and their options |
| `inventory.stock.adjust` | Create, post, cancel stock adjustments; register and close batches |
| `inventory.stock.transfer` | Create, post, cancel stock transfers |
| `inventory.manage` | Create, change, retire, delete warehouses; choose the default |
| `inventory.reports.view` | The dashboard and the three reports (they show costs and values) |

A person without the permission gets `403 Auth.PermissionDenied` naming it. Defaults per role: AUTHORIZATION.md.

### 10.2 Tenant isolation and ownership

* **The organization is never in a request.** It is the organization the server resolved for the signed-in user
  (`X-Organization-Id` header, or their only organization, checked against their memberships on every request). No body, query
  or route carries one; an `organizationId` sent anyway is ignored. A unit test fails if any action of these controllers ever
  gains a parameter or request property named like an organization or tenant.
* **Another organization's record is "not found".** Reading, changing or posting it answers `404`, exactly as for an id that does
  not exist; lists never contain it. Naming another organization in `X-Organization-Id` earns no inventory permission at all
  (`403`). A Super Admin must name the organization they work in (`400 Inventory.OrganizationRequired` otherwise) and is then
  confined to it.
* **References are checked.** A product may only point at its organization''s category, unit and attribute options; a variant,
  stock line or document line only at that organization''s warehouse, product, variant and batch, and the variant must be a
  variant of that product, the batch a batch of that product and variant (`400 Products.ReferenceInvalid`,
  `Products.AttributeValuesInvalid`, `Stock.ItemInvalid`, `404 Warehouses.NotFound`). The database repeats these rules with
  composite `(OrganizationId, Id)` foreign keys, so a bug could not cross tenants either.

### 10.3 How stock changes

Stock is never edited. It changes only when a **document is posted**, and every change is a line in the append-only ledger
(`GET /stock-movements`, the product''s transaction history when filtered by product) that also updates the balance
(`GET /stock`):

* **Stock adjustment** (counts, write-offs, found goods, opening stock): `POST /stock-adjustments` creates a draft; `…/post`
  applies every line; `…/cancel` abandons a draft.
* **Stock transfer**: `POST /stock-transfers` creates a draft; `…/post` takes the stock out of the source and into the destination,
  keeping its cost.
* **Sales and Purchases** (later) post through `IStockPostingService` (10.6).

Rules, all checked on the server: the product exists, is active and tracks stock; a product with variants needs one of its own
variants, one without none; a product that tracks batches needs one of its own batches; the quantity has no more decimals than
the unit allows (`Stock.ItemInvalid`); stock never goes below zero, nor into reserved stock, unless the product allows negative
stock (`409 Stock.InsufficientStock`). Cost is the moving weighted average.

### 10.4 Consistency, concurrency and duplicate posting

* **One transaction.** Posting writes every line''s balance change and ledger movement, the document''s new status and its audit
  entry in ONE database transaction: all of it or none (a transfer moves both warehouses or neither).
* **Concurrent updates.** Balances and documents carry a row version. Two requests changing the same stock at once cannot both
  succeed on a stale quantity: the loser gets `409 Stock.ConcurrentChange` and nothing changed. Read the new levels and retry.
  A refusal for lack of stock (`Stock.InsufficientStock`) is final, not a reason to retry.
* **No duplicate posting.** A posted document cannot be posted again (`409 Adjustments.NotDraft` / `Transfers.NotDraft`), also
  when several requests race (exactly one wins). In the ledger, one source line posts a given movement once (unique index
  `IX_StockMovements_SourceLine`), so a repeated posting by another module cannot double the stock either.
* **Always adds up.** For every balance, quantity on hand equals the sum of its ledger movements; tests check this after every
  scenario against the database itself.

### 10.5 Posted history is immutable

* The ledger is append-only: the database refuses UPDATE and DELETE of a movement (error 51010). A mistake is corrected by a
  reversing movement that points at the original, each reversed once.
* A posted or cancelled adjustment, and a received or cancelled transfer, cannot be changed or reopened (51013, 51014), nor
  deleted (51011, 51012); neither can their lines be added, changed or removed (51015, 51016), apart from the one-time link of
  a line to the movement it became. These are database triggers: they hold against bugs and against direct SQL.
* To correct a posted document, post a **new** one (an adjustment in the opposite direction).

### 10.6 For Sales and Purchases: `IStockPostingService`

Other modules never touch balances or the ledger. They call the contract in `Application/Features/Inventory/StockPostingModels.cs`,
so every rule above applies to them:

```csharp
// Inside the module''s own transaction: the invoice and its stock commit together, or neither does.
return await DatabaseTransactions.RunAsync<Result>(db, async () =>
{
    var staged = await stockPosting.StageAsync(new StockPostingRequest(
        "SalesInvoice", invoice.Id, warehouseId, StockPostingKind.Issue,
        invoice.Lines.Select(line => new StockPostingLine(line.Id, line.ProductId, line.VariantId, line.BatchId, line.QuantityInBaseUnit)).ToList(),
        Reference: invoice.Number));
    if (staged.IsFailure) { return staged.Error; }   // not enough stock, wrong warehouse, already posted ...

    invoice.Confirm();
    audit.Record(...);
    await db.SaveChangesAsync(ct);
    return Result.Success();
}, ct);
```

* `StageAsync` joins the caller''s unit of work and saves nothing; `PostAsync` does the same in a transaction of its own.
* There is no organization in the request: it is the current user''s. A source line posts once; posting it again answers
  `409 Stock.AlreadyPosted` and changes nothing. `SourceType` is a short name of your own (`SalesInvoice`, `PurchaseReceipt`);
  `StockAdjustment`, `StockTransfer` and `Manual` belong to the inventory module.
* Cancelling the document: `StageReversalAsync` / `ReverseAsync(sourceType, sourceId)` writes opposite movements for everything it
  posted that is not reversed yet (`409 Stock.AlreadyReversed` when nothing is left), also for products or warehouses retired
  since. Taking back a receipt that has been sold is refused (`Stock.InsufficientStock`).

### 10.7 Audit and logging

Every change to products, variants, attributes and options, categories, units, conversions, warehouses and batches, and every
creation, posting and cancellation of a stock document, writes an `audit.AuditLogs` entry in the same transaction as the change
(who, organization, correlation id, IP address, and what changed: for updates `{ "changes": { "salePrice": { "from": 10, "to": 12 } } }`).
Refused changes write nothing. Read them with `GET /api/v1/audit-logs?entityType=Product` (needs `audit.view`); the actions are
`product.*`, `variant.*`, `attribute.*`, `category.*`, `unit.*`, `warehouse.*`, `batch.*`, `stock_adjustment.*`, `stock_transfer.*`.
The ledger also stamps each movement with who recorded it and when (`recordedByUserId`). Application logs name user,
organization and record ids and counts only, never names, SKUs or notes: refusals at Information, lost races at Warning,
anything unexpected is a logged `500` with a `correlationId`.

### 10.8 Errors

| Status | `code` | When |
|---|---|---|
| 400 | `Inventory.OrganizationRequired` | A Super Admin (who holds every permission) acts in no organization: send `X-Organization-Id`. Members of several organizations who send none get `403`, because no organization means no permissions |
| 400 | `Stock.ItemInvalid` | A line''s product, variant or batch is unknown, retired, of another product, or the quantity does not fit the unit |
| 400 | `Stock.PostingInvalid` | A posting by another module is malformed |
| 400 | `Products.ReferenceInvalid`, `Products.AttributeValuesInvalid`, `Adjustments.Invalid`, `Transfers.Invalid`, `Warehouses.Inactive` | Invalid references or documents |
| 404 | `Products.NotFound`, `Warehouses.NotFound`, `Adjustments.NotFound`, `Transfers.NotFound`, `Batches.NotFound`, … | No such record in your organization (other organizations'' look the same) |
| 404 | `Stock.SourceNotFound` | A reversal names a document that posted nothing here |
| 409 | `Stock.InsufficientStock` | Not enough available stock; nothing was changed |
| 409 | `Stock.ConcurrentChange` | Someone changed the same stock or document at the same moment; nothing was changed: re-read and retry |
| 409 | `Adjustments.NotDraft`, `Transfers.NotDraft` | The document is already posted or cancelled |
| 409 | `Stock.AlreadyPosted`, `Stock.AlreadyReversed` | A source line already posted; everything already reversed |
| 400 | `Stock.SourceNotReversible` | Adjustments and transfers are corrected with a new document |

## 11. Adding an API controller

1. Inherit `AppApiController` and route under `api/v1/`: `[Route("api/v1/products")]` (a unit test enforces the `api/` prefix).
2. Say how users sign in: `[Authorize(AuthenticationSchemes = BearerScheme)]`. Read-only GET actions that the website's own
   pages call may use `BearerOrCookieSchemes`; actions that change data must not (CSRF; a unit test enforces this).
3. Say what they may do: `[RequirePermission(Permissions.Inventory.ProductManage)]`, or `[RequireAnyPermission(...)]` when one of
   several permissions is enough (see AUTHORIZATION.md).
4. Call one Application service per action and return `ErrorResult(result.Error)` on failure.
5. Anonymous actions must be added to `ControllerConventionTests.ApprovedAnonymousActions`.
6. Inventory and catalogue controllers: unit tests (`InventoryApiConventionTests`) require an `inventory.*` permission on every
   action and no organization/tenant property in any request. New stock-changing code goes through the ledger, never around it (10.3).
