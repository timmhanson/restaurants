import { useEffect } from 'react'
import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { isNewVersionReady } from './api/client'
import { flush, installTracking, track } from './api/tracker'
import { useAuth } from './auth/AuthContext'
import Layout from './components/Layout'
import { Spinner } from './components/ui'
import AnalyticsPage from './pages/AnalyticsPage'
import ActivityPage from './pages/ActivityPage'
import AuditPage from './pages/AuditPage'
import ChangePasswordPage from './pages/ChangePasswordPage'
import CommentsPage from './pages/CommentsPage'
import EnterCardPage from './pages/EnterCardPage'
import GroupsPage from './pages/GroupsPage'
import LoginPage from './pages/LoginPage'
import ReportsPage from './pages/ReportsPage'
import RestaurantsPage from './pages/RestaurantsPage'
import SettingsPage from './pages/SettingsPage'
import UsersPage from './pages/UsersPage'
import type { Permission } from './api/types'

function Guard({ allow, home, children }: { allow: boolean; home: string; children: React.ReactElement }) {
  return allow ? children : <Navigate to={home} replace />
}

/**
 * Logs each page the signed-in user opens (clicks and choices are picked up by installTracking), and loads a
 * freshly deployed version of the site at the next page change instead of carrying on with the old one.
 */
function Tracking({ signedIn }: { signedIn: boolean }) {
  const location = useLocation()
  useEffect(() => installTracking(), [])
  useEffect(() => {
    if (signedIn) track('Page', undefined, undefined, location.pathname + location.search)
    if (isNewVersionReady()) {
      flush()
      window.location.reload()
    }
  }, [location.pathname, location.search, signedIn])
  return null
}

export default function App() {
  const { user, loading, mustChangePassword, has, isAdmin, isSuperAdmin, isGroupWide, overseeing } = useAuth()
  if (loading) return <Spinner label="Starting…" />
  if (!user) {
    return (
      <>
        <Tracking signedIn={false} />
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="*" element={<Navigate to="/login" replace />} />
        </Routes>
      </>
    )
  }
  if (mustChangePassword) {
    return (
      <>
        <Tracking signedIn />
        <Routes>
          <Route path="/password" element={<ChangePasswordPage forced />} />
          <Route path="*" element={<Navigate to="/password" replace />} />
        </Routes>
      </>
    )
  }
  // Entering a card is the main job, so everyone lands there. A system administrator who is not viewing a
  // group lands on the group overview instead; the store pages open once they view a group.
  const home = overseeing ? '/groups' : '/enter'
  const inGroup = !overseeing
  const perm = (p: Permission) => has(p)
  return (
    <>
    <Tracking signedIn />
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<Navigate to={home} replace />} />
        <Route path="/login" element={<Navigate to={home} replace />} />
        <Route path="/enter" element={<Guard home={home} allow={inGroup}><EnterCardPage /></Guard>} />
        <Route path="/password" element={<ChangePasswordPage />} />
        <Route path="/comments" element={<Guard home={home} allow={inGroup && perm('ViewComments')}><CommentsPage /></Guard>} />
        <Route path="/analytics" element={<Guard home={home} allow={inGroup && perm('ViewAnalytics')}><AnalyticsPage /></Guard>} />
        <Route path="/users" element={<Guard home={home} allow={isAdmin || perm('ManageTeamLogin')}><UsersPage /></Guard>} />
        <Route path="/restaurants" element={<Guard home={home} allow={inGroup && isAdmin}><RestaurantsPage /></Guard>} />
        <Route path="/settings" element={<Guard home={home} allow={inGroup && isGroupWide}><SettingsPage /></Guard>} />
        <Route path="/audit" element={<Guard home={home} allow={perm('ViewAudit')}><AuditPage /></Guard>} />
        <Route path="/groups" element={<Guard home={home} allow={isSuperAdmin}><GroupsPage /></Guard>} />
        {/* System Administrator only: the interaction log and cross-group reports. */}
        <Route path="/reports" element={<Guard home={home} allow={isSuperAdmin}><ReportsPage /></Guard>} />
        <Route path="/activity" element={<Guard home={home} allow={isSuperAdmin}><ActivityPage /></Guard>} />
        <Route path="*" element={<Navigate to={home} replace />} />
      </Route>
    </Routes>
    </>
  )
}
