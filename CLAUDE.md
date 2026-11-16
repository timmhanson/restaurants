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
- **System Administrators oversee the site, not restaurants.** Their menu (Overview, Investment groups,
  Restaurants, People, Reports, Billing, Activity log, Audit log, Security, Connected Systems, System status) is entirely
  their own. Accounts for outside services the whole site uses are entered on Connected Systems. Never
  show them restaurant-level screens unless they chose "View as admin". Put new site-wide features in that area
  (`SystemAdminController`, `/api/system/*`, `SuperAdmin` only) and surface anything needing action in the
  Overview's "Needs your attention" list.
- **Never create a dead end.** Every forced or modal screen needs a visible way out (Sign out / Cancel), and
  nothing may require a step the person cannot complete (e.g. an emailed code with no email on file).
- **Money needs its own permission and a cap.** Anything that spends money (postage today) requires a specific
  permission, a confirmation naming what will be charged, protection against charging twice, and an audit row.
  Third-party keys are entered in the site and stored encrypted with Data Protection (never returned to a
  browser, logged or audited beyond their last four characters), or live in App Service settings.
- **Investment groups never see which companies the site uses for mailing (PostGrid).** Screens, messages, history
  entries, exports and audit rows a group can read say "mailed for you" or "the mailing service"
  (`MailStatuses.ForClients`); the names appear only in the System Administrator's area. Groups also see one
  price per card, never the split between the mail house's cost and the site's fee.
- **Card and bank numbers never touch the site.** Payment methods are entered on Stripe's own page; store only
  Stripe's references and a label. A charge needs the amount confirmed by the person (or automatic payment they
  switched on), an idempotency key so a bill cannot be charged twice, and an audit row.
- **Caching:** API responses and pages are `no-store`; only `/assets/*` is cached. Keep it that way so users
  always get the live version.
- **Secrets:** this repo is public. Never commit passwords, connection strings or keys. Use
  `appsettings.Local.json` (git-ignored) or App Service settings.
- **Stale copies:** every API call from the site carries `X-Session-Id`; callers without it are refused as
  out-of-date copies. New fetches must go through `web/src/api/client.ts` (or send `authHeaders()`).
