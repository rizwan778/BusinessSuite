# Configuration

## 1. Where settings come from

ASP.NET Core reads settings in this order; **later sources override earlier ones**:

1. `src/BusinessSuite.Web/appsettings.json` — safe defaults for every environment (committed, no secrets)
2. `appsettings.{Environment}.json` — e.g. `appsettings.Development.json` (committed, no secrets)
3. User secrets — **Development only**, stored outside the repository
4. Environment variables — production and CI (`Section__Key`, double underscore)
5. Command-line arguments — `dotnet run -- --Application:ProductName="Demo"`

The environment comes from `ASPNETCORE_ENVIRONMENT` (`Development` in `launchSettings.json`;
**`Production` when not set**). Every settings class is validated when the app starts: a wrong value stops
start-up with a message that names the setting.

## 2. Settings reference

| Key | Default | Description | Secret? |
|---|---|---|---|
| `ConnectionStrings:DefaultConnection` | `""` (Development: LocalDB) | SQL Server connection string | **Yes** if it contains a password |
| `Application:ProductName` | `Business Suite` | Name in titles, sidebar, e-mails | No |
| `Application:DefaultTimeZone` | `Asia/Karachi` | Display time zone until organizations have their own (IANA id, e.g. `UTC`, `Asia/Riyadh`) | No |
| `Database:CommandTimeoutSeconds` | `30` | SQL command timeout (5–600) | No |
| `Database:MaxRetryCount` | `0` | Retries on transient SQL errors (0 = off; see DATABASE_DESIGN.md §6) | No |
| `Database:ApplyMigrationsOnStartup` | `false` (Development: `true`) | Apply migrations at start-up; **ignored outside Development** | No |
| `Database:EnableDetailedErrors` | `false` (Development: `true`) | Detailed EF Core errors; Development only | No |
| `Database:EnableSensitiveDataLogging` | `false` | Log SQL parameter values; Development only | No |
| `Authentication:SessionTimeoutMinutes` | `480` | Idle minutes before sign-in is required again (5–1440) | No |
| `Identity:Password:RequiredLength` | `12` | Minimum password length (other `IdentityOptions` can be set the same way) | No |
| `Identity:Lockout:MaxFailedAccessAttempts` | `5` | Failed sign-ins before lockout | No |
| `Identity:Lockout:DefaultLockoutTimeSpan` | `00:15:00` | Lockout duration | No |
| `RateLimiting:LoginPermitLimit` | `10` | Sign-in POSTs per client IP per window | No |
| `RateLimiting:LoginWindowSeconds` | `60` | Rate-limit window length | No |
| `RateLimiting:RegistrationPermitLimit` | `5` | API sign-ups per client IP per window | No |
| `RateLimiting:RegistrationWindowSeconds` | `900` | Sign-up rate-limit window length (1–86400) | No |
| `Authentication:ApiAccessTokenMinutes` | `60` | API access-token lifetime (5–1440) | No |
| `Authentication:ApiRefreshTokenDays` | `14` | API refresh-token lifetime / session idle limit, sliding (1–90) | No |
| `Authentication:ApiSessionMaxDays` | `30` | API session absolute limit after login, however often refreshed (1–365) | No |
| `Email:PickupDirectory` | not set (Development: `App_Data/emails`) | Development e-mail delivery: each e-mail (password reset, account set-up) is written to this folder as an `.eml` file, relative to the web project. Not set: sending fails with an error until an e-mail provider exists (Phase 5). The files contain reset codes: development machines only | No |
| `Email:FromAddress` | `no-reply@businesssuite.local` | Sender address of e-mails | No |
| `DevelopmentSeed:OrganizationName` | `Demo Business` | Organization created for the development account, which becomes its Owner (empty: none) | No |
| `DevelopmentSeed:UserIsSuperAdmin` | `false` | Make the development account a Super Admin | No |
| `SecurityHeaders:ContentSecurityPolicy` | strict policy (SECURITY.md §5) | Content-Security-Policy header value | No |
| `DevelopmentSeed:UserEmail` / `UserPassword` / `UserDisplayName` | not set | Development sign-in account, created at start-up (Development only) | **Yes** |
| `Logging:LogLevel:*` | see appsettings | Log levels per category | No |
| `Logging:Console:FormatterName` | `json` (Development: `simple`) | Log line format | No |
| `AllowedHosts` | `*` | Host names the app answers to; set your domain in production | No |

