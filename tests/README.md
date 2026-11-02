# Tests

| Project | What it covers | Needs |
|---|---|---|
| `BusinessSuite.Tests.Unit` | Domain/Application logic, framework building blocks, **architecture rules** (layer dependencies, thin controllers, approved anonymous actions) | Nothing |
| `BusinessSuite.Tests.Integration` | The real web app in memory: sign-in redirects, anti-forgery, security headers, error pages, rate limiting, health checks | Nothing |
| `BusinessSuite.Tests.Integration` (tests marked `[SqlServerFact]`) | Migrations, auditing, end-to-end sign-in, lockout, deactivated users against **real SQL Server** | A SQL Server and one environment variable |

```bash
dotnet test                       # everything; SQL Server tests are skipped if not configured
dotnet test --filter "FullyQualifiedName~Architecture"   # just one group
```

## Enabling the SQL Server tests

Set `BUSINESSSUITE_TEST_SQLSERVER` to a connection string **without** a database name. Each test class
creates its own database named `BusinessSuite_Test_<random>`, applies all migrations and drops it at the end.
Only databases with that prefix are ever dropped.

```powershell
# Windows, LocalDB (installed with Visual Studio or SQL Server Express)
$env:BUSINESSSUITE_TEST_SQLSERVER = "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true"
dotnet test
```

```bash
# Docker SQL Server (see docs/DEVELOPMENT_WORKFLOW.md), password kept in your shell only
export BUSINESSSUITE_TEST_SQLSERVER="Server=localhost,1433;User ID=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=true"
dotnet test
```

GitHub Actions starts a SQL Server container and sets this variable automatically, so every push runs all tests.

## The migration guard

`MigrationTests.Model_has_no_pending_changes` fails when an entity or configuration changed without a new
migration (and until the initial migration has been generated). The failure message contains the exact command.

## Writing tests

- Name tests as sentences: `Deactivated_users_cannot_sign_in`.
- Arrange / Act / Assert, separated by a blank line.
- Unit tests use hand-written fakes from `TestDoubles/` (no mocking library needed).
- Web tests use `BusinessSuiteWebApplicationFactory`; `CreateSignedInClient()` signs in without a database.
- Never use the EF Core in-memory provider to test data access: it does not behave like SQL Server.
- Always add negative tests (forbidden, invalid, other tenant) next to the happy path.
