# Authorization

Who may do what, and how it is enforced. Authentication (sign-in, tokens, sessions) is in [API.md](API.md);
the security rules are in [SECURITY.md](SECURITY.md).

## 1. Model

```
User ──(Identity role)──────────────► Platform role: SuperAdmin | PlatformAdministrator
  │
  └──(OrganizationMembership.RoleId)──► Organization role (platform.RoleDefinitions)
                                          ├─ system: Owner | Administrator | Manager | InventoryManager
                                          │          | SalesOperator | Accountant | ReadOnly
                                          ├─ shared (platform) roles defined by the Super Admin
                                          └─ custom roles of one organization
Organization role ──(stored list)──► Permissions: "invoices.create", "inventory.product.manage", ...  (catalogue in code)
Super Admin ──(platform.RoleAvailability)──► which shared roles each organization may use
```

| Concept | Where | Notes |
|---|---|---|
| Permission | `Application/Common/Security/Permissions.cs` | `area.action` strings; the only thing endpoints check. Code, not data: each is enforced by endpoints |
| Permission catalogue | `Application/Common/Security/PermissionCatalogue.cs` | Labels, descriptions, groups; served by `GET /api/v1/permissions` |
| Organization roles | `platform.RoleDefinitions` (`Domain/Organizations/RoleDefinition.cs`) | Name + permissions (JSON list); managed with `/api/v1/roles` |
| System roles | `Domain/Organizations/OrganizationRoles.cs` | Fixed ids, created by migration; names reserved; never renamed or deleted |
| Default permissions of system roles | `Application/Common/Security/RolePermissions.cs` | Seeded by migration; afterwards the database applies (Super Admin can edit) |
| Role availability | `platform.RoleAvailability` | Shared roles not available to all organizations: the organizations that may use them |
| Platform roles | `Application/Common/Security/PlatformRoles.cs` | Identity roles, created by migration in every environment |
| Organizations, memberships | `platform.Organizations`, `platform.OrganizationMemberships` | One membership per user per organization; `RoleId` references the role |
| Groups (departments) | `platform.Groups` (`Domain/Organizations/Group.cs`) | Named groups per organization; a member belongs to the group whose name equals their `Department`. Grant no permissions |
| Access rules | `UserAccessCalculator`, `Features/Users/UserAccessRules`, `Features/Roles/RoleAccessRules`, `Features/Audit/AuditAccessRules` | Pure; unit-tested |
| Audit log | `audit.AuditLogs` (`Domain/Auditing/AuditLog.cs`), written by `Infrastructure/Auditing/AuditTrail.cs` | Append-only record of administrative changes; read with `/api/v1/audit-logs` |

**Super Admin** (`SuperAdmin` platform role): every permission, in every organization (it may act in any active
organization through `X-Organization-Id`, which is logged as a warning), and passes every role check.
**PlatformAdministrator**: the `platform.*` permissions only; no access inside organizations.
An organization Administrator never gets platform access.

