export type Role = 'SuperAdmin' | 'Owner' | 'Manager' | 'TeamMember'

export type Permission =
  | 'ViewComments' | 'EditComments' | 'DeleteComments' | 'MarkResponded'
  | 'ViewAnalytics' | 'ExportComments' | 'ViewAudit' | 'ManageTeamLogin'

export interface UserDto {
  id: number
  username: string
  displayName: string
  email?: string | null
  role: Role
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
}

export interface RestaurantRequest {
  name: string
  streetAddress?: string | null
  city?: string | null
  state?: string | null
  zip?: string | null
  phone?: string | null
  isActive: boolean
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
}

export interface GroupRequest {
  name: string
  isActive: boolean
  firstOwner?: UserRequest | null
  owners?: UserRequest[]
  restaurants?: RestaurantRequest[]
}

export interface GroupSettings {
  schemaVersion: number
  groupDisplayName: string
  accentColor: string
  couponOffers: string[]
  reasonCategories: string[]
  couponValidityMonths: number
  duplicateWindowDays: number
  repeatGuestAlertThreshold: number
  requirePhoneOrAddress: boolean
  updatedAt?: string | null
  updatedBy?: string | null
}

export interface CommentRequest {
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
}

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
}

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
