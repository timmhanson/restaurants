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

function Guard({ allow, children }: { allow: boolean; children: React.ReactElement }) {
  return allow ? children : <Navigate to="/enter" replace />
}

export default function App() {
  const { user, loading, mustChangePassword, has, isAdmin, isSuperAdmin } = useAuth()
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
  const perm = (p: Permission) => has(p)
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<Navigate to="/enter" replace />} />
        <Route path="/login" element={<Navigate to="/enter" replace />} />
        <Route path="/enter" element={<EnterCardPage />} />
        <Route path="/password" element={<ChangePasswordPage />} />
        <Route path="/comments" element={<Guard allow={perm('ViewComments')}><CommentsPage /></Guard>} />
        <Route path="/analytics" element={<Guard allow={perm('ViewAnalytics')}><AnalyticsPage /></Guard>} />
        <Route path="/users" element={<Guard allow={isAdmin || perm('ManageTeamLogin')}><UsersPage /></Guard>} />
        <Route path="/restaurants" element={<Guard allow={isAdmin}><RestaurantsPage /></Guard>} />
        <Route path="/settings" element={<Guard allow={isAdmin}><SettingsPage /></Guard>} />
        <Route path="/audit" element={<Guard allow={perm('ViewAudit')}><AuditPage /></Guard>} />
        <Route path="/groups" element={<Guard allow={isSuperAdmin}><GroupsPage /></Guard>} />
        <Route path="*" element={<Navigate to="/enter" replace />} />
      </Route>
    </Routes>
  )
}
