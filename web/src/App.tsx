import { useEffect } from 'react'
import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { isNewVersionReady } from './api/client'
import { flush, installTracking, track } from './api/tracker'
import { useAuth } from './auth/AuthContext'
import Layout from './components/Layout'
import { Spinner } from './components/ui'
import AnalyticsPage from './pages/AnalyticsPage'
import ActivityPage from './pages/ActivityPage'
import AllRestaurantsPage from './pages/AllRestaurantsPage'
import AuditPage from './pages/AuditPage'
import ChangePasswordPage from './pages/ChangePasswordPage'
import CommentsPage from './pages/CommentsPage'
import ConnectedSystemsPage from './pages/ConnectedSystemsPage'
import EnterCardPage from './pages/EnterCardPage'
import GroupsPage from './pages/GroupsPage'
import LoginPage from './pages/LoginPage'
import OverviewPage from './pages/OverviewPage'
import PeoplePage from './pages/PeoplePage'
import BillingPage from './pages/BillingPage'
import CardDesignPage from './pages/CardDesignPage'
import MailCardsPage from './pages/MailCardsPage'
import VerifyCardPage from './pages/VerifyCardPage'
import SystemBillingPage from './pages/SystemBillingPage'
import PrintCardsPage from './pages/PrintCardsPage'
import PrintCenterPage from './pages/PrintCenterPage'
import PrintTestPage from './pages/PrintTestPage'
import ReportsPage from './pages/ReportsPage'
import RestaurantsPage from './pages/RestaurantsPage'
import SecurityPage from './pages/SecurityPage'
import PermissionsPage from './pages/PermissionsPage'
import SettingsPage from './pages/SettingsPage'
import SystemStatusPage from './pages/SystemStatusPage'
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

/** Where to go once signed in: the scanned card someone was sent to sign in for. Kept for this browser tab only. */
export const RETURN_TO = 'gf.returnTo'

/** A scanned card's address, opened by someone not signed in: remember it, sign in, then come straight back. */
function SignInFirst() {
  const location = useLocation()
  try {
    sessionStorage.setItem(RETURN_TO, location.pathname)
  } catch {
    // Without storage the person lands on their home page after signing in and can scan the card again.
  }
  return <Navigate to="/login" replace />
}

/**
 * Home after signing in, unless a scanned card is waiting: then straight to that card. Only ever a card's own
 * address on this site, so nothing else can be slipped in.
 */
function AfterSignIn({ home }: { home: string }) {
  let back: string | null = null
  try {
    back = sessionStorage.getItem(RETURN_TO)
  } catch {
    back = null
  }
  useEffect(() => {
    try {
      sessionStorage.removeItem(RETURN_TO)
    } catch {
      // Nothing to clear.
    }
  }, [])
  return <Navigate to={back && /^\/v\/[\w-]{1,64}$/.test(back) ? back : home} replace />
}

export default function App() {
  const { user, loading, mustChangePassword, has, isAdmin, isGroupWide, overseeing } = useAuth()
  if (loading) return <Spinner label="Starting…" />
  if (!user) {
    return (
      <>
        <Tracking signedIn={false} />
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/v/:code" element={<SignInFirst />} />
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
  const home = overseeing ? '/overview' : '/enter'
  const inGroup = !overseeing
  const perm = (p: Permission) => has(p)
  return (
    <>
    <Tracking signedIn />
    <Routes>
      {/* Outside the usual frame so that only the cards themselves reach the printer. */}
      <Route path="/print-cards" element={<Guard home={home} allow={inGroup && perm('MarkResponded')}><PrintCardsPage /></Guard>} />
      <Route path="/print-test" element={<Guard home={home} allow={inGroup && isGroupWide}><PrintTestPage /></Guard>} />
      <Route element={<Layout />}>
        <Route index element={<AfterSignIn home={home} />} />
        <Route path="/login" element={<AfterSignIn home={home} />} />
        <Route path="/enter" element={<Guard home={home} allow={inGroup}><EnterCardPage /></Guard>} />
        <Route path="/password" element={<ChangePasswordPage />} />
        <Route path="/v/:code" element={<VerifyCardPage />} />
        <Route path="/print" element={<Guard home={home} allow={inGroup && perm('MarkResponded') && perm('ViewComments')}><PrintCenterPage /></Guard>} />
        <Route path="/mail-cards" element={<Guard home={home} allow={inGroup && perm('MarkResponded') && perm('ViewComments')}><MailCardsPage /></Guard>} />
        <Route path="/comments" element={<Guard home={home} allow={inGroup && perm('ViewComments')}><CommentsPage /></Guard>} />
        <Route path="/analytics" element={<Guard home={home} allow={inGroup && perm('ViewAnalytics')}><AnalyticsPage /></Guard>} />
        <Route path="/users" element={overseeing ? <Navigate to="/people" replace /> : <Guard home={home} allow={isAdmin || perm('ManageTeamLogin')}><UsersPage /></Guard>} />
        <Route path="/restaurants" element={<Guard home={home} allow={inGroup && isAdmin}><RestaurantsPage /></Guard>} />
        <Route path="/permissions" element={<Guard home={home} allow={inGroup && isGroupWide}><PermissionsPage /></Guard>} />
        <Route path="/settings" element={<Guard home={home} allow={inGroup && isGroupWide}><SettingsPage /></Guard>} />
        <Route path="/card-design" element={<Guard home={home} allow={inGroup && isAdmin}><CardDesignPage /></Guard>} />
        <Route path="/billing" element={<Guard home={home} allow={inGroup && isAdmin}><BillingPage /></Guard>} />
        <Route path="/system-billing" element={<Guard home={home} allow={overseeing}><SystemBillingPage /></Guard>} />
        <Route path="/audit" element={<Guard home={home} allow={perm('ViewAudit')}><AuditPage /></Guard>} />
        <Route path="/groups" element={<Guard home={home} allow={overseeing}><GroupsPage /></Guard>} />
        {/* System Administrator only: their own area about the whole site, never restaurant-level screens. */}
        <Route path="/overview" element={<Guard home={home} allow={overseeing}><OverviewPage /></Guard>} />
        <Route path="/all-restaurants" element={<Guard home={home} allow={overseeing}><AllRestaurantsPage /></Guard>} />
        <Route path="/people" element={<Guard home={home} allow={overseeing}><PeoplePage /></Guard>} />
        <Route path="/security" element={<Guard home={home} allow={overseeing}><SecurityPage /></Guard>} />
        <Route path="/connected-systems" element={<Guard home={home} allow={overseeing}><ConnectedSystemsPage /></Guard>} />
        <Route path="/system" element={<Guard home={home} allow={overseeing}><SystemStatusPage /></Guard>} />
        <Route path="/reports" element={<Guard home={home} allow={overseeing}><ReportsPage /></Guard>} />
        <Route path="/activity" element={<Guard home={home} allow={overseeing}><ActivityPage /></Guard>} />
        <Route path="*" element={<Navigate to={home} replace />} />
      </Route>
    </Routes>
    </>
  )
}
