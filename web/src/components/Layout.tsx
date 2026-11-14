import { Fragment, useEffect, useRef, useState } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { roleTitle } from '../auth/roles'
import { track } from '../api/tracker'
import FollowUpNotice from './FollowUpNotice'

export default function Layout() {
  const { user, logout, has, isAdmin, isSuperAdmin, isGroupWide, isTeamMember, groups, groupId, settings, restaurants, viewingAs, overseeing, exitGroup } = useAuth()
  const [open, setOpen] = useState(false)
  const nav = useNavigate()
  const bar = useRef<HTMLDivElement>(null)

  // The topbar sticks just under the yellow bar, whose height changes when it wraps on a phone.
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
  // group exactly as its Investment Group Administrator would, with the yellow bar to get back out.
  // The System Administrator has a menu of their own, about the whole site. Restaurant work (cards, comments,
  // analytics) only appears while they view one group as its administrator, under the yellow bar.
  const links: { to: string; label: string; show: boolean; section?: string }[] = overseeing
    ? [
        { to: '/overview', label: 'Overview', show: true, section: 'Oversee' },
        { to: '/groups', label: 'Investment groups', show: true },
        { to: '/all-restaurants', label: 'Restaurants', show: true },
        { to: '/people', label: 'People', show: true },
        { to: '/reports', label: 'Reports', show: true, section: 'Insight' },
        { to: '/activity', label: 'Activity log', show: true },
        { to: '/audit', label: 'Audit log', show: true },
        { to: '/security', label: 'Security', show: true, section: 'System' },
        { to: '/connected-systems', label: 'Connected Systems', show: true },
        { to: '/system', label: 'System status', show: true },
      ]
    : [
        { to: '/enter', label: 'Enter card', show: true },
        { to: '/comments', label: 'Comments', show: has('ViewComments') },
        { to: '/print', label: 'Print cards', show: has('ViewComments') && has('MarkResponded') },
        { to: '/analytics', label: 'Analytics', show: has('ViewAnalytics') },
        { to: '/users', label: 'Users', show: isAdmin || has('ManageTeamLogin') },
        { to: '/restaurants', label: 'Restaurants & staff', show: isAdmin },
        { to: '/settings', label: 'Settings', show: isGroupWide },
        { to: '/audit', label: 'Audit log', show: has('ViewAudit') },
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
            Exit to System Administration
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
            <div className="brand-sub">{context || 'No restaurant assigned'}</div>
          </div>
        </div>
        <div className="user-menu">
          <span className="user-name">
            {user.displayName} <small>({viewingAs ? 'System Administrator, viewing as Investment Group Administrator' : roleTitle(user.role)})</small>
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
          .map((l) => (
            <Fragment key={l.to}>
              {l.section && <div className="nav-section">{l.section}</div>}
              <NavLink to={l.to} className={({ isActive }) => (isActive ? 'active' : '')}>
                {l.label}
              </NavLink>
            </Fragment>
          ))}
      </nav>
      <main className="content">
        <FollowUpNotice />
        <Outlet />
      </main>
    </div>
  )
}
