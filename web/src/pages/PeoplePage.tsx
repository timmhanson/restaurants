import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { Role, SystemPerson, UserDto, UserRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { ROLE_TITLES } from '../auth/roles'
import { useViewAs } from '../auth/useViewAs'
import DataTable from '../components/DataTable'
import { Alert, Badge, Confirm, Field, Modal, Spinner, fmtDateTime } from '../components/ui'
import { ResetPasswordModal } from './UsersPage'

const STATUSES = [
  { value: '', label: 'Everyone' },
  { value: 'active', label: 'Can sign in' },
  { value: 'never', label: 'Never signed in' },
  { value: 'temporary', label: 'Temporary password' },
  { value: 'suspended', label: 'Suspended' },
] as const

/**
 * Everyone with access, across every investment group, for the System Administrator: who they are, where they
 * belong, when they were last seen, and the account actions that matter at this level (reset a password, suspend
 * or restore, and manage the System Administrators). Roles and restaurant assignments are changed inside the
 * person's own group ("Open in group").
 */
export default function PeoplePage() {
  const { user: me } = useAuth()
  const viewAs = useViewAs()
  const [params, setParams] = useSearchParams()
  const [people, setPeople] = useState<SystemPerson[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [resetting, setResetting] = useState<UserDto | null>(null)
  const [suspending, setSuspending] = useState<UserDto | null>(null)
  const [editing, setEditing] = useState<UserDto | 'new' | null>(null)
  const [busy, setBusy] = useState(false)

  const f = { status: params.get('status') ?? '', role: params.get('role') ?? '', group: params.get('group') ?? '', q: params.get('q') ?? '' }
  const set = (k: keyof typeof f, v: string) => {
    const next = new URLSearchParams(params)
    if (v) next.set(k, v)
    else next.delete(k)
    setParams(next, { replace: true })
  }

  const load = useCallback(async () => {
    try {
      setPeople(await api.get<SystemPerson[]>('/system/people'))
    } catch (e) {
      setError((e as Error).message)
      setPeople([])
    }
  }, [])
  useEffect(() => {
    void load()
  }, [load])

  const groups = useMemo(
    () => [...new Map((people ?? []).filter((p) => p.user.groupId).map((p) => [p.user.groupId!, p.user.groupName ?? ''])).entries()].sort((a, b) => a[1].localeCompare(b[1])),
    [people],
  )
  const shown = useMemo(() => {
    const q = f.q.trim().toLowerCase()
    return (people ?? []).filter(({ user: u, restaurants }) => {
      if (f.role && u.role !== f.role) return false
      if (f.group && (f.group === 'none' ? u.groupId != null : String(u.groupId ?? '') !== f.group)) return false
      if (f.status === 'active' && !u.isActive) return false
      if (f.status === 'suspended' && u.isActive) return false
      if (f.status === 'never' && (u.lastLoginAt || !u.isActive)) return false
      if (f.status === 'temporary' && (!u.mustChangePassword || !u.isActive)) return false
      return !q || `${u.displayName} ${u.username} ${u.email ?? ''} ${u.groupName ?? ''} ${restaurants.join(' ')}`.toLowerCase().includes(q)
    })
  }, [people, f.q, f.role, f.group, f.status])

  const toggle = async (u: UserDto) => {
    setBusy(true)
    try {
      await api.put(`/users/${u.id}/status`, { active: !u.isActive })
      setNotice(u.isActive ? `${u.displayName} is suspended and can no longer sign in.` : `${u.displayName} can sign in again.`)
      setSuspending(null)
      await load()
    } catch (e) {
      setError((e as Error).message)
      setSuspending(null)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>People</h1>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>+ Add System Administrator</button>
      </div>
      <p className="muted">
        Everyone with access to the site, in every investment group. Here you can reset a password, suspend or restore an account, and manage the
        System Administrators. To change someone's role or restaurants, use <strong>Open in group</strong>.
      </p>
      <div className="filters">
        <label className="field">
          <span className="field-label">Investment group</span>
          <select value={f.group} onChange={(e) => set('group', e.target.value)}>
            <option value="">All groups</option>
            <option value="none">No group (System Administrators)</option>
            {groups.map(([id, name]) => (
              <option key={id} value={id}>{name}</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Role</span>
          <select value={f.role} onChange={(e) => set('role', e.target.value)}>
            <option value="">All roles</option>
            {(Object.keys(ROLE_TITLES) as Role[]).map((r) => (
              <option key={r} value={r}>{ROLE_TITLES[r]}</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Account</span>
          <select value={f.status} onChange={(e) => set('status', e.target.value)}>
            {STATUSES.map((s) => (
              <option key={s.value} value={s.value}>{s.label}</option>
            ))}
          </select>
        </label>
        <label className="field wide">
          <span className="field-label">Search</span>
          <input type="search" value={f.q} onChange={(e) => set('q', e.target.value)} placeholder="Name, username, email, group or restaurant…" />
        </label>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {people === null ? (
        <Spinner />
      ) : (
        <DataTable<SystemPerson>
          title={`${shown.length} ${shown.length === 1 ? 'person' : 'people'}`}
          rows={shown}
          rowKey={(p) => p.user.id}
          filename="people.csv"
          defaultSort={{ key: 'name', desc: false }}
          empty="Nobody matches these filters."
          columns={[
            {
              key: 'name', label: 'Person', value: (p) => p.user.displayName,
              render: ({ user: u }) => (
                <>
                  <strong>{u.displayName}</strong> {u.id === me?.id && <Badge kind="accent">you</Badge>}
                  <div className="muted small">{u.username}{u.email && u.email !== u.username ? ` · ${u.email}` : ''}</div>
                </>
              ),
            },
            { key: 'username', label: 'Username', value: (p) => p.user.username, csvOnly: true },
            { key: 'email', label: 'Email', value: (p) => p.user.email ?? null, csvOnly: true },
            { key: 'role', label: 'Role', value: (p) => p.user.roleTitle, render: (p) => (p.user.role === 'SuperAdmin' ? <Badge kind="accent">{p.user.roleTitle}</Badge> : p.user.roleTitle) },
            { key: 'group', label: 'Investment group', value: (p) => p.user.groupName ?? null, render: (p) => p.user.groupName ?? <span className="muted">whole site</span> },
            {
              key: 'restaurants', label: 'Restaurants', value: (p) => p.restaurants.join('; ') || null,
              render: (p) =>
                p.user.role === 'SuperAdmin' ? <span className="muted">none (oversees the site)</span>
                : p.user.role === 'GroupAdmin' ? <span className="muted">every restaurant in the group</span>
                : p.restaurants.length ? p.restaurants.join(', ') : <Badge kind="warn">none assigned</Badge>,
            },
            {
              key: 'status', label: 'Account', value: (p) => (!p.user.isActive ? 'Suspended' : p.user.mustChangePassword ? 'Temporary password' : !p.user.lastLoginAt ? 'Never signed in' : 'Active'),
              render: ({ user: u }) =>
                !u.isActive ? <Badge kind="danger">Suspended</Badge>
                : u.mustChangePassword ? <Badge kind="warn">Temporary password</Badge>
                : !u.lastLoginAt ? <Badge kind="warn">Never signed in</Badge>
                : <Badge kind="ok">Active</Badge>,
            },
            { key: 'login', label: 'Last sign-in', value: (p) => p.user.lastLoginAt ?? null, render: (p) => (p.user.lastLoginAt ? fmtDateTime(p.user.lastLoginAt) : <span className="muted">never</span>) },
            { key: 'seen', label: 'Last seen', value: (p) => p.lastSeen ?? null, render: (p) => (p.lastSeen ? fmtDateTime(p.lastSeen) : <span className="muted">—</span>) },
            { key: 'use', label: 'Actions in 30 days', num: true, value: (p) => p.interactions30d },
            {
              key: 'actions', label: '', noCsv: true, noSort: true, value: () => null,
              render: ({ user: u }) => (
                <div className="row-actions">
                  {u.role === 'SuperAdmin' ? (
                    <button type="button" className="btn btn-sm" onClick={() => setEditing(u)}>Edit</button>
                  ) : (
                    u.groupId != null && <button type="button" className="btn btn-sm" onClick={() => viewAs(u.groupId!, '/users')}>Open in group</button>
                  )}
                  {u.id !== me?.id && <button type="button" className="btn btn-sm" onClick={() => setResetting(u)}>Reset password</button>}
                  {u.id !== me?.id && (
                    <button type="button" className="btn btn-sm" onClick={() => setSuspending(u)}>{u.isActive ? 'Suspend' : 'Restore'}</button>
                  )}
                </div>
              ),
            },
          ]}
        />
      )}
      {resetting && (
        <ResetPasswordModal
          user={resetting}
          canWaive
          onClose={() => setResetting(null)}
          onSaved={(required) => {
            setNotice(required && resetting.role !== 'TeamMember' ? `${resetting.displayName} will be asked to choose a new password at their next sign-in.` : `${resetting.displayName}'s password was set.`)
            setResetting(null)
            void load()
          }}
        />
      )}
      {suspending && (
        <Confirm
          title={suspending.isActive ? `Suspend ${suspending.displayName}?` : `Restore ${suspending.displayName}?`}
          message={
            suspending.isActive
              ? `${suspending.displayName} will be signed out everywhere and cannot sign in until restored. Nothing they entered is removed.`
              : `${suspending.displayName} will be able to sign in again with their current password.`
          }
          confirmLabel={suspending.isActive ? 'Suspend' : 'Restore'}
          danger={suspending.isActive}
          busy={busy}
          onConfirm={() => void toggle(suspending)}
          onCancel={() => setSuspending(null)}
        />
      )}
      {editing && (
        <SystemAdminModal
          user={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={(msg) => {
            setEditing(null)
            setNotice(msg)
            void load()
          }}
        />
      )}
    </>
  )
}

/** Add another System Administrator, or change one's name, username and email. */
function SystemAdminModal({ user, onClose, onSaved }: { user: UserDto | null; onClose: () => void; onSaved: (message: string) => void }) {
  const [v, setV] = useState({ username: user?.username ?? '', displayName: user?.displayName ?? '', email: user?.email ?? '', password: '', requirePasswordChange: true })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const body: UserRequest = {
        username: v.username, displayName: v.displayName, email: v.email || null, password: v.password || null, role: 'SuperAdmin', permissions: [],
        restaurantIds: [], isActive: user?.isActive ?? true, requirePasswordChange: v.requirePasswordChange,
      }
      if (user) await api.put(`/users/${user.id}`, body)
      else await api.post('/users', body)
      onSaved(user ? `${v.displayName} was updated.` : `${v.displayName} is now a System Administrator${v.requirePasswordChange ? ' and will choose a new password at first sign-in' : ''}.`)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={user ? `Edit ${user.displayName}` : 'Add a System Administrator'} onClose={onClose}>
      <form onSubmit={submit} className="grid">
        {error && <Alert kind="error">{error}</Alert>}
        {!user && <p className="muted" style={{ margin: 0 }}>System Administrators oversee the whole site and every investment group. They are not part of any group or restaurant.</p>}
        <Field label="Name">
          <input value={v.displayName} onChange={(e) => setV((s) => ({ ...s, displayName: e.target.value }))} required autoFocus />
        </Field>
        <Field label="Username" hint="Usually their email address.">
          <input value={v.username} onChange={(e) => setV((s) => ({ ...s, username: e.target.value }))} required minLength={3} autoComplete="off" />
        </Field>
        <Field label="Email" hint="Password codes are sent here.">
          <input type="email" value={v.email} onChange={(e) => setV((s) => ({ ...s, email: e.target.value }))} autoComplete="off" />
        </Field>
        {!user && (
          <>
            <Field label="Password" hint="At least 8 characters.">
              <input type="password" value={v.password} onChange={(e) => setV((s) => ({ ...s, password: e.target.value }))} required minLength={8} autoComplete="new-password" />
            </Field>
            <label className="check">
              <input type="checkbox" checked={v.requirePasswordChange} onChange={(e) => setV((s) => ({ ...s, requirePasswordChange: e.target.checked }))} /> Require a new password at first sign-in
            </label>
          </>
        )}
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : user ? 'Save' : 'Add System Administrator'}</button>
        </div>
      </form>
    </Modal>
  )
}
