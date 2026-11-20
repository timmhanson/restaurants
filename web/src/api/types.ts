export type Role = 'SuperAdmin' | 'GroupAdmin' | 'Owner' | 'Manager' | 'TeamMember'

export type Permission =
  | 'ViewComments' | 'EditComments' | 'DeleteComments' | 'MarkResponded'
  | 'ViewAnalytics' | 'ExportComments' | 'ViewAudit' | 'ManageTeamLogin' | 'BuyPostage' | 'SetFollowUpDays'
  // These four belong to owners and are never given to a manager.
  | 'ManageStaff' | 'SetPermissions' | 'EditRestaurants' | 'DesignCards'

export interface UserDto {
  id: number
  username: string
  displayName: string
  email?: string | null
  role: Role
  /** Display title, e.g. "Investment Group Administrator". */
  roleTitle: string
  /** An investment group administrator who is also an owner of one or more restaurants (restaurantIds are the ones they own). */
  alsoOwner?: boolean
  groupId?: number | null
  groupName?: string | null
  permissions: number
  permissionNames: Permission[]
  restaurantIds: number[]
  isActive: boolean
  mustChangePassword: boolean
  createdAt: string
  lastLoginAt?: string | null
}

export interface LoginResponse {
  token: string
  expiresAt: string
  user: UserDto
  mustChangePassword: boolean
}

export interface UserRequest {
  username: string
  displayName: string
  email?: string | null
  password?: string | null
  role: Role
  permissions: Permission[]
  restaurantIds: number[]
  isActive: boolean
  /** Administrators only: false lets the person keep the password that was set for them. */
  requirePasswordChange?: boolean
}

/** A permission as it stands in the signed-in person's group. managers: all hold it, none can, or it is set for each manager. */
export interface PermissionInfo {
  name: Permission; value: number; description: string; forManagers: boolean; spendsMoney: boolean
  ownersCan: boolean; managers: 'all' | 'none' | 'each'; managersSetBy: 'site' | 'group' | 'owners'
}
/** One permission, with what the site and the group have each said about it and what comes of that. */
export interface PermissionState {
  name: Permission; description: string; forManagers: boolean; spendsMoney: boolean
  siteOwners: 'yes' | 'no' | 'group'; siteManagers?: 'yes' | 'no' | 'group' | null
  groupOwners: 'yes' | 'no'; groupManagers?: 'yes' | 'no' | 'each' | null
  ownersCan: boolean; ownersSetBy: 'site' | 'group'; managers: 'all' | 'none' | 'each'; managersSetBy: 'site' | 'group' | 'owners'
}
export interface PermissionRules { groupId: number; group: string; bySite: boolean; permissions: PermissionState[] }
/** The settings a group's administrators make for all restaurants. groupDecides lists the ones they may change: mail, mode, usual, signature. */
export interface GroupRules {
  mail: 'on' | 'off' | 'each'; mailMode: 'live' | 'test' | 'each'; siteMailMode: 'live' | 'test' | 'each'; usualWay: 'mail' | 'stamp' | 'each'
  signatureForAll?: string | null; groupDecides: string[]
}

export interface PersonRef { id: number; displayName: string; username: string }

export interface RestaurantDto {
  id: number
  name: string
  streetAddress?: string | null
  city?: string | null
  state?: string | null
  zip?: string | null
  phone?: string | null
  /** The name that signs cards entered with this restaurant's shared team login. Managers, owners and administrators sign with their own. */
  signatureName?: string | null
  /** On Print cards, "Mailed for you" is chosen to begin with for this restaurant's cards. */
  mailByDefault?: boolean
  /** The restaurant's own choices; each counts only when its group leaves that decision to the restaurant. */
  mailOn?: boolean
  mailLive?: boolean
  /** What actually applies once the group's (and the site's) settings are counted, and who decided each. */
  applies?: RestaurantRules | null
  isActive: boolean
  /** Filled in on the admin listing (GET /restaurants?all=true) only. */
  owners?: PersonRef[]
  managerCount?: number
  teamLoginCount?: number
  ownedByMe?: boolean
  canManage?: boolean
  /** May change its details and coupon card settings. */
  canEdit?: boolean
  /** An owner may mark this restaurant as theirs (no owner yet, and no investment group administrator to decide). */
  canClaim?: boolean
}

/** Who decided a setting: the site (for every group), the group (for all its restaurants) or the restaurant itself. */
export type SetBy = 'site' | 'group' | 'restaurant'
/**
 * What applies to one restaurant: whether cards can be mailed for it, whether for real (not as tests), whether
 * "Mailed for you" is its usual way, and the name that signs for the team login.
 */
