import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './auth/AuthContext'
import Layout from './components/Layout'
import { Spinner } from './components/ui'
import AnalyticsPage from './pages/AnalyticsPage'
import AuditPage from './pages/AuditPage'
import ChangePasswordPage from './pages/ChangePasswordPage'
import CommentsPage from './pages/CommentsPage'
import EnterCardPage from './pages/EnterCardPage'
import GroupsPage from './pages/GroupsPage'
import LoginPage from './pages/LoginPage'
import RestaurantsPage from './pages/RestaurantsPage'
import SettingsPage from './pages/SettingsPage'
import UsersPage from './pages/UsersPage'
import type { Permission } from './api/types'

function Guard({ allow, home, children }: { allow: boolean; home: string; children: React.ReactElement }) {
  return allow ? children : <Navigate to={home} replace />
}

export default function App() {
  const { user, loading, mustChangePassword, has, isAdmin, isSuperAdmin, isGroupWide, overseeing } = useAuth()
  if (loading) return <Spinner label="Starting…" />
  if (!user) {
    return (
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="*" element={<Navigate to="/login" replace />} />
      </Routes>
    )
  }
  if (mustChangePassword) {
    return (
      <Routes>
        <Route path="/password" element={<ChangePasswordPage forced />} />
        <Route path="*" element={<Navigate to="/password" replace />} />
      </Routes>
    )
  }
  // Entering a card is the main job, so everyone lands there. A system administrator who is not viewing a
  // group lands on the group overview instead; the store pages open once they view a group.
  const home = overseeing ? '/groups' : '/enter'
  const inGroup = !overseeing
  const perm = (p: Permission) => has(p)
  return (
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
        <Route path="*" element={<Navigate to={home} replace />} />
      </Route>
    </Routes>
  )
}
