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
| **System Administrator** (`SuperAdmin`) | every investment group | oversees the groups: creates them and their administrators, sees every user and the whole audit log. **Impersonate** opens one group exactly as its Investment Group Administrator sees it, under a dark blue bar with **Back to System Administration** (the API takes the group from the `X-Group-Id` header). Not assigned to any restaurant |
| **Investment Group Administrator** (`GroupAdmin`) | one investment group | everything: every restaurant, every user account (owners and other administrators included), the group settings, and what owners and managers may do. Can also be ticked as an owner of particular restaurants (two roles at once) |
| **Restaurant Owner** (`Owner`) | the restaurants they own | what the levels above leave to owners, by default everything at those restaurants: cards, analytics, export, audit, staffing and co-owners, the restaurant's details and card design. They edit, reset, suspend and restore the managers and team logins who work there, even ones who also work at other owners' restaurants |
| **Manager** (`Manager`) | one or more restaurants, across owners | enter cards plus what all managers in the group were given and what was ticked for them: ViewComments, EditComments, DeleteComments, MarkResponded, ViewAnalytics, ExportComments, ViewAudit, ManageTeamLogin, BuyPostage, SetFollowUpDays. Permissions apply at every restaurant they work at |
| **Team Member** (`TeamMember`) | the shared login for a restaurant | enter cards only (duplicate checks return counts, never details) |

### Sent is not replied, and the day a card is in the mail

Three separate things happen to a guest's comment, and each is recorded by itself (`GuestComment`):

- **Replied** (`Responded`, `RepliedOn`, `ReplyMethod`): a person recorded that they replied to the guest (a call,
  an email, a word in person). Nothing else sets it: mailing or printing a card never marks the guest as replied to.
