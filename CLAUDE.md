# Working on this repo

See README.md for the architecture, roles and deployment.

## Rules for every new feature

- **Log every user interaction, tagged to the investment group.**
  - API calls are logged automatically by `InteractionLoggingMiddleware`. Don't bypass it.
  - Call `AuditService.LogAsync` for meaningful actions, with the restaurant id. That names the action and
    tags the restaurant on the request's log row.
  - Browser clicks, choices and page views are picked up by `web/src/api/tracker.ts` for any button, link,
    select, checkbox or radio. Give icon-only buttons an `aria-label` so the log says what was clicked.
  - Never log passwords, codes, tokens or anything typed into a field.
- **Only System Administrators (`SuperAdmin`) may read the interaction log or the system reports**
  (`SystemController`, `/reports`, `/activity`). Don't expose them to group admins, owners or managers unless
  the user decides otherwise.
- **Reports for System Administrators must filter by investment group and/or restaurant.** When a feature adds
  data worth reporting on, add it to `GET /api/system/reports` and the Reports page.
- **Caching:** API responses and pages are `no-store`; only `/assets/*` is cached. Keep it that way so users
  always get the live version.
- **Secrets:** this repo is public. Never commit passwords, connection strings or keys. Use
  `appsettings.Local.json` (git-ignored) or App Service settings.
- **Commits:** end commit messages with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
