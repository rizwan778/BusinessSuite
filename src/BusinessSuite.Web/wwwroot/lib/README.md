# Vendored front-end libraries

These files are committed to the repository on purpose: the application works fully **offline**
(no CDN), and the Content-Security-Policy only allows files served by the application itself.

| Library | Version | Files kept | Licence | Source |
|---|---|---|---|---|
| Bootstrap | 5.3.8 | `dist/css/bootstrap.min.css`, `dist/js/bootstrap.bundle.min.js` (+ source maps) | MIT | npm `bootstrap@5.3.8` |
| Bootstrap Icons | 1.13.2 | `font/bootstrap-icons.min.css`, `font/fonts/*.woff2, *.woff` | MIT | npm `bootstrap-icons@1.13.2` |
| jQuery | 3.7.1 | `dist/jquery.min.js` | MIT | .NET 10 MVC template |
| jQuery Validation | 1.21.0 | `dist/jquery.validate.min.js` | MIT | .NET 10 MVC template |
| jQuery Validation Unobtrusive | 4.0.0 | `dist/jquery.validate.unobtrusive.min.js` | Apache-2.0 | .NET 10 MVC template |

jQuery and the validation plugins are used only for client-side form validation (a convenience;
the server always validates again). Bootstrap's own JavaScript does not need jQuery.

## Updating a library

1. On a machine with internet access: `npm pack bootstrap@5.3.x` (or the library you need) and extract the `.tgz`.
2. Copy only the files listed above into the matching folder here, replacing the old ones.
3. Update the version in this table and in `CHANGELOG.md`.
4. Run the application and click through the layout, sign-in page and dashboard.
5. Commit with a message such as `chore: update bootstrap to 5.3.9`.

Never edit vendored files by hand. Put overrides in `wwwroot/css/site.css`.