export interface RestaurantRules {
  mailOn: boolean; mailLive: boolean; mailUsual: boolean; signature?: string | null
  mailOnSetBy: SetBy; mailLiveSetBy: SetBy; mailUsualSetBy: SetBy; signatureSetBy: SetBy
}
/** A group's settings as the System Administrator makes them, with each restaurant's own choices. */
export interface GroupSettingsData {
  groupId: number; group: string
  /** on, off, or each (each restaurant decides). */
  mail: 'on' | 'off' | 'each'
  /** live, test, or each (each restaurant decides); siteMailMode is the site's: live or test for every group, or each. */
  mailMode: 'live' | 'test' | 'each'; siteMailMode: 'live' | 'test' | 'each'
  usualWay: 'mail' | 'stamp' | 'each'
  /** A name for all restaurants, or empty when each restaurant decides. */
  signatureForAll?: string | null
  payRule: 'site' | 'required' | 'notrequired'; siteRequiresPay: boolean
  hasTestKey: boolean; hasLiveKey: boolean; creditCents: number; payLabel?: string | null
  /** The settings left to the group's own administrators: mail, mode, usual, signature. The rest are the site's answer for the group. */
  groupDecides: string[]
  restaurants: { id: number; name: string; isActive: boolean; mailOn: boolean; mailLive: boolean; mailByDefault: boolean; signatureName?: string | null; applies: RestaurantRules }[]
}

export interface StaffMember {
  id: number
  displayName: string
  username: string
  email?: string | null
  role: Role
  permissionNames: Permission[]
  assigned: boolean
  isActive: boolean
  otherRestaurants: string[]
}

export interface StaffResponse {
  restaurant: RestaurantDto
  canManage: boolean
  owners: StaffMember[]
  managers: StaffMember[]
  teamLogins: StaffMember[]
  /** Investment Group administrators see every restaurant without being assigned. */
  groupAdmins: PersonRef[]
  /** The group's administrators, each marked as assigned when they are also an owner of this restaurant. */
  adminOwners: StaffMember[]
  /** False when the group has an investment group administrator, so a restaurant may have no specific owner. */
  ownerRequired: boolean
}

export interface RestaurantRequest {
  name: string
  streetAddress?: string | null
  city?: string | null
  state?: string | null
  zip?: string | null
  phone?: string | null
  signatureName?: string | null
  mailByDefault?: boolean
  mailOn?: boolean
  mailLive?: boolean
  isActive: boolean
  /** Group creation only: usernames of the new owners who own this restaurant. */
  ownerUsernames?: string[] | null
}

export interface GroupDto {
  id: number
  name: string
  isActive: boolean
  createdAt: string
  restaurantCount: number
  userCount: number
  commentCount: number
  ownerNames: string[]
  adminNames: string[]
}

export interface GroupRequest {
  name: string
  isActive: boolean
  firstOwner?: UserRequest | null
  admins?: UserRequest[]
  owners?: UserRequest[]
  restaurants?: RestaurantRequest[]
}

