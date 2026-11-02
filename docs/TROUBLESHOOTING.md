# Troubleshooting

Find the message you see, then apply the fix. Most entries also apply in CI.

## Setup and build

| Symptom / message | Cause | Fix |
|---|---|---|
| `A compatible .NET SDK was not found` / `Requested SDK version: 10.0.100` | .NET 10 SDK not installed | Install the latest .NET 10 SDK; check with `dotnet --list-sdks`. Do not lower the target framework. |
| `NETSDK1045: The current .NET SDK does not support targeting .NET 10.0` | An older SDK is used (e.g. an old Visual Studio) | Install the .NET 10 SDK and use Visual Studio 2026 or VS Code. |
| `NU1301: Unable to load the service index` | No internet during the first restore | Restore once with internet; afterwards the NuGet cache works offline. |
| `NU1900: Error occurred while getting package vulnerability data` | Offline restore | Harmless warning; the vulnerability check needs internet. |
| `Cannot find command 'dotnet ef'` / `dotnet-ef does not exist` | Local tools not restored | `dotnet tool restore` (repository root). |
| Build error `CS8600`–`CS8625` (nullable) | Nullable warnings are treated as errors | Handle the possible `null` (check, `??`, or correct the type). |
| Build warning `IDE0161` | Block-scoped namespace | Use `namespace X;` (file-scoped). |

## Start-up

| Symptom / message | Cause | Fix |
|---|---|---|
| `No EF Core migrations exist yet ...` | Initial migration not generated | `./scripts/db.ps1 add InitialCreate`, then run again and commit the generated files. |
| `The model for context 'AppDbContext' has pending changes` | Entities changed without a migration | `./scripts/db.ps1 add <Name>`, review, run again. |
| `OptionsValidationException: ... ConnectionStrings:DefaultConnection is missing` | No connection string for this environment | Development: check `appsettings.Development.json` / user-secrets. Production: set `ConnectionStrings__DefaultConnection`. |
| `OptionsValidationException: Time zone '...' was not found` | Invalid `Application:DefaultTimeZone` | Use an IANA id such as `Asia/Karachi` or `UTC`. |
| `SqlException ... error: 50/52 - Local Database Runtime error` | LocalDB not installed | Install SQL Server Express LocalDB, or switch to SQL Express/Docker via user-secrets. |
| `SqlException: Cannot open database "BusinessSuite_Dev"` / `Login failed` | Database missing or wrong credentials | Development creates it on start-up; check the connection string and that SQL Server is running. |
| `The development user could not be created: Passwords must ...` | Seed password violates the policy | Use 12+ characters with upper, lower, digit and symbol. |
| `Failed to bind to address https://localhost:7180: address already in use` | Another instance is running | Stop it, or change the port in `Properties/launchSettings.json`. |
| `Unable to configure HTTPS endpoint. No server certificate was specified` | No development certificate | `dotnet dev-certs https --trust`. |

## Browser

| Symptom | Cause | Fix |
|---|---|---|
| Certificate warning on `https://localhost:7180` | Development certificate not trusted | `dotnet dev-certs https --trust` and restart the browser. |
| Sign-in always says "Invalid e-mail address or password" | No such user, wrong password, account deactivated, or locked | Set `DevelopmentSeed:*` user-secrets and restart; check `identity.Users` (`IsActive`, `LockoutEnd`). |
| "Too many attempts" (429) on sign-in | Rate limit reached | Wait a minute, or raise `RateLimiting:LoginPermitLimit` in user-secrets while testing. |
| "Bad request" (400) after submitting a form | Missing/expired anti-forgery token or cookies blocked on `http://` | Reload the page; build forms with `<form asp-...>`; always use `https://`. |
| Page without styles, or JavaScript not running | CSP blocked an inline script/style or external file | F12 → Console shows the blocked item; move code to `wwwroot/js`/`css`; no CDN links. |
| Signed out unexpectedly | Session idle timeout, password change or deactivation (security stamp) | Expected behaviour; adjust `Authentication:SessionTimeoutMinutes` if needed. |
| Redirected to sign-in when opening a non-existent URL | Unknown URLs require sign-in first (secure by default) | Sign in; signed-in users see the 404 page. |

## Tests

| Symptom | Cause | Fix |
|---|---|---|
| `[SqlServerFact]` tests shown as skipped | `BUSINESSSUITE_TEST_SQLSERVER` not set | See `tests/README.md`. |
| `Model_has_no_pending_changes` fails | Missing migration (or initial migration not generated yet) | Follow the message in the test output. |
| SQL tests fail with `Login failed` / timeout | Wrong test connection string or SQL Server not running | Test it with SSMS; for LocalDB use `Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`. |
| `ControllerConventionTests.Only_approved_actions_allow_anonymous_access` fails | A new `[AllowAnonymous]` was added | Confirm it is intended and safe, then add it to `ApprovedAnonymousActions`. |
| `LayerDependencyTests` fails | A project referenced a layer it must not | Move the code to the right layer or introduce an interface in Application. |
| Leftover `BusinessSuite_Test_*` databases | A test run was killed | Safe to drop them manually. |

## Scripts

| Symptom | Fix |
|---|---|
| `running scripts is disabled on this system` (PowerShell) | `powershell -ExecutionPolicy Bypass -File .\scripts\db.ps1 <command>` |
| `./scripts/db.sh: Permission denied` | `chmod +x scripts/db.sh` (or run `bash scripts/db.sh ...`) |
