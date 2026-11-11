import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { api, groupStore, onAuthEvent, tokenStore } from '../api/client'
import type { GroupDto, GroupSettings, LoginResponse, Permission, RestaurantDto, UserDto } from '../api/types'

interface AuthState {
  user: UserDto | null
  loading: boolean
  mustChangePassword: boolean
  restaurants: RestaurantDto[]
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
  isAdmin: boolean
  isSuperAdmin: boolean
  isTeamMember: boolean
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
  requirePhoneOrAddress: true,
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [mustChangePassword, setMustChange] = useState(false)
  const [restaurants, setRestaurants] = useState<RestaurantDto[]>([])
  const [settings, setSettings] = useState<GroupSettings | null>(null)
  const [groups, setGroups] = useState<GroupDto[]>([])
  const [groupId, setGroupId] = useState<number | null>(groupStore.get())

  const logout = useCallback(() => {
    tokenStore.set(null)
    setUser(null)
    setRestaurants([])
    setSettings(null)
    setGroups([])
    setMustChange(false)
  }, [])

  const refreshGroups = useCallback(async () => {
    try {
      const g = await api.get<GroupDto[]>('/groups')
      setGroups(g)
      // Default to the first active group so the super admin lands somewhere useful.
      if (!groupStore.get() && g.length) {
        const first = g.find((x) => x.isActive) ?? g[0]
        groupStore.set(first.id)
        setGroupId(first.id)
      }
    } catch {
      setGroups([])
    }
  }, [])

  const refreshContext = useCallback(async () => {
    const [r, s] = await Promise.all([
      api.get<RestaurantDto[]>('/restaurants').catch(() => [] as RestaurantDto[]),
      api.get<GroupSettings>('/settings').catch(() => defaultSettings),
    ])
    setRestaurants(r)
    setSettings(s)
  }, [])

  const applyLogin = useCallback(
    (res: LoginResponse) => {
      tokenStore.set(res.token)
      setUser(res.user)
      setMustChange(res.mustChangePassword)
      if (res.user.role !== 'SuperAdmin') {
        groupStore.set(null)
        setGroupId(null)
      }
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
  }, [])

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
    void refreshContext()
  }, [user, groupId, mustChangePassword, refreshContext, refreshGroups])

  // Apply the group's accent colour.
  useEffect(() => {
    const color = settings?.accentColor || '#005daa'
    document.documentElement.style.setProperty('--accent', color)
  }, [settings?.accentColor])

  const value = useMemo<AuthState>(() => {
    const isAdmin = user?.role === 'Owner' || user?.role === 'SuperAdmin'
    return {
      user,
      loading,
      mustChangePassword,
      restaurants,
      settings,
      groups,
      groupId,
      login,
      logout,
      applyLogin,
      selectGroup,
      refreshContext,
      refreshGroups,
      has: (p) => isAdmin || (user?.role === 'Manager' && user.permissionNames.includes(p)),
      isAdmin,
      isSuperAdmin: user?.role === 'SuperAdmin',
      isTeamMember: user?.role === 'TeamMember',
    }
  }, [user, loading, mustChangePassword, restaurants, settings, groups, groupId, login, logout, applyLogin, selectGroup, refreshContext, refreshGroups])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