/** qr is the card's QR code: a square whose top-left corner is at its position. */
export type PostcardFieldKey = 'offer' | 'dateIssued' | 'goodUntil' | 'authorizedBy' | 'guestName' | 'street' | 'cityStateZip' | 'qr'
export interface PostcardField { x: number; y: number; show: boolean }
/** How coupon cards print. Positions are inches from the card's top-left corner. */
export interface PostcardSettings {
  stock: 'Preprinted' | 'Blank'
  widthIn: number
  heightIn: number
  nudgeXIn: number
  nudgeYIn: number
  fontPt: number
  allowBoughtPostage: boolean
  fields: Record<PostcardFieldKey, PostcardField>
}
export interface PrintConfig {
  postageAvailable: boolean
  postageProvider: string
  canBuyPostage: boolean
  maxCentsPerCard: number
  mailedMethod: string
  /** The site can print and mail cards for this group (its monthly limit is above zero). */
  mailAvailable: boolean
  /** mailAvailable, a payment method is saved if one is required, and this person has the "buy postage" permission. */
  canMail: boolean
  /** Mailing is on, but the group must first save a payment method on Billing. */
  mailNeedsPayMethod: boolean
  /**
   * Why this person cannot have cards mailed right now, or null when they can: the site has no mailing account yet,
   * it is not switched on for the group, the group's monthly limit is zero, a payment method must be saved first,
   * or this person may not spend money.
   */
  mailBlockedBy?: 'site' | 'group' | 'limit' | 'payment' | 'permission' | null
  /** No payment method is saved where one is required, so the group mails on its credit: only as many cards as mailCreditCents covers. */
  mailOnCredit: boolean
  /** The group's credit that is free to spend on more cards, in cents. */
  mailCreditCents: number
  /** What the group is billed for each card it prints itself for a stamp by hand, in cents (0 when nothing is). Charged once per card, at its first print. */
  printFeeCents: number
  /** For each restaurant this person works at: whether cards can be mailed for it, whether for real, and whether that is set up on the site. */
  mailRestaurants: { restaurantId: number; on: boolean; live: boolean; ready: boolean }[]
  /** The site is in test mode for mailing: nothing is printed, mailed or charged. */
  mailTest: boolean
  /** What the group is billed for each mailed card, in cents. */
  mailCentsPerCard: number
  mailMaxCards: number
  /** The group's limit for mailed cards this calendar month, and what has been billed so far, in cents. */
  mailLimitCents: number
  mailUsedCents: number
}
/** Where a mailed card is. */
export type MailStatus = 'ready' | 'printing' | 'processed_for_delivery' | 'completed' | 'cancelled'
export interface MailOutcome {
  commentId: number; ok: boolean; status?: MailStatus | null; test: boolean; alreadyMailed: boolean; error?: string | null
  /** The card was not taken because of the guest's address: correcting the address is the way forward. */
  addressProblem?: boolean
}
export interface MailStatusInfo { commentId: number; status?: MailStatus | null; test: boolean; hasProof: boolean; error?: string | null }
export interface MailPreview { frontHtml: string; backHtml: string }
/** A card's QR code: the address it holds (this site's verify page) and the code as a picture for an img tag. */
export interface CardQr { commentId: number; cardId: string; url: string; image: string }
/** What scanning a card's QR code shows, once signed in. card is null when the code is not genuine or the card is another restaurant's. */
export interface CardCheck {
  genuine: boolean; message: string; canRedeem: boolean
  card?: {
    commentId: number; cardId: string; restaurant: string; guestName: string; offer?: string | null; dateIssued: string; expiresOn: string; expired: boolean
    redeemedOn?: string | null; redeemedByName?: string | null; sent: boolean; canOpen: boolean
  } | null
}

// ---- How mailed cards look ----
/** custom: laid out item by item in the card designer. */
export type CardFront = 'plain' | 'designed' | 'picture' | 'custom'
export type CardLayout = 'centered' | 'photo' | 'split'
export type CardBack = 'classic' | 'simple' | 'custom'
/**
 * backlogo: the logo on a classic back. footer: a small picture for the back's bottom corner, such as social media
 * icons. A picture placed in the card designer has a kind of its own, el- and a few letters and digits.
 */
export type CardImageKind = 'picture' | 'logo' | 'photo' | 'backlogo' | 'footer' | (string & {})
export type CardElementType = 'text' | 'fill' | 'line' | 'box' | 'image' | 'qr'
export type CardFont = 'sans' | 'geometric' | 'narrow' | 'script'
/**
 * One thing placed on a card in the card designer. x, y, w and h are inches from the top-left corner of the finished
 * 6 x 4 inch card; size and thickness are points; color is a colour such as #2b4a8b or "ink" for the card's ink colour.
 * text can carry fields in braces ({restaurant}, {offer}, …) that are filled in for each card.
 */
export interface CardElement {
  id: string; type: CardElementType; x: number; y: number; w: number; h: number; text?: string | null; value?: string | null
  font: CardFont; valueFont?: CardFont | null; valueSize?: number; size: number; bold: boolean; italic: boolean; lineUnderLabel: boolean; align: 'left' | 'center' | 'right'; color: string; lineHeight: number; thickness: number; image?: string | null
}
/** A field a card's words can carry, with what it prints on the sample card. */
export interface CardMergeField { key: string; label: string; sample: string }
/** fromGroup: borrowed from the group's card, because this restaurant has not uploaded its own. */
export interface CardImage { kind: CardImageKind; url: string; widthPx?: number | null; heightPx?: number | null; bytes: number; uploadedAt: string; uploadedBy: string; fromGroup: boolean }
export interface CardDesignFields {
  front: CardFront; layout: CardLayout; headline?: string | null; message?: string | null; backMessage?: string | null; backgroundColor?: string | null; textColor?: string | null
  /** The back: classic (the restaurant's own coupon card) or simple (a plain coupon box), its ink colour, web address and small print. */
  backStyle: CardBack; inkColor?: string | null; website?: string | null; legalText?: string | null
  /** The items on a back or front laid out in the card designer. */
  backLayout?: CardElement[] | null; frontLayout?: CardElement[] | null
}
/** scope is "group" or a restaurant's id. hasOwn is false when a restaurant uses the group's card. */
export interface CardDesign extends CardDesignFields {
  scope: string; restaurantId?: number | null; name: string; canEdit: boolean; hasOwn: boolean; backgroundColor: string; textColor: string
  inkColor: string
  /** The standard small print, printed when legalText is empty. */
  defaultLegalText: string
  images: CardImage[]; updatedAt?: string | null; updatedBy?: string | null
  /** The classic back and a starter front as items, which a card laid out in the designer starts from. */
  standardBack: CardElement[]; starterFront: CardElement[]
  mergeFields: CardMergeField[]
  /** A stand-in QR code picture, to show where each card's own code is printed. */
  sampleQr: string
}
export interface CardDesignScope { scope: string; restaurantId?: number | null; name: string; canEdit: boolean; hasOwn: boolean }

