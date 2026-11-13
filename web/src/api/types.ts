export type Role = 'SuperAdmin' | 'GroupAdmin' | 'Owner' | 'Manager' | 'TeamMember'

export type Permission =
  | 'ViewComments' | 'EditComments' | 'DeleteComments' | 'MarkResponded'
  | 'ViewAnalytics' | 'ExportComments' | 'ViewAudit' | 'ManageTeamLogin' | 'BuyPostage' | 'SetFollowUpDays'

export interface UserDto {
  id: number
  username: string
  displayName: string
  email?: string | null
  role: Role
  /** Display title, e.g. "Investment Group Administrator". */
  roleTitle: string
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

export interface PermissionInfo { name: Permission; value: number; description: string }

export interface PersonRef { id: number; displayName: string; username: string }

export interface RestaurantDto {
  id: number
  name: string
  streetAddress?: string | null
  city?: string | null
  state?: string | null
  zip?: string | null
  phone?: string | null
  isActive: boolean
  /** Filled in on the admin listing (GET /restaurants?all=true) only. */
  owners?: PersonRef[]
  managerCount?: number
  teamLoginCount?: number
  ownedByMe?: boolean
  canManage?: boolean
  /** An owner may mark this restaurant as theirs (no owner yet, and no investment group administrator to decide). */
  canClaim?: boolean
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

export type PostcardFieldKey = 'offer' | 'dateIssued' | 'goodUntil' | 'authorizedBy' | 'guestName' | 'street' | 'cityStateZip'
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
export interface PrintConfig { postageAvailable: boolean; postageProvider: string; canBuyPostage: boolean; maxCentsPerCard: number; mailedMethod: string }
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
  | 'Note' | 'Attempt' | 'Replied' | 'Reopened' | 'Printed' | 'PostageBought' | 'Redeemed' | 'RedemptionUndone' | 'FollowedUp' | 'FollowUpAttempt'

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
  postage: string; postageConfigured: boolean; postageCardsBought: number; postageCentsSpent: number; postageTestLabels: number
}
export interface DirectoryRestaurant {
  id: number; name: string; city?: string | null; state?: string | null; isActive: boolean; groupId: number; groupName: string; groupIsActive: boolean
  owners: string[]; managers: number; teamLogins: number; cards: number; waiting: number; waitingOverAWeek: number; lastVisit?: string | null; createdAt: string
}
export interface SystemPerson { user: UserDto; restaurants: string[]; lastSeen?: string | null; interactions30d: number }

// ---- Checking a mailing address with USPS ----
export type AddressStatus = 'Verified' | 'Corrected' | 'NeedsUnit' | 'UnitNotConfirmed' | 'NotFound'
export interface AddressSuggestion { streetAddress: string; city: string; state: string; zip: string }
export interface AddressCheckResponse { available: boolean; status: AddressStatus | 'Incomplete' | 'Unavailable'; message: string; suggested?: AddressSuggestion | null }