- **Sent** (`SentAt`, `SentByName`): the coupon card was handed to the mailing service, or printed in the
  restaurant and marked as sent. It is the card's latest send that went (every attempt is in `CardSend`); a send
  cancelled before printing puts the card back to its earlier send, or to "not sent". The follow-up call ("did
  the card arrive?") counts from this day (`CardFollowUps.SentDay`).
- **In the mail** (`InMailAt`, on the card and on each `CardSend`): for a card mailed for the restaurant, when the
  mailing service handed it to the post office (its status reaches `processed_for_delivery`). The mailing service
  keeps no time for that, so the site notes when it last changed the order, or when the site first saw the
  status (it asks every half hour and whenever Follow Up opens). Noted once and never moved; never for a test order.

The migration `SentIsNotReplied` carried over what had already been done: every card that had gone became "sent"
by its latest send, cards the site itself had marked as replied went back to waiting for a person's reply, and the
site's own "reply confirmed" history entries were kept as "card sent" entries. Replies a person recorded were
left as they were.

### Follow Up

Under **Actions** inside a group, **Follow Up** (`/follow-up`, `FollowUpPage`) is the work list for anyone who may
see guest comments (`ViewComments`): the guests still waiting for a reply, and the follow-up calls that are due
(`GET /api/comments?status=open`). Each row shows what is left to do, whether and when the guest was replied to,
when the card was sent and how, the day it went into the mail, the follow-up call and whether the coupon was used.
**Reply** / **Open** opens the card to record a reply, an attempt or the follow-up call; **History** opens the
card's sends. "Every card, by date" turns the page into a report of all cards created in a range of dates; every
column sorts, and the CSV download is offered only with `ExportComments`. It replaced the Sent Postcards report
(`/sent-postcards` now leads here).

The menu shows a red count beside **Print Cards** (cards not sent yet) and **Follow Up** (guests waiting for a
reply or a follow-up call), for people who can act on them (`MarkResponded`). Both come from
`GET /api/follow-ups/summary` and are counted again on every page change and whenever something is recorded.

### A removed comment stays readable in the audit log

Removing a comment hides it (`IsDeleted`), and its `CommentDeleted` audit row holds everything the card held: the
guest and address, the visit, the coupon, the reply, the send and its day in the mail, printing, postage, the
mailing reference and status, redemption, the follow-up, its whole follow-up history and every attempt to send
it. As everywhere a group can read, a mailed card shows the one price paid and never names the mail house.

### Sending a card again, and the history of sends

A card can be sent more than once (it never arrived, or its address was wrong). Every attempt is kept in
`CardSend` (`SendHistory`): when, how (mailed for the restaurant, or printed there), by whom, the address that
attempt used, what came of it (sent, not taken by the mailing service, cancelled) and, for a mailed attempt, the
mailing service's latest status and what was billed. The card itself carries only its latest send. Rows are only
added (a mailed row's status is kept up to date), so the address each attempt used stays on record however the
card's address is changed later. The migration that added the table carried over what had already been done: the
send each card carried, cards printed and marked sent, and refusals from the audit log.

- **History · Send again** on Follow Up opens a card's attempts (`GET /api/comments/{id}/sends`) and lets the
  address be corrected, then goes to Mail Cards or Print Cards in "again" mode (`resend`), where the cost is shown
  and confirmed as for any card. A mailed card is charged again; a reprint here is not (the hand-print fee is
  once per card). A mailed card still waiting to be printed is cancelled and corrected instead of sent twice.
- **Billing** counts the earlier sends of a card that was sent again (`MailLedger.BilledEarlierSends`), so every
  card that really went is on the bill, and a cancelled resend is not.
- **Mailing statuses keep themselves up to date** (`MailStatusRefresher`): a background worker asks the mailing
  service every half hour about the cards it has not finished with, and Follow Up asks when it opens
  (`POST /api/comments/mail/refresh`), so "waiting to print" is never left standing.

### Needs your attention: dismissing, and what each item is about

On the System Administrator's Overview, each "Needs your attention" item can be **dismissed** (`AttentionPanel`,
`POST /api/system/attention/dismiss` and `/restore`, table `AttentionDismissal`). A dismissal is that System
Administrator's own and holds only while the item reads exactly the same: when its count or detail changes, or it
goes away and comes back, it shows again (`Attention.Key` says which item it is whatever its numbers;
`Attention.Fingerprint` what it says now). "Show dismissed" lists the dismissed ones with **Bring back**. A switch
shows **All**, **System Only** (the site itself: backups, errors, keys, email, security) or **Restaurants Only**
(investment groups: their restaurants, people, cards and bills); which an item is comes from where it leads
(`Attention.Scope`). The switch is remembered in the browser.

### The AI assistant on Enter Card

Where it is switched on, Enter Card starts with a box: the person types what happened in their own words, ticks
"Also draft an email" if one is wanted, and presses **Process**. The assistant (`IAiDrafter`, `ClaudeDrafter`:
one Messages API request to Claude with a JSON schema, no tools) reads it and fills in the card for them to check:
the guest, the visit, the reason (only ever one from the group's own list), a short account for managers, and how
upset the guest was as one of five faces (`AiMoods`, 🙂 to 😡). Each face stands for one of the group's rewards
(`GroupSettings.AiRewards`, else its offers spread in order), which goes on the card; picking another face changes
the reward. What was typed is treated as material to read, never as instructions. Nothing is saved until the card
is. The card also has an optional email section (the guest's address, a subject and the email), kept with the
card; sending it from the site is not built yet.

- **Off by default, handed down like every other setting** (`AiPolicy`, `GroupBilling.AiEnabled`,
  `GroupChoice.Ai`). The System Administrator sets each group, in its Edit dialog (`PUT /api/system/groups/{id}/ai`):
  off, on for the group, or "the group decides", plus the highest level the group may choose
  (`GroupBilling.AiMaxLevel`; levels above it are shown to the group as not offered, and lowering it brings the
  level in use down with it) and the most it may be charged in a month. Where the group
  decides, its administrators, and owners who hold `ManageAiAssistant`, switch it on or off on their Billing page
  (`AiGroupPanel`, `PUT /api/ai/settings`), where they also choose the level and the reward for each face. Where it
  is off, the section is simply not on the card form (`GET /api/ai/config`).
- **Levels and prices** (`AiLevels`): Quick (0.1¢ a request), Balanced (1.8¢) and Most thorough (3¢), which run on
  Claude Haiku 5.5, Sonnet 5.5 and Opus 5.5. Groups see the labels and prices and "the assistant", never the
  company or the model; those names are on Connected Systems and Reports only. Prices are kept in tenths of a
  cent ("mills"); a month's requests go onto the bill as a line of their own for each restaurant, in whole cents.
- **Money rules.** The price is on the button; a group cannot be charged past its monthly limit; each press
  carries its own key, so a press that arrives twice is answered and charged once; a try that fails is recorded
  and not charged. Anyone who can enter a card where it is on can use it, as with the hand-print fee.
- **Every press is kept** (`AiRequest`, `AiLedger`): who, when, where, the level, what it was charged, what was
  typed, what came back, the tokens used and what it cost the site, and the card saved from it (the card form
  sends the keys of its requests when it saves). A request that never became a card stays on record as "no card
  saved". The group sees its requests on Billing; the System Administrator sees them all on **Reports, AI
  assistant** (`GET /api/system/ai/usage`), by group, restaurant, level and day, with what each cost.
- **The site's key** is entered on Connected Systems (`AiAccountStore`, encrypted, never shown again;
  `Ai__ApiKey` is a fallback). What was typed is never written to the audit or interaction logs.

### Who decides what: handed down level by level

Permissions and settings are decided from the top down, and a decision made higher up takes precedence
(`PermissionPolicy`, `GroupPolicy`):

1. **The System Administrator**, for each investment group (Investment groups, **Edit**). For every permission:
   *all owners can*, *no owner can* or *the group decides*, and the same for managers. For each group setting
   (having cards mailed, real or test mailing, how cards usually go out, the name that signs for the team login):
   the System Administrator's answer for the group, or a tick to let the group's own administrators change it.
2. **Investment Group Administrators** can do everything in their group. On **Permissions** they decide what is
   left to them: whether owners can, and for managers *all can*, *none can* or *set for each manager*. On
   **Settings** they make each setting left to them one answer for all restaurants, or leave it to each restaurant.
3. **Restaurant Owners** hold what is left to them. The owner-only permissions are `ManageStaff`,
   `SetPermissions` (choose what each manager may do), `EditRestaurants` and `DesignCards`. An owner makes a
   restaurant's own choices where "each restaurant decides", and ticks the manager-by-manager permissions on
   All Staff when owners hold `SetPermissions` (the group's administrators always can).
4. **Managers** hold what all managers were given plus what was ticked for them, and never a permission the
   group's owners do not hold. **Team logins** only enter cards.

Inside a group the menu has four parts: **Actions** (Enter Card, Print Cards, Follow Up), **Reports** (All
Comments, Billing, Analytics, Audit Log), **Staff** (Restaurants & Staff, All Staff) and, at the bottom,
**Configuration** (Permissions, Settings, Card Design). Each person sees only the entries they may use. Every
word of a tab's name is capitalized, in both menus; the section titles are in the site's blue.

Every change of these rules is audited (`PermissionRulesSetBySite`, `PermissionRulesChanged`,
`GroupSettingsChanged`). On **All Staff**, **Restaurants** on a person's row maps them to restaurants in one step
(`PUT /api/users/{id}/restaurants`): where a manager or team login works, or which restaurants an owner or an
administrator owns.

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
2. **Impersonate** opens the group; its **Restaurants & Staff** page has each restaurant's **Staff** screen, which lists the
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

Three services can check addresses, and the System Administrator chooses the one that does, on **Connected
Systems, Address Verification** (`AddressVerificationPanel`, `AddressServiceChoice`,
`GET/PUT /api/system/address-checking`). It is one section: a drop-down picks the service, the section shows
that service's key (saved, with its last four characters, or boxes to enter it), and **Save** stores what was
entered and makes the service picked the one that checks every guest address. Nothing changes until Save, and
only a service with a key can be saved as the one in use:

- **Geocodio** (`GeocodioAddressVerifier`, `GET /geocode` with `fields=zip4`): finds the address at its house
  number and how it is properly written, and from the post office's ZIP+4 data says whether mail is delivered
  there and whether a unit number is missing or not known. An address placed only at a street, a town or the
  nearest other building counts as not found. Each check uses two lookups (the address and the ZIP+4 data);
  answers are remembered for a day. The key is entered under **Connected Systems, Geocodio**
  (`GeocodioAccountStore`, encrypted, never shown again; `Geocodio__ApiKey` is a fallback) and travels in a
  header, never in the address of the request. Geocodio does not offer addresses as they are typed.
- **USPS**, as above.
- **PostGrid** (`PostGridAddressVerifier`, PostGrid Address Verification, which uses USPS data), with its *Address
  Verification* API key (`PostGridAddressAccountStore`, encrypted the same way; `PostGrid__AddressApiKey` is a
  fallback). It charges per lookup after its free allowance. This key also offers addresses as they are typed
  (below) whichever service checks them, so it can be saved with **Save key only**, which leaves the service in
  use as it is.

`AddressCheckers` asks the chosen service and no other, whether or not the others have keys saved: when it cannot
answer (busy, down, out of lookups) the card is saved without a check, as it always could be. If the chosen
service's key is removed, addresses are not checked until another service is saved, and the Overview says so.
Until a choice has ever been made, the service that checked before carries on (USPS, else PostGrid, else
Geocodio). Nothing said to a person entering a card names any of these companies.

**Addresses offered as the street is typed.** With that key entered, the street address box on Enter Card offers
addresses that begin the way the street has been typed, for restaurants that have their cards printed and mailed
for them (`POST /api/addresses/suggest`, `IAddressSuggester`, `AddressSuggestInput`). Picking one fills in the
street, city, state and zip, and the address is then checked as usual. It uses PostGrid's autocomplete previews
(`GET /v1/addver/completions`), which use no lookup, so offering addresses costs nothing; the paid completion
call is not used. Addresses in the restaurant's state come first. What is typed travels in the request body, the
list is marked `data-no-track`, outgoing requests are not logged, and only "Picked a suggested address" is
recorded. Groups never see PostGrid's name here: the form just offers addresses, or does not. When the key has
not been entered, the System Administrator is told on the Overview ("Needs your attention"), never on a screen
inside a group, where an owner or manager (or the System Administrator while impersonating) would read it.

**Correcting an address the mailing service refuses.** On Mail Cards, a card that was not taken because of its
address says so with a **Correct the address** button, and every card not yet sent has **Correct address** (or
**Add address**). The dialog has the same address suggestions and USPS check as Enter Card, changes only the
address (`PUT /api/comments/{id}/address`, for anyone who may send cards; audited as `CardAddressCorrected`),
and puts the card back among the cards to send. A card already with the mailing service is cancelled first.

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
  match; **mailing must be switched on for the group** (the System Administrator does this per group on Billing,
  `GroupBilling.MailEnabled`; it is off until then); the group must be under its monthly limit; and, when the
  System Administrator requires it, the group must have a payment method saved (otherwise it prints its own
  cards). When any of these is missing, the choice on Print cards stays visible but greyed out and says what it
  takes (`PrintConfig.MailBlockedBy`: the site, the group's switch, the limit, a payment method or the
  permission). A restaurant can make "Mailed for you" **its usual way** (Restaurants & Staff,
  `Restaurant.MailByDefault`): Print cards then starts on it when every card chosen is from such a restaurant. A card is never sent twice: cards already
  with the mail house are skipped and each request carries an idempotency key, so a retry returns the first
  order. Each card is marked sent, gets a history entry and an audit row (`CardMailed`, `CardMailFailed`,
  `CardMailOverLimit`). **Cancel mailing** works until printing starts (`CardMailCancelled`), puts the card back
  with the cards not sent, and the card is not billed. A PostGrid *test* key (`test_…`) makes proofs only:
  nothing is printed, mailed, charged or billed, and test orders are labelled as such everywhere.

**Card design.** The **Card design** page (group administrators and owners) sets how mailed cards look
(`CardDesign`, `CardImage`, `CardDesignController`). The investment group has one card for every restaurant; a
restaurant can have **its own** instead, changed by its owners or the group's administrators, and goes back to
the group's with one button. A restaurant's own design borrows any picture it has not uploaded (typically the
logo) from the group's.

- **The front** is one of: **plain** (the restaurant's name on the group's colour), **designed here** (three
  layouts, a headline, a message, background and text colours, a logo and a photo), **your own artwork** (a
  finished picture, ideally 1875 × 1275 pixels), or **laid out yourself** in the card designer.
- **The back** is the coupon: **classic** (the default, laid out to match the restaurants' own printed coupon
  cards: logo and the restaurant's name and address across the top, "Please enjoy", "Date Issued" and
  "Authorized Signature" on fill-in lines, the small print, and a web address with a small picture such as social
  media icons in the bottom corner, all in one ink colour), **simple** (a plain coupon box), or **laid out
  yourself**, which starts as the classic card.

**The card designer** (`web/src/components/CardLayoutEditor.tsx`, `Services/CardLayout.cs`). A side laid out
yourself is a list of **items** (`CardElement`, stored as JSON in `CardDesign.BackLayoutJson` /
`FrontLayoutJson`): words, fill-in lines (a label and what is printed on the line), rules, boxes, pictures and
the QR code. Each has a place and size in inches on the finished 6 × 4 inch card, and words have a typeface,
size, weight, alignment and colour. In the browser every item can be dragged, resized, nudged with the arrow
keys, copied, re-ordered, removed and undone, and new ones added, so a new restaurant's card is set up on the
site without any programming. Words can carry **fields** in braces, filled in for each card: `{restaurant}`,
`{restaurantState}`, `{street}`, `{cityStateZip}`, `{website}`, `{offer}`, `{dateIssued}`, `{goodUntil}`,
`{months}`, `{authorizedBy}`, `{guest}`, `{legal}` and `{note}`. That is how one group design prints each
restaurant's own name and address. A line made only of fields that come out empty is left off. The classic back
is simply the standard list of items (`CardElements.ClassicBack`), positioned from a photograph of the
restaurants' printed card, and is drawn by the same code (`CardElements.Render`).

- Four typefaces are built into the site (`src/RestaurantFeedback.Api/CardFonts`, SIL Open Font License, licences
  beside them): Arimo, Montserrat, Roboto Condensed and Great Vibes (a flowing signature hand). What is filled in
  on a fill-in line can have a typeface and size of its own, which is how the classic card prints the Authorized
  Signature in the signature hand.
- **Who signs a card** (`CardSignature`). A manager, owner or group administrator who enters a card signs it with
  their own name: the "Authorized signature" box on Enter card starts with it and can be changed. The team login
  is shared by the crew, so its cards take the name set for the restaurant (`Restaurant.SignatureName`). Owners
  and administrators set that on Restaurants & Staff; the restaurant's managers set it from Enter card
  (`PUT /api/restaurants/{id}/signature`, audited as `RestaurantSignatureChanged`); a team login cannot. A card
  saved with no name gets the same default on the server. The mailed card prints the name on the card, or the
  restaurant's name for older cards that carry none. The printed card carries the ones it uses inside its own
  page, so the print matches the preview and the mail house downloads nothing; the designer loads the same files
  from `/card-fonts/…`.
- Everything from the designer is checked on the server (`CardElements.Clean`): at most 40 items, known kinds
  only, sizes kept on the card, colours and typefaces from the allowed set. Every word is escaped when the card
  is drawn, so nothing typed in the designer can become markup.
- Pictures placed in the designer are uploaded like the others (kind `el-…`, up to 12 per card) and removed once
  no card places them.
- PostGrid prints the guest's address and the postage on the right 2.4 inches of the back and a barcode along
  the bottom right. The designer and the preview shade those areas and warn when an item reaches into them or
  sits within ⅛ inch of the edge.

Both sides are drawn by the server from the same HTML the mail house receives (`PostGridCardDesign`), so the
live preview on the page is exactly what prints. Pictures are PNG or JPEG up to 5 MB, stored in the database and
served at `/card-art/{group}/{hash}` (public, because the mail house downloads them; the address cannot be
guessed).

**A QR code on every card.** Each card has its own random GUID (`GuestComment.PublicId`, unique across every
investment group, shown as the Card ID on the card's record). Every printout carries a QR code for it: the cards
printed in the restaurant (bottom left on blank stock; a positioned blank, on by default, on pre-printed stock) and
the mailed cards (beside the date and signature lines). The code holds only `…/v/{code}`, where the code is the
GUID followed by a signature: the first ten bytes of an HMAC-SHA256 of the GUID, keyed with a secret only the
site holds (`CardCodes`; the secret is made once and kept in `SiteSettings`, encrypted with Data Protection). A
code therefore cannot be made up, guessed or altered, and it shows nothing by itself. Scanning it opens **Check a
coupon card**, which makes the person sign in first and then returns to the card. Only people who work at the
card's restaurant see it, as the site has it (guest, offer, dates, whether it was redeemed), so a card whose
printed words were changed is caught by comparing it with the screen, and a used card or a photocopy shows as
already redeemed. Anyone with the mark-as-sent permission can mark it redeemed from there. Every check, and every
rejected code, is audited (`CardCodeChecked`, `CardCodeRejected`). If the signing secret were ever lost, cards
printed before then would stop checking as genuine.

**Billing for mailed cards.** The site pays the mail house first, so every mailed card is recorded with what it
cost the site and the site's fee at the moment it was sent (`MailEstimateCents`, `MailFeeCents`); later price
changes never rewrite history. The System Administrator's **Billing** page (`SystemBillingController`,
`/api/system/billing`) sets:

- what a card costs the site and **the site's fee per card**, for every group and optionally **per group**;
- **the fee for cards a group prints itself for a stamp by hand** (`BillingSettings.ChargeHandPrintFee`, on by
  default): the group's fee per card, without the mail house's cost, charged once per card at its first print
  (`GuestComment.PrintFeeCents`, audited as `CardPrintFeeCharged`). The print screens say what will be billed
  and ask before printing; reprints, cards printed before the charge existed and cards with bought postage are
  not charged. The fees go on the monthly bill as their own lines and count against the group's credit like
  mailed cards (`MailLedger.ChargesAsync`), but not against its monthly limit, so a group can always print;
- **whether mailing is switched on** for each group (off until the System Administrator switches it on;
  audited as `GroupMailingSwitchedOn` / `GroupMailingSwitchedOff`);
- **whether a group needs a payment method before mailing**: the site's setting (required by default) applies
  unless the group is given its own rule, required or not required (`GroupBilling.RequirePayMethod`, audited as
  `GroupPaymentRuleChanged`);
- **credit** for a group (`GroupCredit`, `POST /api/system/billing/groups/{id}/credits`): a check that arrived, a
  correction, a welcome bonus. Each credit needs an amount (up to $10,000), a reason and details, is confirmed
  before it is recorded, and carries a key so a repeated request cannot record it twice. Credit comes off the
  group's bills by itself, oldest first, as soon as there is a bill waiting (`BillingService.ApplyCreditAsync`,
  `MailInvoice.CreditCents`); a bill the credit covers in full is marked paid with credit, and only what is
  still owed is ever charged to a card or bank account. Cancelling a bill returns the credit used on it. Unused
  credit can be taken back with a reason. The **credit log** on Billing lists every line (given, used,
  returned, taken back) with who and why, filters by investment group, and nothing in it can be edited or
  removed. The group's administrators see their credit and where it went on their own Billing page, without
  the System Administrator's details. **Credit also stands in for a payment method:** a group that needs one
  and has none can still have cards mailed, paid from its credit, but only as many as the credit covers
  (`GroupTerms.OnCredit`, `MailLedger.SpendableCreditAsync`). Cards mailed since the last bill count against
  the credit straight away, so it cannot be spent twice; a request for more cards than it covers is refused
  (`CardMailOverCredit`) and Print cards says how many it covers;
- a **monthly limit** per group (site default and per group; $0 switches mailing off), which caps what any
  group can owe;
- the **bill day** (last month's bills are created on it), days to pay, whether bills are **emailed** and whether
  a **printed statement** is also posted through the mail house (PostGrid letters, paid by the site);
- the **credit card fee** (3% by default), the **bank account fee** (0.08% by default) and whether a **payment
  method is required** before a group may mail (on by default: no card, not even a test order, is sent to the
  mail house for a group that has not saved one);
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

**Paying on the site** uses the site's own **Stripe** account (`StripePayments`). Under **Connected Systems,
Stripe** the System Administrator enters a **restricted API key** (`rk_…`, with only the permissions the page
lists; a secret key also works but is discouraged) and the **signing secret** of a webhook, both encrypted
(`Payments__SecretKey` and `Payments__WebhookSecret` are fallbacks). Every request is pinned to one Stripe API
version (`Payments__ApiVersion`) and names no payment types beyond an allow-list of bank account and card, so
which of those are offered is controlled in the Stripe Dashboard. A group administrator saves a
bank account or a card on Stripe's own page (Checkout in setup mode), so no card or bank number ever reaches the
site; the site keeps only Stripe's references and a label such as "Visa credit card ending 4242". A bill is
paid with that saved method: the confirmation names the exact amount, the confirmed amount must match, and the
payment carries an idempotency key made of the bill and its attempt number, so a bill is never charged twice.
**Credit cards add the card fee and connected bank accounts add the bank fee (to the nearest cent); debit
cards add nothing.** Bank payments show as "on their
way" until Stripe reports them settled: Stripe calls `/webhooks/stripe` (outside `/api`, signature checked with
HMAC-SHA256 and a five-minute tolerance, `StripeWebhooks`), the site then asks Stripe itself how the payment
stands before marking the bill paid, and the hourly round checks the same thing as a fallback. A group can switch on **automatic payment** of each new bill. A Stripe
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
| **Investment groups** | create and rename groups; **Impersonate** |
| **Restaurants** | directory of every restaurant in every group: owners, staff counts, cards, waiting; **Open in group** |
| **People** | everyone with access: group, restaurants, account state, last sign-in, last seen; reset password, suspend/restore, add and edit System Administrators; **Open in group** to change roles or restaurants |
| **Reports**, **Activity log**, **Audit log** | see below |
| **Billing** | what mailed cards cost and the site's fee (for every group and per group), monthly limits, when and how bills go out, every bill and whether it is paid, and what was mailed month by month |
| **Connected Systems** | the accounts for outside services the whole site uses, grouped under Mailing and Postage Services, Payment, Address Verification and Email: the one service that checks guest addresses (Geocodio, USPS or PostGrid, picked from a drop-down with its key), PostGrid's Print & Mail key (mails every group's cards), and Stripe (takes payment for mailing bills); EasyPost and email are summarised with where each is managed |
| **Security** | sign-ins, failed sign-ins by username, refused access, requests from out-of-date copies, accounts to look at, the System Administrators |
| **System status** | version, uptime, database size and row counts, settings storage, email service (with a test email), 24-hour request and error figures, recent server errors with what went wrong, slowest requests |

Restaurant-level screens (Enter card, Comments, Analytics, All Staff, Permissions, Restaurants & Staff, Settings) never appear
for a System Administrator unless they choose **Impersonate** / **Open in group**, which shows one group as
its Investment Group Administrator sees it under a dark blue bar. That choice lasts for the browser tab only.
All of `/api/system/*` is `SuperAdmin` only.

### What Stripe tells the site (payment notifications)

Stripe calls `POST /webhooks/stripe` about payments, refunds and disputes. Nothing in a notification is trusted
until its signature has been checked against the signing secret saved on Connected Systems. Then:

- **Every notification is kept whole, once** (`StripeEvent`, `PaymentNotifications`): the full payload, plus what
  was read out of it (type, when, the payment, amount, status, Stripe's reason) and the bill and group it is about.
  Stripe sending the same one again is counted on the row already kept. Notifications that fail the signature
  check are not kept.
- **Billing shows them** to the System Administrator (`GET /api/system/billing/notifications`, filter by group):
  a "Payment notifications from Stripe" panel in plain words, each row opening to every data point Stripe sent
  (`…/notifications/{id}`, which is audited as `PaymentNotificationRead`). Each bill's details list what Stripe has
  said about its payment, and a refund or dispute is flagged on the bill and on the Overview's "Needs your attention".
- **Who can read them:** System Administrators only. Payloads carry the payer's name, email and billing address
  (never a card or bank account number, only the last four digits), so they are not written to the application
  log or the audit log; the audit row `PaymentNotificationReceived` says only what kind it was and which bill.
- In Stripe the destination listens for `payment_intent.succeeded`, `.payment_failed`, `.canceled`, `.processing`,
  `charge.refunded`, `charge.dispute.created` and `charge.dispute.closed`. Only the payment ones change a bill
  (after the site asks Stripe itself how the payment stands); the others are kept and shown.

### Mailing keys, real or test, and how a group is billed

- **Two PostGrid keys.** Connected Systems takes a test key and a live key (`postgrid.mail.test`,
  `postgrid.mail.live`, both encrypted). Real mailing uses the live key; test mailing uses the test key, which
  makes a proof and prints, mails and bills nothing.
- **Real or test** is a setting with levels: the System Administrator can fix it for the whole site (Billing),
  decide it for one group or leave it to the group (Edit group); the group fixes it for all its restaurants or
  leaves it to each restaurant. A batch can mix restaurants: each card goes by its own restaurant's rule.
- **On Investment groups**, **Users** shows a group's people in place (map to restaurants, reset password,
  suspend, restore), and
  **Edit** holds the group's settings and permissions. There is no separate "Set up".
- **How a group is billed** is the group's own choice (Billing, "How you are billed", `PUT /api/billing/split`):
  one bill for everything, a bill for each restaurant, or bills for sets of restaurants the group names
  (`Restaurant.BillSet`; the ones in no set share "Other restaurants"). It applies from the next bills made. A
  group's bills for a month are created together, unique per group, month and unit (`MailInvoice.Unit`), so a
  month is never billed twice or in part; credit is used on them in bill-number order.

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
- **The site is stopped while its files are replaced** (both the workflow and `deploy.sh`): stop, deploy, start,
  then wait for `/api/health` to answer, failing the run if it does not within six minutes. The start step runs
  even when the deploy step fails, so the site is never left off. Replacing the files under a running copy once
  left it half-loaded and answering every request with an error although the run was green (2026-11-19). Expect
  about a minute of downtime per deploy.
- **The database is backed up every hour, and at every start** (`Services/DatabaseBackups.cs`, `BackupWorker`,
  `DbSeeder`). A deploy starts the site, and the copy at start is made before the new version runs its
  migrations, so there is always a backup from just before each deploy and each change to the database's
  structure. The System Administrator can also press **Back up now** on System Status. A copy is made with
  SQLite's online backup, so it is consistent while the site is in use, then checked (`PRAGMA integrity_check`),
  compressed (`restaurant-feedback-yyyyMMdd-HHmmss.db.gz`) and kept in two places: the site's own disk
  (`/home/data/backups`) and the storage account's `database-backups` container.
  **What is kept:** every copy for two days (`Backups__KeepHourlyHours`); after that the first copy of each day,
  for three months (`Backups__KeepDailyDays`); after that the first copy of each month, always. The disk keeps
  the two days and a week of daily copies (`Backups__OnSiteDays`).
  System Status lists the copies with Download (SuperAdmin only, asked for and audited: `DatabaseBackedUp`,
  `DatabaseBackupFailed`, `DatabaseBackupDownloaded`). The Overview says so when the last backup failed or is
  more than six hours old.
  **To restore:** stop the site, replace `/home/data/restaurant-feedback.db` with the unzipped copy (remove any
  `-wal` and `-shm` files beside it), start the site. The Data Protection key ring (`/home/data/keys`) is
  deliberately not in a copy; restored onto a new site, the outside services' keys are entered again on
  Connected Systems. Group settings are in Blob Storage already and are not part of the database.
- **The database is checked at every start** (`Data/DatabaseCare.cs`, after migrations): SQLite's own integrity
  check runs, and if the only damage is an index out of step with its table, the indexes are rebuilt (an index is
  made from its table, so nothing is lost) and an audit row `DatabaseIndexesRebuilt` records what was found. Two
  copies of the site writing the file at once can cause that, because it lives on a network share. Damage to the
  tables themselves is never touched: it is logged and shown at the top of the System Administrator's Overview.

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