// ---- Billing for mailed cards ----
export interface InvoiceLine { restaurantId: number; restaurant: string; cards: number; cents: number }
export interface MailMonthRow { restaurantId: number; restaurant: string; cards: number; totalCents: number }
export interface MailMonth { month: string; monthName: string; cards: number; totalCents: number; restaurants: MailMonthRow[] }
export interface PayMethod { kind: 'card' | 'bank'; funding?: string | null; label: string; setAt?: string | null; setBy?: string | null }
export interface BillingContact { name?: string | null; email?: string | null; street?: string | null; city?: string | null; state?: string | null; zip?: string | null }
export type InvoiceStatus = 'open' | 'processing' | 'paid' | 'void'
export interface Invoice {
  id: number; number: string; month: string; monthName: string; cards: number; totalCents: number; issuedAt: string; dueOn: string; status: InvoiceStatus; overdue: boolean
  paidAt?: string | null; paidHow?: string | null; surchargeCents: number
  /** What paying with the saved payment method would add (a credit card or a bank account). */
  payFeeCents: number
  paymentError?: string | null; lines: InvoiceLine[]
  /** The restaurant or set of restaurants the bill is for, when the group's month is split into several bills. */
  for?: string | null
  /** How much of the group's credit was used on this bill, and what is still owed after it. */
  creditCents: number; dueCents: number
}
/** One line of a group's credit account as the group sees it. Above zero is credit received; below zero is credit used. */
export interface GroupCreditLine { at: string; kind: string; label: string; amountCents: number; invoiceNumber?: string | null }
/** One line of the credit log as the System Administrator sees it, with the details entered. */
export interface GroupCredit { id: number; groupId: number; group: string; at: string; kind: string; label: string; amountCents: number; details: string; by: string; invoiceNumber?: string | null }
export interface GroupBilling {
  /** How the group has its month billed (one, restaurant or sets), and the set each restaurant is billed with. Administrators only. */
  bills?: { split: 'one' | 'restaurant' | 'sets'; sets: { restaurantId: number; restaurant: string; set?: string | null }[] } | null
  /** The group's unused credit, which comes off its next bills by itself, and the lines that make it up. */
  creditCents: number; credits: GroupCreditLine[]
  /** What the group is billed for each card it prints itself for a stamp by hand, in cents (0 when nothing is). */
  printFeeCents: number
  /** Real or test mailing for the group (each: each restaurant decides), and whether the site has fixed it for every group. */
  mailMode: 'live' | 'test' | 'each'; mailModeSetBySite: boolean
  /** A group administrator: may save a payment method, pay bills and change where bills go. Owners only see usage. */
  canManage: boolean
  mailAvailable: boolean; test: boolean; centsPerCard: number; limitCents: number; usedCents: number; cardsThisMonth: number
  payAvailable: boolean; payRequired: boolean; payTest: boolean; cardFeePercent: number; bankFeePercent: number; method?: PayMethod | null; autoPay: boolean; contact: BillingContact
  businessName: string; businessEmail?: string | null; payInstructions?: string | null; billDay: number; dueDays: number
  months: MailMonth[]; invoices: Invoice[]; owedCents: number
}
export interface BillingActionResult { ok: boolean; message: string; billing: GroupBilling }

