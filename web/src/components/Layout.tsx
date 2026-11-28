import { Fragment, useEffect, useRef, useState } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { roleTitle } from '../auth/roles'
import { track } from '../api/tracker'
import FollowUpNotice, { useFollowUpSummary } from './FollowUpNotice'
import SetupNotice, { useOnboarding } from './SetupNotice'

export default function Layout() {
  const { user, logout, has, isAdmin, isSuperAdmin, isGroupWide, isTeamMember, groups, groupId, settings, restaurants, viewingAs, overseeing, exitGroup, contextLoaded } = useAuth()
  const [open, setOpen] = useState(false)
  const nav = useNavigate()
  const bar = useRef<HTMLDivElement>(null)

  // The topbar sticks just under the dark blue bar, whose height changes when it wraps on a phone.
  useEffect(() => {
    const el = bar.current
    const root = document.documentElement
    if (!el) {
      root.style.removeProperty('--viewing-bar-h')
      return
    }
    const ro = new ResizeObserver(() => root.style.setProperty('--viewing-bar-h', `${el.offsetHeight}px`))
    ro.observe(el)
    return () => ro.disconnect()
  }, [viewingAs])

  if (!user) return null

  // A system administrator either oversees every group (Groups, all users, the whole audit log) or views one
  // group exactly as its Investment Group Administrator would, with the dark blue bar to get back out.
  // The System Administrator has a menu of their own, about the whole site. Restaurant work (cards, comments,
  // analytics) only appears while they view one group as its administrator, under the dark blue bar.
  // What is waiting to be done shows as a red count beside its tab, as on a phone's home screen.
  const waiting = useFollowUpSummary()
  // A new group's administrators are welcomed and walked through set-up; it stays in their menu until it is finished.
  const setUp = useOnboarding()
  const links: { to: string; label: string; show: boolean; section?: string; count?: number }[] = overseeing
    ? [
        { to: '/overview', label: 'Overview', show: true, section: 'Oversee' },
        { to: '/groups', label: 'Investment Groups', show: true },
        { to: '/all-restaurants', label: 'Restaurants', show: true },
        { to: '/people', label: 'People', show: true },
        { to: '/reports', label: 'Reports', show: true, section: 'Insight' },
        { to: '/system-billing', label: 'Billing', show: true },
        { to: '/activity', label: 'Activity Log', show: true },
        { to: '/audit', label: 'Audit Log', show: true },
        { to: '/security', label: 'Security', show: true, section: 'System' },
        { to: '/connected-systems', label: 'Connected Systems', show: true },
        { to: '/system', label: 'System Status', show: true },
      ]
    : [
        { to: '/enter', label: 'Enter Card', show: true, section: 'Actions' },
        { to: '/print', label: 'Print Cards', show: has('ViewComments') && has('MarkResponded'), section: 'Actions', count: waiting.summary?.toSend },
        { to: '/follow-up', label: 'Follow Up', show: has('ViewComments'), section: 'Actions', count: waiting.summary?.pending },
        { to: '/comments', label: 'All Comments', show: has('ViewComments'), section: 'Reports' },
        { to: '/billing', label: 'Billing', show: isAdmin, section: 'Reports' },
        { to: '/analytics', label: 'Analytics', show: has('ViewAnalytics'), section: 'Reports' },
        { to: '/audit', label: 'Audit Log', show: has('ViewAudit'), section: 'Reports' },
        { to: '/restaurants', label: 'Restaurants & Staff', show: isAdmin, section: 'Staff' },
        { to: '/users', label: 'All Staff', show: isAdmin || has('ManageTeamLogin'), section: 'Staff' },
        { to: '/welcome', label: 'Getting Started', show: Boolean(setUp), section: 'Configuration' },
        { to: '/permissions', label: 'Permissions', show: isGroupWide, section: 'Configuration' },
        { to: '/settings', label: 'Settings', show: isGroupWide, section: 'Configuration' },
        { to: '/card-design', label: 'Card Design', show: isAdmin, section: 'Configuration' },
      ]

  const groupName = isSuperAdmin ? groups.find((g) => g.id === groupId)?.name : user.groupName
  const context = overseeing
    ? 'System administration'
    : isTeamMember
      ? restaurants.map((r) => r.name).join(', ')
      : [groupName || settings?.groupDisplayName, restaurants.length === 1 ? restaurants[0].name : restaurants.length ? `${restaurants.length} restaurants` : null]
          .filter(Boolean)
          .join(' · ')

  const exit = () => {
    exitGroup()
    nav('/overview')
  }

  return (
    <div className={`app ${viewingAs ? 'viewing-as' : ''} ${overseeing ? 'system' : ''}`}>
      {viewingAs && (
        <div className="viewing-bar" role="status" ref={bar}>
          <span>
            You are viewing <strong>{groupName ?? 'this group'}</strong> as an Investment Group Administrator. Everything you do here is real and is audited under
            your name.
          </span>
          <button type="button" className="btn btn-sm" onClick={exit}>
            Back to System Administration
          </button>
        </div>
      )}
      <header className="topbar">
        <button type="button" className="btn-icon nav-toggle" aria-label="Menu" onClick={() => setOpen((o) => !o)}>
          ☰
        </button>
        <div className="brand" onClick={() => nav(overseeing ? '/overview' : '/enter')}>
          <span className="brand-mark" />
          <div>
            <div className="brand-title">Guest Feedback</div>
            <div className="brand-sub">{context || (contextLoaded ? 'No restaurant assigned' : 'Loading…')}</div>
          </div>
        </div>
        <div className="user-menu">
          <span className="user-name">
            {user.displayName} <small>({viewingAs ? 'System Administrator, viewing as Investment Group Administrator' : user.alsoOwner ? user.roleTitle : roleTitle(user.role)})</small>
          </span>
          <NavLink to="/password" className="btn btn-sm">
            Password
          </NavLink>
          <button
            type="button"
            className="btn btn-sm"
            onClick={() => {
              track('SignOut')
              logout()
            }}
          >
            Sign out
          </button>
        </div>
      </header>
      <nav className={`sidenav ${open ? 'open' : ''}`} onClick={() => setOpen(false)}>
        {links
          .filter((l) => l.show)
          .map((l, i, shown) => (
            <Fragment key={l.to}>
              {l.section && (overseeing || shown[i - 1]?.section !== l.section) && <div className="nav-section">{l.section}</div>}
              <NavLink to={l.to} className={({ isActive }) => (isActive ? 'active' : '')} aria-label={l.count ? `${l.label}, ${l.count} waiting` : undefined}>
                {l.label}
                {l.count ? <span className="nav-count" aria-hidden="true">{l.count > 99 ? '99+' : l.count}</span> : null}
              </NavLink>
            </Fragment>
          ))}
      </nav>
      <main className="content">
        <SetupNotice state={setUp} />
        <FollowUpNotice summary={waiting.summary} reload={waiting.reload} />
        <Outlet />
      </main>
    </div>
  )
}
