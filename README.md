# Restaurant Guest Feedback

Tracks the guest comment / coupon postcards that restaurant teams hand out when something goes wrong,
replacing the spreadsheet. Built for investment groups that operate several Culver's restaurants.

- **Back end:** ASP.NET Core 10 REST API (`src/RestaurantFeedback.Api`), EF Core + SQLite, JWT auth.
- **Front end:** React 19 + Vite + TypeScript (`web`), served by the API from `wwwroot` in production.
- **Settings:** one JSON blob per investment group in Azure Blob Storage (local files in development).
- **Email:** Azure Communication Services Email sends the 6-digit verification codes (logged to the console in development).
- **Hosting:** Azure App Service **F1 (free)** + a Standard_LRS storage account, all in `rg-restaurant-feedback`.

## Roles and permissions

| Title (internal role) | Scope | Can |
| --- | --- | --- |
| **System Administrator** (`SuperAdmin`) | every investment group | oversees the groups: creates them and their administrators, sees every user and the whole audit log. **View as admin** opens one group exactly as its Investment Group Administrator sees it, under a yellow bar with **Exit to System Administration** (the API takes the group from the `X-Group-Id` header). Not assigned to any restaurant |
| **Investment Group Administrator** (`GroupAdmin`) | one investment group | every restaurant, every user account (owners and other administrators included) and the group settings |
| **Restaurant Owner** (`Owner`) | the restaurants they own | everything at those restaurants: cards, analytics, export, audit, staffing and co-owners. They edit, reset, suspend and restore the managers and team logins who work there, even ones who also work at other owners' restaurants |
| **Manager** (`Manager`) | one or more restaurants, across owners | enter cards plus whatever is granted: ViewComments, EditComments, DeleteComments, MarkResponded, ViewAnalytics, ExportComments, ViewAudit, ManageTeamLogin. Permissions apply at every restaurant they work at |
| **Team Member** (`TeamMember`) | the shared login for a restaurant | enter cards only (duplicate checks return counts, never details) |

Rules worth knowing:

- A group always keeps at least one active Investment Group Administrator; the last one cannot be suspended, removed or demoted.
- Only administrators change roles, change another owner's account or edit group settings.
- Suspending (`PUT /api/users/{id}/status`) blocks the whole account at once and is audited with every
  restaurant affected, so the other owners see it. Removing someone who also works for other owners is
  left to them or the administrators.
- Owners see the audit log for their restaurants and the people who work there, not the whole group.
- With an Investment Group Administrator in the group, a restaurant may have no specific owner (the administrators run
  it) and owners cannot claim restaurants themselves. In an older group without one, owners can still
  claim a restaurant that has no owner, and every restaurant must keep one.

### Setting up a group

1. A System Administrator creates the investment group on **Groups** with at least one Group
   Administrator, optional Restaurant Owners and the group's restaurants, ticking who owns each restaurant.
2. **Set up** opens the group's **Restaurants & staff** page. Each restaurant's **Staff** screen lists the
   group's owners, managers and team logins as checkboxes, with quick-add for new people.
3. Owners staff only the restaurants they own; managers can be added at several owners' restaurants.

### Following up on cards

Each card has a follow-up history every manager of the restaurant can read:

