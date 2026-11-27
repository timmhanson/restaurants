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
  show them restaurant-level screens unless they chose "Impersonate" (once called "View as admin"). Put new site-wide features in that area
  (`SystemAdminController`, `/api/system/*`, `SuperAdmin` only) and surface anything needing action in the
  Overview's "Needs your attention" list.
- **Permissions and settings are handed down level by level, and the higher level wins.** System Administrator
  (per investment group) → Investment Group Administrators (who can do everything in their group) → Restaurant
  Owners → Managers; team logins only enter cards. Check `me.Has(ManagerPermission.X)` for anything an owner or
  manager does, never the role alone: owners hold only what `PermissionPolicy` leaves them, and a manager never
  holds what the group's owners do not. A new ability gets a `ManagerPermission` (owner-only ones go in
  `PermissionPolicy.OwnerOnly`) so it shows on the Permissions screens by itself. A new group setting says in
  plain words who decides it ("X for all restaurants", "Each restaurant decides", "the group decides") and goes
  through `GroupPolicy`; a level never changes what the level above has decided.
- **Group and permission screens save each change as it is made** (no Save button), and say "Saved." The System
  Administrator's Edit group dialog is the exception: it saves with its own Save.
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
- **Cards a group prints itself for a stamp by hand are charged the site's fee** (once per card, at its first
  print; the System Administrator can switch this off). Anyone who can print can incur it, so it has no separate
  permission; it still needs the amount shown and confirmed before printing, and an audit row. To the group it is
  "what a card you print yourself costs", never "the site's fee".
- **Credit given to a group is a ledger, not a number.** A credit (`GroupCredit`) needs a reason and details, a
  confirmation naming the amount, a key so it cannot be recorded twice, and an audit row. Lines are never edited
  or removed: a mistake is put right with a new line (take back, or cancel the bill to return it). Only System
  Administrators give credit or see its details; the group sees the amounts.
- **Card and bank numbers never touch the site.** Payment methods are entered on Stripe's own page; store only
  Stripe's references and a label. A charge needs the amount confirmed by the person (or automatic payment they
  switched on), an idempotency key so a bill cannot be charged twice, and an audit row.
- **What an outside service tells the site is kept, and kept private.** Stripe's notifications are stored whole
  (`StripeEvent`) after their signature is checked, shown only in the System Administrator's Billing area, and never
  copied into the application log, the audit log's details or anything a group can read.
- **Say "Loading…" until the answer is in.** Never show an empty-state message ("not assigned to a restaurant",
  "nothing yet") before the data has come back; `contextLoaded` in `AuthContext` says when the person's restaurants
  are known.
- **Back up before every deploy and every change to the database's structure.** The site copies its database at
  every start, before migrations run, and every hour (`DatabaseBackups`); take a hand copy through Kudu before
  pushing as well, and never run SQL against the live database without one.
- **Everything worth keeping must be in a backup.** The SQLite database is copied to the site's disk and
  to Blob Storage (`DatabaseBackups`). Keep new data in the database (or in Blob Storage) so it is covered; never
  put the Data Protection key ring, or any key, into a backup.
- **Caching:** API responses and pages are `no-store`; only `/assets/*` is cached. Keep it that way so users
  always get the live version.
- **Secrets:** this repo is public. Never commit passwords, connection strings or keys. Use
  `appsettings.Local.json` (git-ignored) or App Service settings.
- **Stale copies:** every API call from the site carries `X-Session-Id`; callers without it are refused as
  out-of-date copies. New fetches must go through `web/src/api/client.ts` (or send `authHeaders()`).