export interface BillingSettings {
  costCents: number; feeCents: number; monthlyLimitCents: number; billDay: number; dueDays: number; emailBills: boolean; printBills: boolean
  cardFeePercent: number; bankFeePercent: number; requirePaymentMethod: boolean
  /** Groups are also charged the site's fee for each card they print themselves for a stamp by hand. */
  chargeHandPrintFee: boolean
  /** Real or test mailing for the whole site: live or test for every group, or each (each group decides). */
  mailMode: 'live' | 'test' | 'each'; businessName: string; businessEmail?: string | null
  street?: string | null; city?: string | null; state?: string | null; zip?: string | null; payInstructions?: string | null; siteUrl?: string | null
}
/** One notification from Stripe's webhook. label says it in plain words; needsALook marks a refund or a dispute. */
export interface PaymentNotification {
  id: number; eventId: string; type: string; label: string; receivedAt: string; happenedAt?: string | null; test: boolean; deliveries: number
  objectType?: string | null; objectId?: string | null; paymentId?: string | null; invoiceId?: number | null; bill?: string | null
  groupId?: number | null; group?: string | null; amountCents?: number | null; currency?: string | null; status?: string | null; reason?: string | null
  outcome?: string | null; needsALook: boolean
}
export interface PaymentNotificationDetail { notification: PaymentNotification; dataPoints: { name: string; value: string }[] }
export interface SystemInvoice extends Omit<Invoice, 'payFeeCents'> {
  /** What Stripe has told the site about this bill's payment, newest first. */
  notifications: PaymentNotification[]
  groupId: number; group: string; costCents: number; feeCents: number; paidNote?: string | null; paidBy?: string | null; paymentRef?: string | null
  emailedAt?: string | null; emailedTo?: string | null; printedAt?: string | null; deliveryError?: string | null
}
export interface GroupBillingRow {
  groupId: number; group: string; active: boolean; feeCents?: number | null; effectiveFeeCents: number; limitCents?: number | null; effectiveLimitCents: number
  cardsThisMonth: number; billedThisMonthCents: number; owedCents: number; payLabel?: string | null; autoPay: boolean; billingEmail?: string | null
  /** Having cards printed and mailed is switched on for this group. */
  mailEnabled: boolean
  /** on, off or each (each restaurant decides); and live, test or each for real or test mailing. */
  mail: 'on' | 'off' | 'each'; mailMode: 'live' | 'test' | 'each'
  /** Whether this group must save a payment method before mailing; ownPayRule is null when it follows the site's setting. */
  payRequired: boolean; ownPayRule?: boolean | null
  /** The group's unused credit. */
  creditCents: number
}
export interface SystemBilling {
  settings: BillingSettings; mailReady: boolean; mailTest: boolean; payReady: boolean; payTest: boolean; emailReady: boolean
  groups: GroupBillingRow[]; invoices: SystemInvoice[]; unbilledMonths: string[]; owedCents: number; overdueCents: number
  /** The credit log (newest first, for the group chosen), unused credit across every group, and the most one credit can be. */
  credits: GroupCredit[]; creditCents: number; maxCreditCents: number
}
export interface SiteMailStatementRow { groupId: number; group: string; restaurantId: number; restaurant: string; cards: number; costCents: number; feeCents: number; totalCents: number ; printedCards: number }
export interface SiteMailStatement { month: string; months: string[]; rows: SiteMailStatementRow[]; cards: number; costCents: number; feeCents: number; totalCents: number }
/** A key saved on Connected Systems: never sent to the browser, only its last four characters. */
export interface SiteKeyInfo { configured: boolean; source: 'Saved in the site' | 'App settings' | 'None'; keyEnding?: string | null; test: boolean; savedAt?: string | null; savedBy?: string | null }
/** restricted: the key is a restricted key (rk_). The webhook signing secret is never sent to the browser, only its last four characters. */
export interface StripeAccount extends SiteKeyInfo {
  groupsWithMethod: number; canEnter: boolean; restricted: boolean
  webhookConfigured: boolean; webhookEnding?: string | null; lastNotificationAt?: string | null; apiVersion: string
}
export interface PostageOutcome { commentId: number; ok: boolean; cents?: number | null; labelUrl?: string | null; test: boolean; alreadyBought: boolean; error?: string | null }
export interface FollowUpRestaurant { id: number; name: string; days?: number | null; effectiveDays: number; canSet: boolean }
export interface FollowUpSummary { due: number; oldestDueOn?: string | null; defaultDays: number; restaurants: FollowUpRestaurant[] }

export interface GroupSettings {
  schemaVersion: number
  followUpDays: number
  postcard: PostcardSettings
  groupDisplayName: string
  accentColor: string
  couponOffers: string[]
  reasonCategories: string[]
  couponValidityMonths: number
  duplicateWindowDays: number
  repeatGuestAlertThreshold: number
  replyMethods: string[]
  requirePhoneOrAddress: boolean
  updatedAt?: string | null
  updatedBy?: string | null
}

/** How a guest comment came in. Phone calls need the guest's phone number. */
export const COMMENT_SOURCES = [
  { value: 'SMG survey', label: 'SMG survey', hint: 'Guest survey from SMG' },
  { value: 'Culvers.com Contact Us', label: 'Culvers.com', hint: 'Contact Us form on Culvers.com' },
  { value: 'Phone call', label: 'Phone call', hint: 'The guest called the restaurant' },
] as const
export const PHONE_SOURCE = 'Phone call'