- **Record reply** asks how the guest was replied to (the group's reply methods, editable in Settings),
  the date, what was sent or said, and a tick confirming the reply was actually sent or made.
- **Log attempt** records a try that did not reach the guest, such as a voicemail.
- **Reopen** puts a card back in the queue and needs a reason.
- **Notes** are shared with the other managers and never change the status.

The **Reply queue** on Comments lists every card still waiting, earliest visit first. Analytics rolls up
all of a person's restaurants (or any chosen set) with a per-restaurant comparison, a monthly trend per
restaurant, reply methods and the average days from visit to reply.

Roles, permissions and restaurant assignments are read from the database on every request, so changes
(including deactivating a user) take effect immediately without the person signing in again.

Password rules: everyone can change their own password, but only with a 6-digit code emailed to the
address on file (`POST /api/auth/change-password/request`, then `POST /api/auth/change-password` with the
current password, new password and code; codes last 10 minutes, 5 attempts, one resend per minute).
Administrators can reset anyone's password in their scope, and owners can reset the managers and team
logins at their restaurants (managers with `ManageTeamLogin` can reset the team login). A reset by someone else forces a new password at the next login, except for team logins, which
usually have no email and are managed by the owner/manager. System and Investment Group Administrators can untick
**Require a new password at next sign-in** (`requirePasswordChange: false`) to set a password the person keeps.
An account with no email is never forced to change its password, because the change needs an emailed code and
could not be completed; an account already in that state is released at its next sign-in. The forced
"Choose a new password" page has **Sign out** at the top.

Adding a user starts with "What kind of login is this?" (nothing preselected), so a shared **Team Member**
login is not created as a manager by accident.

## The card

Signing in lands everyone on **Enter card**, since entering cards is the main job (System Administrators
land on the group overview).

The first question is **how the comment came in**: an SMG survey, the Contact Us form on Culvers.com, or a
phone call. A phone call asks for the guest's phone number and will not save without it. The source shows
on the card, in the comments list, in the export ("Came In Via") and in Analytics ("How comments came in",
with replied and waiting counts). Cards entered before this was asked show "not recorded".

The entry form mirrors the physical postcard: coupon offer ("Please enjoy ___"), date issued, expiry
(issue date + the group's validity months, 3 by default), authorized by; guest name, street, city/state/zip,
phone; plus visit date, reason category and details. Reason categories and coupon offers are configurable
per group.

Duplicate detection runs on every card (see `GuestMatcher`): phone number wins, then name + street
(abbreviations normalised), then name + zip. The API returns **409** with the matches when the same guest,
visit date and reason already exist (exact duplicate) or when the guest has another card within the
configured window (likely duplicate); the UI shows the alert and offers "Submit anyway" (`force: true`).
A guest with two or more cards on file anywhere in the group is flagged as a **repeat guest**, listed in
analytics and filterable in the comments view.

Every login, card, edit, response, export, user, restaurant and settings change is written to the audit log.

## Printing, postage, follow-up and redemption

**Printing.** The **Print cards** tab chooses what to print: everything **not sent yet** (the default), the
cards for **a specific date**, or **a date range** (by the date entered or the visit date). It covers every
restaurant the person works at unless one is picked, can leave out cards with no full mailing address or
already printed, and sets the postage choice and whether printing marks the cards as sent. Cards can also be
ticked on Comments, or printed singly with **Print card**. Each card prints on its own page at the card's
real size. Investment group settings choose the stock:

- **Pre-printed cards:** only the blanks are printed (offer, dates, authorized by, guest name and address),
  each at a position measured in inches from the card's top-left corner, with a nudge for the printer.
- **Blank postcards:** the whole card is printed, with the return address and a box for the stamp.

Settings shows a real-size preview and prints a test card. After the print dialog closes, one confirmation
marks every card as sent (reply method "Mailed coupon card") and records the print in the card's history.

**Checking addresses.** As a card is entered, the guest's address is checked with USPS (`UspsAddressVerifier`,
USPS Addresses 3.0). The form shows a confirmation, the address as USPS writes it with **Use this address**, or
a warning (not recognised, apartment or unit needed). It only advises: a card always saves, and the verdict is
kept on the card and shown on the print screen. It needs a free USPS developer app. The System Administrator
enters its Consumer Key and Secret under **Connected Systems, USPS account for address checking**
(`UspsAccountStore`): they are encrypted before they are stored, never shown again, and **Save and test**
signs in to USPS and checks a known address. The `Usps__ClientId` / `Usps__ClientSecret` app settings still
work as a fallback when nothing is entered in the site. The audit log records, per card, whether the person used USPS's version of the address
(`AddressUspsVersionUsed`, with what was first typed) or kept their own (`AddressKeptAsTyped`, with the USPS
version passed over), including when a card is saved without choosing. Answers are remembered for a day and checks pause when USPS says it is busy, to stay within
USPS's request allowance. USPS allows this only to help send mail, which is what it is used for.

PostGrid can check addresses too (`PostGridAddressVerifier`, PostGrid Address Verification, which uses USPS
data), as a backup for USPS or instead of it. The System Administrator enters PostGrid's *Address
Verification* API key under **Connected Systems, PostGrid, Checking guest addresses** (`PostGridAddressAccountStore`,
encrypted the same way) and picks **USPS first** (PostGrid answers only when USPS is not set up or cannot answer)
or **PostGrid first**. `AddressCheckers` asks them in that order and moves to the next whenever one cannot
answer. PostGrid charges per lookup after its free allowance, billed to the account whose key is entered; the
`PostGrid__AddressApiKey` app setting is a fallback when nothing is entered in the site.

**How the cards go out.** The print screen offers three choices every time:

- **Stamps by hand:** nothing is charged; the card prints with the guest's address and a stamp box.
- **Buy and print postage:** USPS First-Class postage is bought per card through EasyPost
  (`EasyPostProvider`) and its mailing label prints as the card's address side. Each investment group
  pays from **its own EasyPost account**: a group administrator pastes the group's API key under
  **Settings, Postage account**. The key is encrypted with ASP.NET Core Data Protection before it is
  stored (`GroupPostageAccounts`; the encryption keys live beside the database in `keys/`), is never sent
  back to a browser or written to any log, and is shown only by its last four characters. The optional
  `Postage__EasyPostApiKey` app setting is a shared site account used only for groups without their own.
  Buying also needs the group's "allow postage" setting and the `BuyPostage` permission. A card is never charged twice, nothing is bought above `Postage__MaxCentsPerCard` (150), and
  each purchase is audited. An EasyPost *test* key buys sample labels at no cost. USPS itself has no
  service for printing postage on postcards; its label service is for packages.
- **Mailed for you:** nothing is printed in the restaurant. The site sends the card to a mail house (PostGrid,
  `PostGridMailer`, Print & Mail API), which prints both sides of a 6 × 4 postcard on card stock, adds
  First-Class postage and mails it, usually the next business day. It runs on **the site's own PostGrid
  account**: the System Administrator enters the Print & Mail key once under **Connected Systems, PostGrid**
  (`SiteMailing`, encrypted, shown only by its last four characters, checked with PostGrid before it is saved;
  `PostGrid__MailApiKey` is a fallback). PostGrid bills the site, and the site bills each investment group (see
  *Billing for mailed cards*). **Groups never see PostGrid's name:** every screen, message, history entry,
  export column and audit row they can read says "mailed for you" or "the mailing service"
  (`MailStatuses.ForClients`), and the printed proof is fetched by the site and handed on
  (`comments/{id}/mail/proof`) instead of linking to PostGrid.
  **Preview and mail** shows both sides exactly as sent, then a confirmation naming the number of cards and the
  group's exact price. Sending needs the `BuyPostage` and `MarkResponded` permissions, at most
  `PostGrid__MaxCardsPerMailing` (100) cards at a time; the confirmed count and the confirmed price must both
  match; the group must be under its monthly limit; and, when the System Administrator requires it, the group
  must have a payment method saved (otherwise it prints its own cards). A card is never sent twice: cards already
  with the mail house are skipped and each request carries an idempotency key, so a retry returns the first
  order. Each card is marked sent, gets a history entry and an audit row (`CardMailed`, `CardMailFailed`,
  `CardMailOverLimit`). **Cancel mailing** works until printing starts (`CardMailCancelled`), puts the card back
  with the cards not sent, and the card is not billed. A PostGrid *test* key (`test_…`) makes proofs only:
  nothing is printed, mailed, charged or billed, and test orders are labelled as such everywhere.

**Card design.** The **Card design** page (group administrators and owners) sets how mailed cards look
(`CardDesign`, `CardImage`, `CardDesignController`). The investment group has one card for every restaurant; a
restaurant can have **its own** instead, changed by its owners or the group's administrators, and goes back to
the group's with one button. The front is one of: **plain** (the restaurant's name on the group's colour),
**designed here** (three layouts, a headline, a message, background and text colours, a logo and a photo), or
**your own artwork** (a finished picture, ideally 1875 × 1275 pixels). A restaurant's own design borrows any
picture it has not uploaded (typically the logo) from the group's. The back is the coupon, laid out to leave
the address and postage area clear, with an optional short note from the restaurant above it. Both sides are
drawn by the server from the same HTML the mail house receives (`PostGridCardDesign`), so the live preview on
the page is exactly what prints. Pictures are PNG or JPEG up to 5 MB, stored in the database and served at
`/card-art/{group}/{hash}` (public, because the mail house downloads them; the address cannot be guessed).

**Billing for mailed cards.** The site pays the mail house first, so every mailed card is recorded with what it
cost the site and the site's fee at the moment it was sent (`MailEstimateCents`, `MailFeeCents`); later price
changes never rewrite history. The System Administrator's **Billing** page (`SystemBillingController`,
`/api/system/billing`) sets:

