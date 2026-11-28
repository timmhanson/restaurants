import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode, useRef } from 'react'
import { api, groupStore, onAuthEvent, tokenStore } from '../api/client'
import { flush } from '../api/tracker'
import type { GroupDto, GroupSettings, LoginResponse, Permission, RestaurantDto, UserDto } from '../api/types'

interface AuthState {
  user: UserDto | null
  loading: boolean
  mustChangePassword: boolean
  restaurants: RestaurantDto[]
  /** True once the restaurants and settings for the current user / group have come back (even if empty). */
  contextLoaded: boolean
  /** The last attempt to load the restaurants failed, so an empty list means "unknown", not "none". */
  contextFailed: boolean
  settings: GroupSettings | null
  groups: GroupDto[]
  groupId: number | null
  login: (username: string, password: string) => Promise<LoginResponse>
  logout: () => void
  applyLogin: (res: LoginResponse) => void
  selectGroup: (id: number | null) => void
  refreshContext: () => Promise<void>
  refreshGroups: () => Promise<void>
  has: (p: Permission) => boolean
  /** System administrators, investment group administrators and restaurant owners (owners only for their restaurants). */
  isAdmin: boolean
  isSuperAdmin: boolean
  isGroupAdmin: boolean
  /** System or investment group administrator: everything in the group. */
  isGroupWide: boolean
  isTeamMember: boolean
  /** A system administrator looking at one group as its Investment Group Administrator would (the dark blue bar). */
  viewingAs: boolean
  /** A system administrator with no group open: the oversight pages only. */
  overseeing: boolean
  /** Leave the group being viewed and go back to system administration. */
  exitGroup: () => void
}

const AuthContext = createContext<AuthState | null>(null)