## 3. Connection strings

| Database | Connection string | Where to put it |
|---|---|---|
| LocalDB (default) | `Server=(localdb)\MSSQLLocalDB;Database=BusinessSuite_Dev;Trusted_Connection=True;MultipleActiveResultSets=False;TrustServerCertificate=True` | already in `appsettings.Development.json` (no secret) |
| SQL Server Express (Windows login) | `Server=.\SQLEXPRESS;Database=BusinessSuite_Dev;Trusted_Connection=True;TrustServerCertificate=True` | user-secrets |
| Docker SQL Server (`tools/sqlserver`) | `Server=localhost,1433;Database=BusinessSuite_Dev;User ID=sa;Password=<password>;TrustServerCertificate=True` | user-secrets (**contains a password**) |
| Production SQL Server / Azure SQL | `Server=<host>;Database=BusinessSuite;User ID=<app user>;Password=<secret>;Encrypt=True` | environment variable or secret store |

Production: use a dedicated SQL login with only the rights the app needs (not `sa`), and `Encrypt=True`
with a trusted certificate (no `TrustServerCertificate=True`).

## 4. User secrets (development)

The Web project already has a `UserSecretsId`. Run from the repository root:

```powershell
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<connection string>" --project src/BusinessSuite.Web
dotnet user-secrets set "DevelopmentSeed:UserEmail" "you@example.com" --project src/BusinessSuite.Web
dotnet user-secrets set "DevelopmentSeed:UserPassword" "<long unique password>" --project src/BusinessSuite.Web
dotnet user-secrets list --project src/BusinessSuite.Web
dotnet user-secrets remove "DevelopmentSeed:UserPassword" --project src/BusinessSuite.Web
```

Stored in `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json` (Windows) or
`~/.microsoft/usersecrets/<UserSecretsId>/secrets.json` (Linux/macOS). Shape: `docs/examples/user-secrets.example.json`.
User secrets are not encrypted; they simply keep secrets out of Git. They are not used in production.

## 5. Environment variables (production, CI)

Replace `:` with `__` (two underscores):

```powershell
# PowerShell (current session)
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ConnectionStrings__DefaultConnection = "<connection string>"
$env:Application__DefaultTimeZone = "Asia/Karachi"
```

```bash
export ASPNETCORE_ENVIRONMENT=Production
export ConnectionStrings__DefaultConnection='<connection string>'
```

On a server, set them in the hosting platform (IIS application pool / `web.config` environment variables,
systemd `Environment=`, container settings, or the cloud provider's app settings / secret store).
Do not create an `appsettings.Production.json` with secrets; `.gitignore` blocks that file name on purpose.

## 6. Logging

* Development: one readable line per entry with the correlation id, e.g.
  `10:15:02 info: BusinessSuite.Web.Controllers.AccountController[0] => ... => CorrelationId:7f3c... User 2a1b... signed in.`
* Production: JSON lines (`Logging:Console:FormatterName = json`) that log collectors can parse.
* See the SQL that EF Core runs (Development): `Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command = Information`
  (already set in `appsettings.Development.json`; set to `Warning` to silence it).
* More detail for one area: e.g. `Logging:LogLevel:Microsoft.AspNetCore.Authentication = Debug`.

## 7. Content-Security-Policy changes

Only relax the CSP for a concrete, reviewed need (e.g. a payment provider's hosted page in Phase 5). Change the
value in `appsettings.json`, document why in `docs/SECURITY.md`, and keep `default-src 'self'` and
`object-src 'none'`. Development already allows `ws:`/`wss:` for hot reload.