- what a card costs the site and **the site's fee per card**, for every group and optionally **per group**;
- a **monthly limit** per group (site default and per group; $0 switches mailing off), which caps what any
  group can owe;
- the **bill day** (last month's bills are created on it), days to pay, whether bills are **emailed** and whether
  a **printed statement** is also posted through the mail house (PostGrid letters, paid by the site);
- the **credit card fee** (3% by default), the **bank account fee** (0.08% by default) and whether a **payment
  method is required** before a group may mail;
- who the bills are from (business name, address, how to pay).

`BillingWorker` runs hourly: on or after the bill day it creates one bill per group for the finished month
(`MailInvoice`, unique per group and month, frozen with a line per restaurant), sends it, charges the groups that
chose automatic payment, and checks bank payments that were on their way. Bills can also be created at once for
any finished month. The System Administrator sees every bill (filtered by group, status and month), what is owed
and overdue, **marks a bill paid** (check, bank transfer, cash; with a note and date), reopens or cancels one, and
sends one again. A month-by-month statement splits the mail house's cost from the site's fee by group and
restaurant. Overdue bills, bills that could not be sent, failed payments and groups near their limit appear in the
Overview's "Needs your attention".

Investment groups have their own **Billing** page (`BillingController`, `/api/billing`): the price per card, this
month's use against the limit, each month by restaurant, their bills, where bills go, and their payment method.
They see one price per card, never the split between cost and fee. Owners see the use of their own restaurants;
only the group's administrators see bills, save a payment method or pay.

**Paying on the site** uses the site's own **Stripe** account (`StripePayments`, secret key entered under
**Connected Systems, Stripe**, encrypted; `Payments__SecretKey` is a fallback). A group administrator saves a
bank account or a card on Stripe's own page (Checkout in setup mode), so no card or bank number ever reaches the
site; the site keeps only Stripe's references and a label such as "Visa credit card ending 4242". A bill is
paid with that saved method: the confirmation names the exact amount, the confirmed amount must match, and the
payment carries an idempotency key made of the bill and its attempt number, so a bill is never charged twice.
**Credit cards add the card fee and connected bank accounts add the bank fee (to the nearest cent); debit
cards add nothing.** Bank payments show as "on their
way" until Stripe reports them settled. A group can switch on **automatic payment** of each new bill. A Stripe
*test* key (`sk_test_…`) moves no money. Every step is audited (`MailBillCreated`, `MailBillEmailed`,
`MailBillPosted`, `MailBillCharged`, `MailBillPaymentFailed`, `MailBillMarkedPaid`, `PaymentMethodSaved`, …).

