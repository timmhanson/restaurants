import { useState } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'

export default function Layout() {
  const { user, logout, has, isAdmin, isSuperAdmin, isTeamMember, groups, groupId, selectGroup, settings, restaurants } = useAuth()
  const [open, setOpen] = useState(false)
  const nav = useNavigate()
  if (!user) return null

  const links: { to: string; label: string; show: boolean }[] = [
    { to: '/enter', label: 'Enter card', show: true },
    { to: '/comments', label: 'Comments', show: has('ViewComments') },
    { to: '/analytics', label: 'Analytics', show: has('ViewAnalytics') },
    { to: '/users', label: 'Users', show: isAdmin || has('ManageTeamLogin') },
    { to: '/restaurants', label: 'Restaurants & staff', show: isAdmin },
    { to: '/settings', label: 'Settings', show: isAdmin },
    { to: '/audit', label: 'Audit log', show: has('ViewAudit') },
    { to: '/groups', label: 'Groups', show: isSuperAdmin },
  ]

  const groupName = isSuperAdmin ? groups.find((g) => g.id === groupId)?.name : user.groupName
  const context = isTeamMember
    ? restaurants.map((r) => r.name).join(', ')
    : [settings?.groupDisplayName || groupName, restaurants.length === 1 ? restaurants[0].name : restaurants.length ? `${restaurants.length} restaurants` : null]
        .filter(Boolean)
        .join(' · ')

  return (
    <div className="app">
      <header className="topbar">
        <button type="button" className="btn-icon nav-toggle" aria-label="Menu" onClick={() => setOpen((o) => !o)}>
          ☰
        </button>
        <div className="brand" onClick={() => nav('/enter')}>
          <span className="brand-mark" />
          <div>
            <div className="brand-title">Guest Feedback</div>
            <div className="brand-sub">{context || 'No restaurant assigned'}</div>
          </div>
        </div>
        {isSuperAdmin && (
          <label className="group-switch">
            <span>Group</span>
            <select value={groupId ?? ''} onChange={(e) => selectGroup(e.target.value ? Number(e.target.value) : null)}>
              <option value="">— none —</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name}
                  {g.isActive ? '' : ' (inactive)'}
                </option>
              ))}
            </select>
          </label>
        )}
        <div className="user-menu">
          <span className="user-name">
            {user.displayName} <small>({user.role === 'TeamMember' ? 'Team' : user.role === 'SuperAdmin' ? 'Super admin' : user.role})</small>
          </span>
          <NavLink to="/password" className="btn btn-sm">
            Password
          </NavLink>
          <button type="button" className="btn btn-sm" onClick={logout}>
            Sign out
          </button>
        </div>
      </header>
      <nav className={`sidenav ${open ? 'open' : ''}`} onClick={() => setOpen(false)}>
        {links
          .filter((l) => l.show)
          .map((l) => (
            <NavLink key={l.to} to={l.to} className={({ isActive }) => (isActive ? 'active' : '')}>
              {l.label}
            </NavLink>
          ))}
      </nav>
      <main className="content">
        <Outlet />
      </main>
    </div>
  )
}