export interface CommentRequest {
  source?: string | null
  /** What was done with USPS's version of the address, for the audit log: took it, or kept what was typed. */
  addressChoice?: 'UsedUsps' | 'KeptOwn' | null
  addressAsTyped?: string | null
  restaurantId: number
  guestFirstName: string
  guestLastName: string
  streetAddress?: string | null
  city?: string | null
  state?: string | null
  zip?: string | null
  phone?: string | null
  couponOffer?: string | null
  dateIssued?: string | null
  authorizedBy?: string | null
  visitDate: string
  reason: string
  reasonDetails?: string | null
  notes?: string | null
  force: boolean
}

export interface CommentSummary {
  id: number
  restaurantId: number
  restaurantName: string
  guestFirstName: string
  guestLastName: string
  phone?: string | null
  streetAddress?: string | null
  city?: string | null
  state?: string | null
  zip?: string | null
  couponOffer?: string | null
  dateIssued: string
  expiresOn: string
  authorizedBy?: string | null
  visitDate: string
  dayOfWeek: string
  reason: string
  reasonDetails?: string | null
  notes?: string | null
  responded: boolean
  respondedAt?: string | null
  respondedByName?: string | null
  createdAt: string
  createdByName: string
  updatedAt?: string | null
  guestKey: string
  guestCommentCount: number
  replyMethod?: string | null
  source?: string | null
  repliedOn?: string | null
  lastActivityAt?: string | null
  noteCount: number
  /** What USPS said about the mailing address when the card was saved; absent when it was not checked. */
  addressStatus?: AddressStatus | null
  printedAt?: string | null
  printedByName?: string | null
  printCount: number
  postageCents?: number | null
  postageLabelUrl?: string | null
  postageTrackingCode?: string | null
  postageTest: boolean
  /** The fee the group was charged because this card was printed in the restaurant for a stamp by hand; null when none was. */
  printFeeCents?: number | null
  /** The card's own identifier, carried (signed) by the QR code printed on it. */
  cardId: string
  mailStatus?: MailStatus | null
  mailedAt?: string | null
  mailTest?: boolean
  redeemedOn?: string | null
  redeemedByName?: string | null
  followedUpOn?: string | null
  followedUpByName?: string | null
  /** The day the guest should be followed up with; absent when none is needed. */
  followUpDueOn?: string | null
  attemptCount: number
  latestNote?: ActivityDto | null
}

export type ActivityKind =
  | 'Note' | 'Attempt' | 'Replied' | 'Reopened' | 'Printed' | 'PostageBought' | 'Redeemed' | 'RedemptionUndone' | 'FollowedUp' | 'FollowUpAttempt' | 'Mailed' | 'MailCancelled'

export interface ActivityDto {
  id: number
  kind: ActivityKind
  method?: string | null
  contactedOn?: string | null
  text?: string | null
  createdAt: string
  createdByName: string
  createdByRole: string
}

export interface FollowUpRequest {
  kind: ActivityKind
  method?: string | null
  contactedOn?: string | null
  text?: string | null
  confirmed?: boolean
}

export interface FollowUpResponse { comment: CommentSummary; activity: ActivityDto[] }

export interface DuplicateCheckResult {
  exactDuplicate: boolean
  likelyDuplicate: boolean
  repeatGuest: boolean
  priorCount: number
  threshold: number
  guestKey: string
  priorComments: CommentSummary[]
  message?: string | null
}

export interface PagedResult<T> { items: T[]; total: number; page: number; pageSize: number }

export interface CountItem { label: string; count: number; responded?: number | null }

export interface RepeatGuestItem {
  guestKey: string
  name: string
  phone?: string | null
  address?: string | null
  count: number
  lastVisit: string
  restaurants: string[]
  reasons: string[]
}

export interface AnalyticsDto {
  totalComments: number
  responded: number
  unresponded: number
  distinctGuests: number
  repeatGuests: number
  avgPerWeek: number
  byDayOfWeek: CountItem[]
  byReason: CountItem[]
  byCouponOffer: CountItem[]
  byRestaurant: CountItem[]
  byWeek: CountItem[]
  byMonth: CountItem[]
  byEnteredBy: CountItem[]
  topRepeatGuests: RepeatGuestItem[]
  scope?: { id: number; name: string }[] | null
  restaurants: RestaurantRollup[]
  monthlyByRestaurant: RestaurantMonth[]
  crossRestaurantGuests: number
  avgDaysToReply?: number | null
  waitingOverAWeek: number
  byReplyMethod: CountItem[]
  bySource: CountItem[]
  printed: number
  redeemed: number
  followedUp: number
  postageCents: number
}