**Following up.** Once a card has gone out by mail, the guest is due a follow-up to check it arrived:
5 days later by default (`FollowUpDays` in group settings), or the number its restaurant chooses. Owners set
their restaurants' days; managers can only with the `SetFollowUpDays` permission; 0 turns it off. Managers
and owners see a notice at the top of every page while anyone is due, with **Review and call**. Recording
the follow-up keeps notes about the conversation in the card's history; "no answer" is kept as an attempt
and leaves it due. No follow-up is asked for once the coupon is redeemed.

**Redeeming.** **Coupon redeemed** records the day the guest used the coupon (and can be undone with a
reason). Analytics, the export and the System Administrator's reports show cards printed, guests followed
up with, coupons redeemed, postage bought, and cards mailed with their cost and the site's fee.

## System administration

A System Administrator oversees the whole site, not restaurants, and has a menu of their own (dark sidebar):

| Page | What it is for |
| --- | --- |
| **Overview** (home) | "Needs your attention" (server errors, bursts of failed sign-ins, groups with no administrator or restaurants, cards waiting over a week, people who never signed in, email not set up), headline numbers for the whole site, every investment group at a glance and the latest sign-ins |
| **Investment groups** | create and rename groups; **View as admin** |
| **Restaurants** | directory of every restaurant in every group: owners, staff counts, cards, waiting; **Open in group** |
| **People** | everyone with access: group, restaurants, account state, last sign-in, last seen; reset password, suspend/restore, add and edit System Administrators; **Open in group** to change roles or restaurants |
| **Reports**, **Activity log**, **Audit log** | see below |
| **Billing** | what mailed cards cost and the site's fee (for every group and per group), monthly limits, when and how bills go out, every bill and whether it is paid, and what was mailed month by month |
| **Connected Systems** | the accounts for outside services the whole site uses, each with Save and test: USPS and PostGrid's address verification key (and which is asked first), PostGrid's Print & Mail key (mails every group's cards), and Stripe (takes payment for mailing bills); EasyPost and email are summarised with where each is managed |
| **Security** | sign-ins, failed sign-ins by username, refused access, requests from out-of-date copies, accounts to look at, the System Administrators |
| **System status** | version, uptime, database size and row counts, settings storage, email service (with a test email), 24-hour request and error figures, recent server errors with what went wrong, slowest requests |

Restaurant-level screens (Enter card, Comments, Analytics, Users, Restaurants & staff, Settings) never appear
for a System Administrator unless they choose **View as admin** / **Open in group**, which shows one group as
its Investment Group Administrator sees it under a yellow bar. That choice lasts for the browser tab only.
All of `/api/system/*` is `SuperAdmin` only.

## Interaction log and System Administrator reports

Every user interaction is logged and tagged with the person, their investment group and (where known) the
restaurant:

- **Server requests:** every `/api` call, with method, path, result, duration and the audited action it
  performed (`InteractionLoggingMiddleware`). Request and response bodies are never stored.
- **Browser events:** pages opened, buttons and links clicked, and choices made in drop-downs, checkboxes
  and radio buttons (`web/src/api/tracker.ts`, sent in batches to `POST /api/activity`). What people type
  into fields is never recorded.
- **Sign-ins:** both successful and failed, with the account they were for.

Rows are written in the background, so logging never slows anyone down. They are kept for
`InteractionLog__RetentionDays` (730 by default). A System Administrator viewing a group as its admin is
tagged to that group and marked "viewing as".

**Only System Administrators can see the log.** On **Reports** they get, for any date range, any set of
investment groups and any set of restaurants:

- headline figures;
- a day-by-day chart per group (active people, interactions or cards entered);
- tables by group, by restaurant and of the most active people, each sortable and downloadable as CSV.

**Activity log** filters the raw log by group, restaurant, person, kind, date and text, and can follow one
browser visit end to end. It also downloads as CSV.

New endpoints and screens are logged automatically. Keep it that way: don't bypass the middleware, and don't
put typed values into logged labels or paths.

## Caching

The site always loads its live version:

- `index.html`, every page route and every API response are sent with `Cache-Control: no-store`.
- The fingerprinted files under `/assets` are cached for a year; their names change with every build.
- Each API response carries `X-App-Version`, the build's index hash. A tab left open across a deploy
  reloads itself at the next page change.
- Every current copy of the site sends `X-Session-Id` with each API call. A caller without it is a stale
  cached copy from before pages were `no-store`: it gets `426` with a "reload this page" message and
  `Clear-Site-Data: "cache"`, so nobody keeps working in out-of-date screens. Scripts calling the API must
  send the header too.

The investment group's name is stored once, on the group (renamed by a System Administrator on Investment
groups); settings and the top bar echo it.

