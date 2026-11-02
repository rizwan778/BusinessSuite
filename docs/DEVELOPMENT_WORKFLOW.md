# Development workflow

## 1. Daily loop

```text
git pull  ->  git switch -c feature/<name>  ->  code + tests  ->  dotnet test  ->  commit  ->  push  ->  pull request  ->  CI green  ->  merge to main
```

* Run with hot reload while coding: `dotnet watch run --project src/BusinessSuite.Web --launch-profile https`
  (C# and Razor edits apply without restarting; some changes ask for a restart).
* Keep `main` always buildable and green. Small pull requests, one feature or fix each.

## 2. Branches

| Branch | Use |
|---|---|
| `main` | Stable code. Protected on GitHub: changes arrive through pull requests once you work with others. |
| `feature/<short-name>` | New functionality, e.g. `feature/product-catalog` |
| `fix/<short-name>` | Bug fixes, e.g. `fix/invoice-rounding` |
| `chore/<short-name>` | Tooling, dependencies, docs, e.g. `chore/update-efcore` |

## 3. Commit messages (Conventional Commits)

`<type>: <what changed, imperative, lower case>`

| Type | Example |
|---|---|
| `feat` | `feat: implement tenant-scoped product catalog` |
| `fix` | `fix: validate invoice totals on server` |
| `test` | `test: add tenant isolation tests` |
| `docs` | `docs: explain migration rollback` |
| `refactor` | `refactor: extract stock posting service` |
| `chore` | `chore: initialize solution and repository` |

## 4. Git setup and first commit

Replace the placeholders in `<...>` with your own values; nothing here contains real credentials.

```powershell
# One-time identity for this machine
git config --global user.name "<Your Name>"
git config --global user.email "<you@example.com>"

# From the BusinessSuite folder
./scripts/db.ps1 add InitialCreate   # generate the initial migration first, so CI can build the database
git init -b main
git status                      # check: no bin/, obj/, secrets or .user files listed
git add .
git commit -m "chore: initialize solution and repository"

# Connect to an EMPTY repository you created on GitHub (no README/licence added there)
git remote add origin https://github.com/<your-github-username>/<repository-name>.git
git push -u origin main

# Start the next piece of work on a branch
git switch -c feature/organizations
```

Before the first push, confirm the ignore rules work:

```powershell
git check-ignore -v src/BusinessSuite.Web/bin/Debug/net10.0/BusinessSuite.Web.dll
git check-ignore -v src/BusinessSuite.Web/appsettings.Production.json
git ls-files | Select-String -Pattern "secrets.json|\.pfx|\.env$"   # must print nothing
```

Never disable Git hooks, signing or GitHub secret scanning to make a commit or push succeed.

## 5. Pull request checklist

The template in `.github/pull_request_template.md` is shown automatically. In short: tests (including a negative
case), reviewed migration, authorization on every new action, no secrets, no business logic in controllers or
views, docs updated.

## 6. Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request:

1. Install the .NET SDK version from `global.json`
2. `dotnet restore` and `dotnet build -c Release`
3. Start a disposable SQL Server 2022 container with a random password
4. `dotnet test` (unit, web and SQL Server tests) and upload the `.trx` results

A red build blocks merging (once branch protection is enabled under GitHub → Settings → Branches).

## 7. Database changes

1. Change or add the entity and its `IEntityTypeConfiguration`.
2. `./scripts/db.ps1 add <DescriptiveName>` and **read the generated migration**.
3. `./scripts/db.ps1 update` (or just run the app in Development).
4. Run the tests with SQL Server enabled.
5. Commit the entity, configuration and all migration files together.

Details and rollback: [DATABASE_DESIGN.md](DATABASE_DESIGN.md) §5.

## 8. Local SQL Server options

| Option | Cost | Setup |
|---|---|---|
| LocalDB | Free | Installed with Visual Studio ("Data storage and processing" workload) or the SQL Server Express installer (LocalDB option). Default connection string already configured. |
| SQL Server Express / Developer | Free | Install from Microsoft; use a user-secret connection string. |
| Docker | Free | `tools/sqlserver/docker-compose.yml`; set `MSSQL_SA_PASSWORD` first. |

Inspect data with SQL Server Management Studio (Windows, free) or the VS Code "SQL Server (mssql)" extension.

## 9. Working offline

| Needs internet | Works offline |
|---|---|
| First `dotnet restore` and `dotnet tool restore`; adding/updating NuGet packages; `git push/pull`; CI | Build, run, debug, hot reload, tests, migrations (`dotnet ef`), LocalDB/Docker SQL, all documentation |

The NuGet cache (`%USERPROFILE%\.nuget\packages`) keeps every restored package. Front-end libraries are
committed in `wwwroot/lib`, so the UI never needs a CDN. When offline, `dotnet restore` may print warning
`NU1900` (cannot download the vulnerability database); it is harmless.

## 10. Code style and versions

* `.editorconfig` defines formatting and naming; `dotnet format` applies it. File-scoped namespaces are required.
* Nullable reference types are on, and nullable warnings fail the build.
* Package versions: only in `Directory.Packages.props`. Check for updates monthly:
  `dotnet list package --outdated` and `dotnet list package --vulnerable --include-transitive`.
* Release versions: bump `<Version>` in `Directory.Build.props` and add an entry to `CHANGELOG.md`.