export interface RestaurantRollup {
  restaurantId: number
  name: string
  total: number
  responded: number
  waiting: number
  responseRate: number
  guests: number
  repeatGuests: number
  avgPerWeek: number
  avgDaysToReply?: number | null
  waitingOverAWeek: number
  oldestWaitingDays?: number | null
  topReason?: string | null
  lastVisit?: string | null
}

export interface RestaurantMonth { month: string; restaurantId: number; restaurant: string; count: number }

export interface AuditDto {
  id: number
  timestamp: string
  userId?: number | null
  userName: string
  userRole?: string | null
  restaurantId?: number | null
  restaurantName?: string | null
  action: string
  entityType?: string | null
  entityId?: string | null
  details?: string | null
  ipAddress?: string | null
}

// ---- System Administrator: interaction log and reports ----
export type InteractionKind = 'Api' | 'Page' | 'Click' | 'Choice' | 'SignOut'
export interface InteractionDto {
  id: number
  at: string
  kind: InteractionKind
  userId?: number | null
  userName?: string | null
  displayName?: string | null
  roleTitle?: string | null
  groupId?: number | null
  groupName?: string | null
  restaurantId?: number | null
  restaurantName?: string | null
  viewingAs: boolean
  method?: string | null
  path: string
  action?: string | null
  detail?: string | null
  status?: number | null
  durationMs?: number | null
  sessionId?: string | null
  ipAddress?: string | null
  userAgent?: string | null
}
export interface InteractionPage { total: number; page: number; pageSize: number; items: InteractionDto[] }
export interface SystemCatalog {
  groups: { id: number; name: string; isActive: boolean }[]
  restaurants: { id: number; name: string; groupId: number; isActive: boolean }[]
  users: { id: number; username: string; displayName: string; roleTitle: string; groupId?: number | null }[]
}
export interface CardStats {
  cards: number
  printed: number
  redeemed: number
  followedUp: number
  postageCents: number
  /** Cards printed and mailed by PostGrid, what they were expected to cost and the site's fee (cents). */
  mailedByPostGrid: number
  mailEstimateCents: number
  mailFeeCents: number
  replied: number
  waiting: number
  waitingOverAWeek: number
  avgDaysToReply?: number | null
  smg: number
  website: number
  phone: number
  sourceNotRecorded: number
}
export interface GroupReportRow {
  id: number; name: string; isActive: boolean; restaurants: number; users: number; activeUsers: number; signIns: number; interactions: number
  lastActivity?: string | null; stats: CardStats
}
export interface RestaurantReportRow {
  id: number; name: string; groupId: number; groupName: string; isActive: boolean; staff: number; activeUsers: number; interactions: number
  lastActivity?: string | null; lastVisit?: string | null; stats: CardStats
}
export interface UserReportRow {
  id: number; displayName: string; username: string; roleTitle: string; groupId?: number | null; groupName?: string | null
  interactions: number; signIns: number; pageViews: number; cardsEntered: number; lastSeen?: string | null
}
export interface DailyReportRow { date: string; groupId?: number | null; interactions: number; activeUsers: number; cards: number }
export interface SystemReport {
  from: string; to: string; totals: CardStats; groups: number; restaurants: number; users: number; activeUsers: number; signIns: number
  failedSignIns: number; interactions: number; byGroup: GroupReportRow[]; byRestaurant: RestaurantReportRow[]; topUsers: UserReportRow[]
  daily: DailyReportRow[]; byKind: CountItem[]
}