const defaultSettings: GroupSettings = {
  schemaVersion: 1,
  groupDisplayName: '',
  accentColor: '#005daa',
  couponOffers: [],
  reasonCategories: [],
  couponValidityMonths: 3,
  duplicateWindowDays: 7,
  repeatGuestAlertThreshold: 2,
  requirePhoneOrAddress: true, replyMethods: [],
  followUpDays: 5,
  postcard: {
    stock: 'Blank', widthIn: 6, heightIn: 4, nudgeXIn: 0, nudgeYIn: 0, fontPt: 11, allowBoughtPostage: true,
    fields: {
      offer: { x: 0.95, y: 1.2, show: true }, dateIssued: { x: 1.15, y: 1.95, show: true }, goodUntil: { x: 1.15, y: 2.3, show: false },
      authorizedBy: { x: 1.6, y: 2.75, show: true }, guestName: { x: 3.45, y: 1.7, show: true }, street: { x: 3.45, y: 2.1, show: true },
      cityStateZip: { x: 3.45, y: 2.5, show: true }, qr: { x: 4.9, y: 1.75, show: true },
    },
  },
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [mustChangePassword, setMustChange] = useState(false)
  const [restaurants, setRestaurants] = useState<RestaurantDto[]>([])
  const [contextLoaded, setContextLoaded] = useState(false)
  const [contextFailed, setContextFailed] = useState(false)
  /** The person and group the restaurants on screen were loaded for. */
  const contextFor = useRef<string | null>(null)
  const [settings, setSettings] = useState<GroupSettings | null>(null)
  const [groups, setGroups] = useState<GroupDto[]>([])
  const [groupId, setGroupId] = useState<number | null>(groupStore.get())

  const logout = useCallback(() => {
    flush() // send the last clicks while the token is still valid
    tokenStore.set(null)
    setUser(null)
    setRestaurants([])
    setContextLoaded(false)
    contextFor.current = null
    setSettings(null)
    setGroups([])
    setMustChange(false)
  }, [])

  const refreshGroups = useCallback(async () => {
    try {
      const g = await api.get<GroupDto[]>('/groups')
      setGroups(g)
      // Forget a group that no longer exists so the system administrator is not stuck viewing it.
      const open = groupStore.get()
      if (open && !g.some((x) => x.id === open)) {
        groupStore.set(null)
        setGroupId(null)
      }
    } catch {
      setGroups([])
    }
  }, [])

  const refreshContext = useCallback(async () => {
    let failed = false
    const [r, s] = await Promise.all([
      api.get<RestaurantDto[]>('/restaurants').catch(() => {
        failed = true
        return [] as RestaurantDto[]
      }),
      api.get<GroupSettings>('/settings').catch(() => defaultSettings),
    ])
    setRestaurants(r)
    setSettings(s)
    setContextFailed(failed)
    setContextLoaded(true)
  }, [])

  const applyLogin = useCallback(
    (res: LoginResponse) => {
      tokenStore.set(res.token)
      setUser(res.user)
      setMustChange(res.mustChangePassword)
      // Everyone starts outside any "viewing as" session; system administrators land on their overview.
      groupStore.set(null)
      setGroupId(null)
    },
    [],
  )

  const login = useCallback(
    async (username: string, password: string) => {
      const res = await api.post<LoginResponse>('/auth/login', { username, password })
      applyLogin(res)
      return res
    },
    [applyLogin],
  )

  const selectGroup = useCallback((id: number | null) => {
    groupStore.set(id)
    setGroupId(id)
    // "Loading" is shown by the effect below once the group has really changed; choosing the same group again changes nothing.
    if (id === null) {
      setRestaurants([])
      setSettings(null)
    }
  }, [])
  const exitGroup = useCallback(() => selectGroup(null), [selectGroup])

  // Restore the session on page load.
  useEffect(() => {
    onAuthEvent((e) => {
      if (e === 'unauthorized') logout()
      if (e === 'password-change-required') setMustChange(true)
    })
    const token = tokenStore.get()
    if (!token) {
      setLoading(false)
      return
    }
    api
      .get<UserDto>('/auth/me')
      .then((u) => {
        if (u.role !== 'SuperAdmin' && groupStore.get() !== null) {
          groupStore.set(null)
          setGroupId(null)
        }
        setUser(u)
        setMustChange(u.mustChangePassword)
      })
      .catch(() => logout())
      .finally(() => setLoading(false))
    return () => onAuthEvent(null)
  }, [logout])

  // Load the restaurant / settings context whenever the user or selected group changes.
  useEffect(() => {
    if (!user || mustChangePassword) return
    if (user.role === 'SuperAdmin') void refreshGroups()
    // A different person or group: say "Loading" rather than show the last one's restaurants, or "not assigned"
  // before the answer is in. The same person checked again (the sign-in being confirmed) keeps what is on screen.
    const whose = `${user.id}:${groupId ?? ''}`
    if (contextFor.current !== whose) {
      contextFor.current = whose
      setContextLoaded(false)
    }
    void refreshContext()
  }, [user, groupId, mustChangePassword, refreshContext, refreshGroups])

  // Apply the group's accent colour.
  useEffect(() => {
    const color = settings?.accentColor || '#005daa'
    document.documentElement.style.setProperty('--accent', color)
  }, [settings?.accentColor])

  const value = useMemo<AuthState>(() => {
    const isGroupWide = user?.role === 'SuperAdmin' || user?.role === 'GroupAdmin'
    const isAdmin = isGroupWide || user?.role === 'Owner'
    return {
      user,
      loading,
      mustChangePassword,
      restaurants,
      contextLoaded,
      contextFailed,
      settings,
      groups,
      groupId,
      login,
      logout,
      applyLogin,
      selectGroup,
      refreshContext,
      refreshGroups,
      has: (p) => user?.role === 'SuperAdmin' || user?.role === 'GroupAdmin' || ((user?.role === 'Owner' || user?.role === 'Manager') && user.permissionNames.includes(p)),
      isAdmin,
      isSuperAdmin: user?.role === 'SuperAdmin',
      isGroupAdmin: user?.role === 'GroupAdmin',
      isGroupWide,
      isTeamMember: user?.role === 'TeamMember',
      viewingAs: user?.role === 'SuperAdmin' && groupId !== null,
      overseeing: user?.role === 'SuperAdmin' && groupId === null,
      exitGroup,
    }
  }, [user, loading, mustChangePassword, restaurants, contextLoaded, contextFailed, settings, groups, groupId, login, logout, applyLogin, selectGroup, exitGroup, refreshContext, refreshGroups])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
