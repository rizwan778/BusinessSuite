<#
.SYNOPSIS
    EF Core database helper for BusinessSuite (Windows PowerShell 5.1+ or PowerShell 7+).

.DESCRIPTION
    Wraps the `dotnet ef` commands with the correct project paths so you do not have to remember them.
    Run it from any folder. It uses the Development configuration (appsettings.Development.json and
    your user-secrets) to find the database.

.EXAMPLE
    ./scripts/db.ps1 add InitialCreate     # create a migration after changing entities
    ./scripts/db.ps1 update                # apply all pending migrations to your local database
    ./scripts/db.ps1 update AddProducts    # move the database to a named migration (also used to ROLL BACK)
    ./scripts/db.ps1 update 0              # roll back everything (empty schema) - local databases only!
    ./scripts/db.ps1 list                  # list migrations and whether each is applied
    ./scripts/db.ps1 remove                # delete the LAST migration (only if it was never applied elsewhere)
    ./scripts/db.ps1 script                # idempotent SQL script for production -> artifacts/migrations.sql
    ./scripts/db.ps1 bundle                # migration executable for deployment -> artifacts/efbundle(.exe)

.NOTES
    If Windows blocks the script ("running scripts is disabled"), run:
        powershell -ExecutionPolicy Bypass -File .\scripts\db.ps1 add InitialCreate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('add', 'update', 'list', 'remove', 'script', 'bundle')]
    [string] $Command,

    [Parameter(Position = 1)]
    [string] $Name
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$infrastructure = Join-Path $root 'src/BusinessSuite.Infrastructure'
$web = Join-Path $root 'src/BusinessSuite.Web'
$projects = @('--project', $infrastructure, '--startup-project', $web)
$artifacts = Join-Path $root 'artifacts'

function Invoke-Dotnet {
    param([string[]] $Arguments)
    Write-Host "> dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

Push-Location $root
try {
    # Installs the pinned dotnet-ef version from .config/dotnet-tools.json (needs internet the first time only).
    Invoke-Dotnet @('tool', 'restore')

    switch ($Command) {
        'add' {
            if ([string]::IsNullOrWhiteSpace($Name)) {
                throw 'Give the migration a descriptive name, e.g.: ./scripts/db.ps1 add AddProducts'
            }
            Invoke-Dotnet (@('ef', 'migrations', 'add', $Name) + $projects + @('--output-dir', 'Persistence/Migrations'))
            Write-Host 'Review the generated file in src/BusinessSuite.Infrastructure/Persistence/Migrations before committing.' -ForegroundColor Yellow
        }
        'update' {
            if ([string]::IsNullOrWhiteSpace($Name)) {
                Invoke-Dotnet (@('ef', 'database', 'update') + $projects)
            }
            else {
                Invoke-Dotnet (@('ef', 'database', 'update', $Name) + $projects)
            }
        }
        'list' {
            Invoke-Dotnet (@('ef', 'migrations', 'list') + $projects)
        }
        'remove' {
            Invoke-Dotnet (@('ef', 'migrations', 'remove') + $projects)
        }
        'script' {
            New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
            $output = Join-Path $artifacts 'migrations.sql'
            Invoke-Dotnet (@('ef', 'migrations', 'script', '--idempotent', '--output', $output) + $projects)
            Write-Host "Idempotent SQL script written to $output. Review it, back up the database, then run it." -ForegroundColor Green
        }
        'bundle' {
            New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
            $output = Join-Path $artifacts 'efbundle'
            if ($env:OS -eq 'Windows_NT') { $output = "$output.exe" }
            Invoke-Dotnet (@('ef', 'migrations', 'bundle', '--force', '--output', $output) + $projects)
            Write-Host "Migration bundle written to $output. Run it with --connection `"<connection string>`"." -ForegroundColor Green
        }
    }
}
finally {
    Pop-Location
}
