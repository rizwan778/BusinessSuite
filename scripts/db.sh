#!/usr/bin/env bash
# =============================================================================
# EF Core database helper for BusinessSuite (Linux, macOS, WSL, Git Bash).
# Same commands as scripts/db.ps1. Uses the Development configuration
# (appsettings.Development.json + your user-secrets) to find the database.
#
#   ./scripts/db.sh add InitialCreate    create a migration after changing entities
#   ./scripts/db.sh update               apply all pending migrations to your local database
#   ./scripts/db.sh update AddProducts   move the database to a named migration (also used to ROLL BACK)
#   ./scripts/db.sh list                 list migrations and whether each is applied
#   ./scripts/db.sh remove               delete the LAST migration (only if never applied elsewhere)
#   ./scripts/db.sh script               idempotent SQL script -> artifacts/migrations.sql
#   ./scripts/db.sh bundle               migration executable -> artifacts/efbundle
# =============================================================================
set -euo pipefail

usage() {
  sed -n '3,13p' "$0" | sed 's/^# \{0,1\}//'
  exit 1
}

[[ $# -ge 1 ]] || usage
command="$1"
name="${2:-}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
projects=(--project "$root/src/BusinessSuite.Infrastructure" --startup-project "$root/src/BusinessSuite.Web")
artifacts="$root/artifacts"

run() {
  echo "> dotnet $*" >&2
  dotnet "$@"
}

cd "$root"
# Installs the pinned dotnet-ef version from .config/dotnet-tools.json (needs internet the first time only).
run tool restore

case "$command" in
  add)
    [[ -n "$name" ]] || { echo "Give the migration a descriptive name, e.g.: ./scripts/db.sh add AddProducts" >&2; exit 1; }
    run ef migrations add "$name" "${projects[@]}" --output-dir Persistence/Migrations
    echo "Review the generated file in src/BusinessSuite.Infrastructure/Persistence/Migrations before committing."
    ;;
  update)
    if [[ -n "$name" ]]; then
      run ef database update "$name" "${projects[@]}"
    else
      run ef database update "${projects[@]}"
    fi
    ;;
  list)
    run ef migrations list "${projects[@]}"
    ;;
  remove)
    run ef migrations remove "${projects[@]}"
    ;;
  script)
    mkdir -p "$artifacts"
    run ef migrations script --idempotent --output "$artifacts/migrations.sql" "${projects[@]}"
    echo "Idempotent SQL script written to $artifacts/migrations.sql. Review it, back up the database, then run it."
    ;;
  bundle)
    mkdir -p "$artifacts"
    run ef migrations bundle --force --output "$artifacts/efbundle" "${projects[@]}"
    echo "Migration bundle written to $artifacts/efbundle. Run it with --connection \"<connection string>\"."
    ;;
  *)
    usage
    ;;
esac