## Run locally

```bash
cd src/RestaurantFeedback.Api && dotnet run        # http://localhost:5298, SQLite in App_Data
cd web && npm install && npm run dev               # http://localhost:5173, proxies /api and /card-art
```

Set `PostGrid__Sample=true`, `Payments__Sample=true` and `Postage__Sample=true` to use stand-ins for PostGrid
mailing, Stripe and EasyPost that never contact the real service.

Development seeds an owner `owner` / `LocalOwner!2026` with the "Culver's of Joliet" restaurant.
Put a super admin and any other local-only values in `src/RestaurantFeedback.Api/appsettings.Local.json`
(git-ignored):

```json
{ "Seed": { "SuperAdminUsername": "you@example.com", "SuperAdminPassword": "..." } }
```

Tests: `dotnet test` (unit tests for guest matching plus end-to-end API tests on a throw-away SQLite file).

## Deploy

- `deploy/provision.sh` creates the Azure resources (idempotent) and prints the app settings to configure.
- `deploy/deploy.sh` builds the front end, publishes the API and zip-deploys it.
- `.github/workflows/deploy.yml` does the same on every push to `main`. It signs in to Azure with
  OpenID Connect (no stored passwords) as the `gh-restaurants-deploy` app registration, which trusts only
  this repo's `main` branch and holds Website Contributor on the web app alone. Repository secrets:
  `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.

Configuration keys (App Service settings use `__` as the separator): `ConnectionStrings__Default`,
`Jwt__Key`, `Storage__AccountUrl` (managed identity) or `Storage__ConnectionString`,
`Email__ConnectionString` + `Email__Sender` (Azure Communication Services; the sender is the
`DoNotReply@<id>.azurecomm.net` address of the Azure-managed domain), and the `Seed__*` values for the
first super admin, group, restaurant and owner.

Azure resources: `rg-restaurant-feedback` holds the F1 plan + web app, the storage account, and the
Communication Services + Email Communication Service resources. Everything except email is on a free
tier; email is pay-per-message (fractions of a cent).

Free-tier notes: F1 sleeps after 20 minutes idle, so the first request can take 30 to 60 seconds, and it
has a 60 CPU-minutes/day quota. The SQLite file lives on the persistent `/home/data` share. To grow, move
the plan to B1 and swap SQLite for Azure SQL (change the EF provider and regenerate the migration).
