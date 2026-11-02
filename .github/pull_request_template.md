## What and why

<!-- One or two sentences: what changes and which problem it solves. -->

## Checklist

- [ ] Builds and `dotnet test` passes locally (SQL Server tests enabled if data access changed)
- [ ] New behaviour has tests, including a negative case (invalid input, forbidden, other organization)
- [ ] Entity/configuration change: migration added with `scripts/db.ps1 add <Name>` and reviewed
- [ ] Authorization: every new action is protected; any new `[AllowAnonymous]` is justified and listed in `ControllerConventionTests`
- [ ] No secrets, connection strings with passwords, or personal data in code, config, logs or screenshots
- [ ] No business rules in controllers, views or JavaScript; totals/prices calculated on the server
- [ ] Docs updated (`docs/`, `CHANGELOG.md`) if behaviour, configuration or setup changed