System roles and their **default** permissions (the Super Admin can change all but the Owner's):

| System role | Grants |
|---|---|
| Owner | Every organization permission, always (fixed, including permissions added in future releases) |
| Administrator | Every organization permission except `subscription.manage` (includes `users.manage`, `roles.manage`, `audit.view`) |
| Manager | All `*.view` except `audit.view` + the whole inventory module (products, attributes, warehouses, stock adjustments and transfers), purchasing and sales management; create/cancel invoices; record payments; export reports |
| InventoryManager | Organization view; the whole inventory module including its reports; purchasing (view + manage); reports view |
| SalesOperator | Organization and inventory view (products and stock levels, no costs or valuations); sales; view/create invoices; view/record payments |
| Accountant | Organization, sales, purchasing view; invoices view; payments view/record; accounts view/manage; reports view/export; subscription view |
| ReadOnly | Every `*.view` permission except `audit.view`, nothing else |

### Inventory permissions

| Permission | Allows |
|---|---|
| `inventory.view` | Read everything in the module: products, categories, units, attributes, warehouses, batches, stock levels, stock documents, the movement history |
| `inventory.product.manage` | Products, variants, categories, units of measure and conversions |
| `inventory.attribute.manage` | The attributes organizations define for their products, and their options |
| `inventory.stock.adjust` | Stock adjustments (counts, write-offs, opening stock) and batches |
| `inventory.stock.transfer` | Stock transfers between warehouses |
| `inventory.manage` | Warehouses: create, change, retire, delete, choose the default |
| `inventory.reports.view` | The inventory dashboard and reports, which show costs and values |

Each permission is independent: holding `inventory.stock.adjust` does not let you read stock (that needs `inventory.view`), so
a custom role usually combines a few. The sales default deliberately lacks `inventory.reports.view`. Migration
`SplitInventoryPermissions` replaced the earlier `catalog.view` and `catalog.manage` in every stored role (system, shared and
custom): `catalog.view` became `inventory.view`, `catalog.manage` became product plus attribute management, `inventory.manage`
also grants stock adjustment and transfer, and whoever could view inventory keeps the reports (except SalesOperator, see
above). Rolling the migration back maps each permission to its closest old one. Tests check that the endpoints require exactly
these permissions (a person holding only one reaches only its endpoints) and that every inventory action carries one.

`audit.view` (the organization's audit log) is deliberately not a "viewer" permission: it shows who changed whose access.
Migration `AddAuditLog` granted it to the stored Administrator role; the Owner holds it automatically.

Permission groups (`GET /api/v1/permissions`): **User Management** (`users.*`, `roles.*`, `audit.view`), **Product Management**
(`inventory.product.manage`, `inventory.attribute.manage`), **Inventory** (`inventory.view`, `inventory.manage`,
`inventory.stock.adjust`, `inventory.stock.transfer`), **Sales** (`sales.*`, `invoices.*`), **Purchases** (`purchasing.*`),
**Accounts** (`accounts.*`, `payments.*`), **Reports** (`reports.view`, `reports.export`, `inventory.reports.view`), **Organization Management**
(`organization.*`, `subscription.*`), plus **Platform Administration** (`platform.*`), which no organization role can grant.

## 2. How a request is authorized

1. **Authentication**: bearer token (API) or sign-in cookie (website). For tokens, `ApiSessionValidator` checks the
   session, security stamp and active account on every request (see API.md §2). Cookies are re-checked against the
   security stamp every 5 minutes; until then a deactivated account's cookie still authenticates but receives **no**
   platform role, organization or permission (step 2 ignores inactive accounts).
2. **Organization claims**: `AccessClaimsTransformation` asks `IUserAccessService` (database) for the user's platform
   roles and memberships, selects the organization (`X-Organization-Id` header, or the only one), and adds claims to
   the principal for this request only: `bs:org_id`, `bs:org_role`, one `bs:permission` per permission,
   `bs:super_admin`. They are never stored in a cookie or token, so they are always current and cannot be forged.
   Anonymous endpoints and unknown routes skip this (no database query).
3. **Authorization**: the endpoint's requirements are checked against those claims by `AccessAuthorizationHandler`.
   Denials are logged and, for `/api`, answered with `403` problem details naming the missing permission or role.

## 3. Protecting an endpoint

```csharp
[Route("api/v1/invoices")]
[Authorize(AuthenticationSchemes = BearerScheme)]          // how the user signs in
public sealed class InvoicesController(IInvoiceService invoices) : AppApiController
{
    [HttpPost]
    [RequirePermission(Permissions.Invoices.Create)]       // permission-based (preferred)
    public Task<IActionResult> Create(...) { ... }

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Invoices.View)]         // several attributes: ALL are required
    [RequirePermission(Permissions.Invoices.Cancel)]
    public Task<IActionResult> Cancel(Guid id) { ... }

    [HttpGet("export")]
    [RequireAnyPermission(Permissions.Reports.Export, Permissions.Invoices.Cancel)]  // ANY ONE of them is enough
    public Task<IActionResult> Export() { ... }

    [HttpDelete("{id:guid}")]
    [RequireOrganizationRole(OrganizationRoles.Owner)]     // role-based: ANY of the listed roles
    public Task<IActionResult> Delete(Guid id) { ... }
}

[RequireSuperAdmin]                                          // Super Admins only
[Authorize(Policy = AuthorizationPolicies.PlatformAdministrator)]  // PlatformAdministrator or SuperAdmin
```

Prefer permissions: roles are data (custom roles, edited permissions), so only permission checks keep working for
every role. `[RequireOrganizationRole]` can only name system roles. A new permission needs: a constant in `Permissions`,
an entry in `PermissionCatalogue` (a unit test enforces it), and, if existing system roles should get it, a migration
updating their stored lists (the Owner gets it automatically). A unit test fails if an attribute names an unknown
permission or role.

`[RequireAnyPermission]` suits an endpoint shared by two kinds of caller, such as organization users and platform
operators. It only lets the caller through the door: the Application service must still decide WHICH data they reach
(see `OrganizationAccessRules`). Denials for `platform.*` permissions do not mention organizations.

In Application services use `ICurrentUser`: `OrganizationId`, `IsSuperAdmin`, `HasPermission(...)`. Always take the
organization from `ICurrentUser`, never from the request body, and filter every query by it.

## 4. Managing access

**Through the API** (organization users: `users.manage`, i.e. Owner or Administrator): `/api/v1/users` creates
members and changes their role, department and status inside the caller's own organization only. The rules (no
self-promotion, Owner-only Owner changes, last-Owner protection, platform accounts untouchable) are in
`Application/Features/Users/UserAccessRules.cs`; see [API.md §5](API.md#5-user-management). An Organization
Administrator can never reach another organization's users: the organization is always the server-verified one.

**Roles and permissions** through `/api/v1/roles` (see [API.md §6](API.md#6-roles-and-permissions)):

| Who | May |
|---|---|
| Super Admin | Create, rename and delete shared (platform) roles; change any role's permissions except the Owner's; decide which shared roles each organization may use (`PUT /roles/{id}/availability`) |
| Organization users with `roles.manage` (Owner, Administrator) | Create, rename, delete and configure their organization's custom roles, granting only permissions they hold themselves; never shared or system roles |
| Organization users with `users.manage` | Assign only roles available to their organization, whose permissions they hold (the Owner role needs an Owner) |

Role and permission changes apply to every holder on their next request. A role someone holds cannot be deleted, and a
shared role cannot be withdrawn from an organization where someone holds it.

**Organizations** through `/api/v1/organizations` (see [API.md §7](API.md#7-organizations)):

| Who | May |
|---|---|
| Super Admin, Platform Administrator (`platform.organizations.manage`) | List and view every organization; create, rename, suspend and reactivate them. Platform Administrators have no organization permissions, so they cannot read an organization's users or roles |
| Super Admin only | Create an organization **together with its first administrator** (`initialAdmin`: the account becomes the organization's Owner). Platform Administrators may create organizations but not that account (`403 Organizations.SuperAdminRequired`) |
| Organization users with `organization.view` | See only the organization they act in |
| Organization users with `organization.manage` (Owner, Administrator) | Rename their own organization; never create, suspend or reactivate organizations, not even their own |

A suspended organization gives its members no access from their next request; reactivating restores it.

An Organization Administrator (Owner or Administrator) can never create an organization or change another one: creating
and suspending need `platform.organizations.manage`, which no organization role can hold (roles never grant
`platform.*` permissions), and every other organization's id is `404`, with or without an `X-Organization-Id` header.

**Groups** through `/api/v1/groups` (see [API.md §8](API.md#8-groups)): viewing needs `users.view`, changing needs
`users.manage` (groups are part of user management). They always work in the organization the request acts in.

**Audit log** through `/api/v1/audit-logs` (see [API.md §9](API.md#9-audit-logs)): organization users with `audit.view`
(Owner, Administrator) read their own organization's log only; Super Admins and Platform Administrators
(`platform.audit.view`) read every organization's and the platform-wide entries. Every change made through the users,
roles, groups and organizations APIs is recorded there, in the same transaction as the change.

**Platform roles** (Super Admin, Platform Administrator) are not managed through the API yet.

Development: the seeder (`DevelopmentSeed:*` user-secrets) creates the dev account, a "Demo Business" it owns
(`DevelopmentSeed:OrganizationName`) and, with `DevelopmentSeed:UserIsSuperAdmin = true`, makes it a Super Admin.

Any environment (SQL, reviewed and run by an administrator):

```sql
-- Make a user Super Admin (role rows are created by migration)
INSERT INTO [identity].[UserRoles] (UserId, RoleId)
SELECT u.Id, r.Id FROM [identity].[Users] u, [identity].[Roles] r
WHERE u.NormalizedEmail = UPPER('person@example.com') AND r.Name = 'SuperAdmin';

-- Create an organization and add a member
DECLARE @org uniqueidentifier = NEWID();
INSERT INTO [platform].[Organizations] (Id, Name, IsActive, CreatedAtUtc) VALUES (@org, N'Al-Noor Traders', 1, SYSUTCDATETIME());
-- RoleId: the Owner system role (ids of all system roles: Domain/Organizations/OrganizationRoles.cs)
INSERT INTO [platform].[OrganizationMemberships] (Id, OrganizationId, UserId, RoleId, IsActive, CreatedAtUtc)
SELECT NEWID(), @org, Id, '5b1f0d55-3c2e-4b7a-9a0e-6f1d2c3b4b01', 1, SYSUTCDATETIME() FROM [identity].[Users] WHERE NormalizedEmail = UPPER('person@example.com');
```

Changes apply on the user's next request; no new sign-in is needed.