// ---- System administration: overview, security, status, directories ----
export interface AttentionItem { severity: 'high' | 'medium' | 'info'; title: string; detail: string; link?: string | null }
export interface GroupSnapshot {
  id: number; name: string; isActive: boolean; restaurants: number; people: number; admins: number; activePeople7d: number; cards7d: number
  waiting: number; waitingOverAWeek: number; lastActivity?: string | null
}
export interface SignInRow {
  at: string; userName?: string | null; displayName?: string | null; roleTitle?: string | null; groupName?: string | null; succeeded: boolean
  ipAddress?: string | null; userAgent?: string | null
}
export interface SystemOverview {
  generatedAt: string; groups: number; activeGroups: number; restaurants: number; people: number; systemAdmins: number; activeToday: number
  active7d: number; signInsToday: number; failedSignIns24h: number; cardsToday: number; cards7d: number; waiting: number; waitingOverAWeek: number
  errors24h: number; attention: AttentionItem[]; groupSnapshots: GroupSnapshot[]; recentSignIns: SignInRow[]
}
export interface FailedSignInRow { userName: string; knownAccount: boolean; attempts: number; last: string; ipAddresses: string[] }
export interface RefusedRow { userName?: string | null; displayName?: string | null; roleTitle?: string | null; groupName?: string | null; count: number; last: string; paths: string[] }
export interface AccountFlag { id: number; displayName: string; username: string; roleTitle: string; groupId?: number | null; groupName?: string | null; issue: string; since?: string | null }
export interface SystemSecurity {
  days: number; signIns: number; failedSignIns: number; refused: number; staleCopies: number; failed: FailedSignInRow[]; refusedBy: RefusedRow[]
  accounts: AccountFlag[]; systemAdministrators: UserDto[]; recentSignIns: SignInRow[]
}
export interface EndpointStat { endpoint: string; calls: number; avgMs: number; maxMs: number; errors: number }
export interface ErrorRow { at: string; userName?: string | null; groupName?: string | null; method: string; path: string; status: number; detail?: string | null }
export interface SystemStatus {
  version: string; environment: string; startedAt: string; serverTime: string; database: string; databaseBytes: number; latestMigration?: string | null
  tables: { name: string; rows: number }[]; settingsStore: string; email: string; emailConfigured: boolean; testEmailTo?: string | null
  logRetentionDays: number; oldestLogEntry?: string | null; requests24h: number; errors24h: number; refused24h: number; avgMs24h: number
  p95Ms24h: number; slowest: EndpointStat[]; recentErrors: ErrorRow[]
  addressChecking: string; addressCheckingConfigured: boolean; addressesConfirmed: number; addressesNotFound: number
  postageGroupAccounts: number
  postage: string; postageConfigured: boolean; postageCardsBought: number; postageCentsSpent: number; postageTestLabels: number
}
export interface DirectoryRestaurant {
  id: number; name: string; city?: string | null; state?: string | null; isActive: boolean; groupId: number; groupName: string; groupIsActive: boolean
  owners: string[]; managers: number; teamLogins: number; cards: number; waiting: number; waitingOverAWeek: number; lastVisit?: string | null; createdAt: string
}
/** The copies of the site's database. offSite: copies are also sent to the site's storage account. */
export interface Backups {
  enabled: boolean
  last?: { at: string; ok: boolean; name?: string | null; bytes: number; offSite: boolean; problem?: string | null; by: string } | null
  files: { name: string; at: string; bytes: number; onSite: boolean; offSite: boolean }[]
  offSite: boolean; hourUtc: number; keepOnSite: number; keepDaily: number; keepMonthly: number
}
export interface SystemPerson { user: UserDto; restaurants: string[]; lastSeen?: string | null; interactions30d: number }

// ---- Checking a mailing address with USPS ----
export type AddressStatus = 'Verified' | 'Corrected' | 'NeedsUnit' | 'UnitNotConfirmed' | 'NotFound'
export interface AddressSuggestion { streetAddress: string; city: string; state: string; zip: string }
/** An address offered while the street is typed; city, state and zip are there when the address service gave them. */
export interface AddressPreview { streetAddress: string; city?: string | null; state?: string | null; zip?: string | null }
export interface AddressCheckResponse { available: boolean; status: AddressStatus | 'Incomplete' | 'Unavailable'; message: string; suggested?: AddressSuggestion | null }

/** An investment group's own postage account. The key itself never reaches the browser. */
export interface PostageAccount {
  hasOwnAccount: boolean
  keyEnding?: string | null
  looksLikeTestKey: boolean
  addedAt?: string | null
  addedBy?: string | null
  canAddAccount: boolean
  siteAccountAvailable: boolean
  provider: string
}

/** The site's USPS account for address checking. Neither the key nor the secret ever reaches the browser. */
export interface UspsAccount {
  configured: boolean
  source: 'Saved in the site' | 'App settings' | 'None'
  keyEnding?: string | null
  testEnvironment: boolean
  savedAt?: string | null
  savedBy?: string | null
  canEnter: boolean
}
export interface ServiceTestResult { ok: boolean; message: string }

/** PostGrid on Connected Systems: the address verification key (top level) and the Print & Mail key that mails every group's cards. */
export interface PostGridSite {
  configured: boolean
  source: 'Saved in the site' | 'App settings' | 'None'
  keyEnding?: string | null
  /** PostGrid is asked before USPS; otherwise only when USPS cannot answer. */
  useFirst: boolean
  savedAt?: string | null
  savedBy?: string | null
  canEnter: boolean
  mail: SiteKeyInfo
  /** The test key and the live key for printing and mailing, each entered on its own. */
  mailTestKey: SiteKeyInfo
  mailLiveKey: SiteKeyInfo
  cardsMailed: number
  testCardsMailed: number
  cardsThisMonth: number
}
