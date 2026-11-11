import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { Permission, PermissionInfo, RestaurantDto, Role, UserDto, UserRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Confirm, Empty, Field, Modal, Spinner, fmtDateTime } from '../components/ui'

const ROLE_HELP: Record<Role, string> = {
  SuperAdmin: 'Administers every investment group. Not tied to a restaurant.',
  Owner: 'Sees everything for their restaurants, manages users, restaurants and settings.',
  Manager: 'Enters cards for their restaurants; owners pick which other features they can use.',
  TeamMember: 'One shared login per restaurant for crew members. Can only enter cards; sees nothing else.',
}

export default function UsersPage() {
  const { user: me, isAdmin, isSuperAdmin, groupId } = useAuth()
  const [users, setUsers] = useState<UserDto[] | null>(null)
  const [restaurants, setRestaurants] = useState<RestaurantDto[]>([])
  const [catalog, setCatalog] = useState<PermissionInfo[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [editing, setEditing] = useState<UserDto | 'new' | null>(null)
  const [resetting, setResetting] = useState<UserDto | null>(null)
  const [removing, setRemoving] = useState<UserDto | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      const [u, r, p] = await Promise.all([
        api.get<UserDto[]>('/users'),
        api.get<RestaurantDto[]>(isAdmin ? '/restaurants?all=true' : '/restaurants'),
        api.get<{ permissions: PermissionInfo[] }>('/users/permissions'),
      ])
      setUsers(u)
      setRestaurants(r)
      setCatalog(p.permissions)
    } catch (e) {
      setError((e as Error).message)
      setUsers([])
    }
  }, [isAdmin])

  useEffect(() => {
    void load()
  }, [load, groupId])

  const restaurantName = (id: number) => restaurants.find((r) => r.id === id)?.name ?? `#${id}`
  // Owners manage people at restaurants they own (mirrors the API rules); super admins and managers are scoped by the API list.
  const ownerScoped = me?.role === 'Owner'
  const owns = (id: number) => restaurants.some((r) => r.id === id && r.canManage)
  const canEdit = (u: UserDto) => !ownerScoped || u.id === me?.id || u.restaurantIds.length === 0 || u.restaurantIds.some(owns)
  const canRemove = (u: UserDto) => canEdit(u) && (!ownerScoped || u.restaurantIds.every(owns))

  const remove = async () => {
    if (!removing) return
    setBusy(true)
    try {
      await api.delete(`/users/${removing.id}`)
      setNotice(`${removing.displayName} was removed.`)
      setRemoving(null)
      await load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>Users</h1>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>
          + Add user
        </button>
      </div>
      {!isAdmin && <Alert kind="info">As a manager you can create and reset the shared team-member login for your restaurants.</Alert>}
      {isSuperAdmin && !groupId && <Alert kind="warn">Select an investment group in the top bar to manage its owners, managers and team logins.</Alert>}
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {users === null ? (
        <Spinner />
      ) : users.length === 0 ? (
        <Empty>No users yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>User</th><th>Role</th><th>Restaurants</th><th>Can do</th><th>Last login</th><th></th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id} className={u.isActive ? '' : 'inactive'}>
                  <td>
                    <strong>{u.displayName}</strong> {u.id === me?.id && <Badge kind="accent">you</Badge>}{' '}
                    {!u.isActive && <Badge kind="neutral">inactive</Badge>} {u.mustChangePassword && <Badge kind="warn" title="Must pick a new password at next login">password reset</Badge>}
                    <div className="muted" style={{ fontSize: '0.8rem' }}>{u.username}{u.email ? ` · ${u.email}` : ''}</div>
                  </td>
                  <td>
                    {u.role === 'TeamMember' ? <Badge kind="neutral">Team login</Badge> : u.role === 'SuperAdmin' ? <Badge kind="accent">Super admin</Badge> : u.role}
                    {u.role === 'SuperAdmin' && u.groupName && <div className="muted">{u.groupName}</div>}
                  </td>
                  <td>{u.role === 'SuperAdmin' ? <span className="muted">all</span> : u.restaurantIds.length ? u.restaurantIds.map(restaurantName).join(', ') : <span className="muted">none</span>}</td>
                  <td style={{ maxWidth: 280 }}>
                    {u.role === 'Manager' ? (
                      u.permissionNames.length ? <span style={{ fontSize: '0.82rem' }}>{u.permissionNames.map(pretty).join(', ')}</span> : <span className="muted">Enter cards only</span>
                    ) : u.role === 'TeamMember' ? (
                      <span className="muted">Enter cards only</span>
                    ) : (
                      <span className="muted">Everything</span>
                    )}
                  </td>
                  <td>{u.lastLoginAt ? fmtDateTime(u.lastLoginAt) : <span className="muted">never</span>}</td>
                  <td>
                    {canEdit(u) ? (
                      <div className="row-actions">
                        <button type="button" className="btn btn-sm" onClick={() => setEditing(u)}>Edit</button>
                        <button type="button" className="btn btn-sm" onClick={() => setResetting(u)}>Reset password</button>
                        {u.id !== me?.id && canRemove(u) && (
                          <button type="button" className="btn btn-sm btn-danger" onClick={() => setRemoving(u)}>Remove</button>
                        )}
                      </div>
                    ) : (
                      <span className="muted" style={{ fontSize: '0.8rem' }}>Managed by the owners of {u.restaurantIds.map(restaurantName).join(', ')}</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {editing && (
        <UserModal
          user={editing === 'new' ? null : editing}
          restaurants={restaurants.filter((r) => r.isActive || (editing !== 'new' && editing.restaurantIds.includes(r.id)))}
          catalog={catalog}
          canPickRole={isAdmin}
          canPickSuperAdmin={isSuperAdmin}
          ownerScoped={me?.role === 'Owner'}
          meId={me?.id}
          onClose={() => setEditing(null)}
          onSaved={(msg) => {
            setEditing(null)
            setNotice(msg)
            void load()
          }}
        />
      )}
      {resetting && (
        <ResetPasswordModal
          user={resetting}
          onClose={() => setResetting(null)}
          onSaved={() => {
            setNotice(
              resetting.role === 'TeamMember'
                ? `Password for the team login "${resetting.displayName}" was changed. Share it with the crew.`
                : `${resetting.displayName} will be asked to choose a new password at their next login.`,
            )
            setResetting(null)
            void load()
          }}
        />
      )}
      {removing && (
        <Confirm
          title="Remove user?"
          danger
          busy={busy}
          confirmLabel="Remove"
          message={`Remove ${removing.displayName} (${removing.username})? They will no longer be able to sign in. Their past entries and audit history are kept.`}
          onCancel={() => setRemoving(null)}
          onConfirm={() => void remove()}
        />
      )}
    </>
  )
}

const pretty = (p: string) => p.replace(/([A-Z])/g, ' $1').trim()

function UserModal({ user, restaurants, catalog, canPickRole, canPickSuperAdmin, ownerScoped, meId, onClose, onSaved }: {
  user: UserDto | null
  restaurants: RestaurantDto[]
  catalog: PermissionInfo[]
  canPickRole: boolean
  canPickSuperAdmin: boolean
  /** Owners may only assign people to restaurants they own (their own account excepted). */
  ownerScoped: boolean
  meId?: number
  onClose: () => void
  onSaved: (message: string) => void
}) {
  const [v, setV] = useState<UserRequest>({
    username: user?.username ?? '',
    displayName: user?.displayName ?? '',
    email: user?.email ?? '',
    password: '',
    role: user?.role ?? (canPickRole ? 'Manager' : 'TeamMember'),
    permissions: user?.role === 'Manager' ? user.permissionNames : ['ViewComments', 'MarkResponded'],
    restaurantIds: user?.restaurantIds ?? (lockable(restaurants).length === 1 ? [lockable(restaurants)[0].id] : []),
    isActive: user?.isActive ?? true,
  })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Restaurants this editor may tick or untick for this user.
  function lockable(list: RestaurantDto[]) {
    return list.filter((r) => !ownerScoped || r.canManage || (user !== null && user.id === meId))
  }
  const editable = new Set(lockable(restaurants).map((r) => r.id))

  const togglePerm = (p: Permission) =>
    setV((s) => ({ ...s, permissions: s.permissions.includes(p) ? s.permissions.filter((x) => x !== p) : [...s.permissions, p] }))
  const toggleRestaurant = (id: number) =>
    setV((s) => ({ ...s, restaurantIds: s.restaurantIds.includes(id) ? s.restaurantIds.filter((x) => x !== id) : [...s.restaurantIds, id] }))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (!user && !v.password) return setError('A password is required for a new user.')
    if (v.role !== 'SuperAdmin' && v.restaurantIds.length === 0) return setError('Pick at least one restaurant.')
    setBusy(true)
    try {
      const body: UserRequest = { ...v, email: v.email || null, password: v.password || null }
      if (user) await api.put(`/users/${user.id}`, body)
      else await api.post('/users', body)
      onSaved(
        user
          ? `${v.displayName} was updated.${v.password && user.role !== 'TeamMember' ? ' They must choose a new password at next login.' : ''}`
          : v.role === 'TeamMember'
            ? `Team login "${v.username}" created. Share the username and password with the crew at the restaurant.`
            : `${v.displayName} was created. They will be asked to choose a new password when they first sign in.`,
      )
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const roles: Role[] = canPickRole ? (canPickSuperAdmin ? ['Owner', 'Manager', 'TeamMember', 'SuperAdmin'] : ['Owner', 'Manager', 'TeamMember']) : ['TeamMember']
  const roleLocked = !!user && (user.role === 'SuperAdmin' || !canPickRole)

  return (
    <Modal title={user ? `Edit ${user.displayName}` : 'Add user'} onClose={onClose} wide>
      <form onSubmit={submit} className="grid">
        {error && <Alert kind="error">{error}</Alert>}
        <div className="form-row">
          <Field label="Role">
            <select value={v.role} onChange={(e) => setV((s) => ({ ...s, role: e.target.value as Role }))} disabled={roleLocked}>
              {roles.map((r) => (
                <option key={r} value={r}>{r === 'TeamMember' ? 'Team member (shared login)' : r === 'SuperAdmin' ? 'Super admin' : r}</option>
              ))}
              {roleLocked && !roles.includes(v.role) && <option value={v.role}>{v.role}</option>}
            </select>
          </Field>
          <Field label="Active">
            <select value={v.isActive ? '1' : '0'} onChange={(e) => setV((s) => ({ ...s, isActive: e.target.value === '1' }))}>
              <option value="1">Active</option><option value="0">Inactive</option>
            </select>
          </Field>
        </div>
        <p className="muted" style={{ margin: 0 }}>{ROLE_HELP[v.role]}</p>
        <div className="form-row">
          <Field label="Username" hint={v.role === 'TeamMember' ? 'e.g. team-joliet' : 'Email address or short name'}>
            <input value={v.username} onChange={(e) => setV((s) => ({ ...s, username: e.target.value }))} required minLength={3} autoComplete="off" />
          </Field>
          <Field label="Display name" hint={v.role === 'TeamMember' ? 'e.g. Joliet crew' : undefined}>
            <input value={v.displayName} onChange={(e) => setV((s) => ({ ...s, displayName: e.target.value }))} required />
          </Field>
        </div>
        <div className="form-row">
          <Field label="Email (optional)">
            <input type="email" value={v.email ?? ''} onChange={(e) => setV((s) => ({ ...s, email: e.target.value }))} autoComplete="off" />
          </Field>
          <Field label={user ? 'New password (leave blank to keep)' : 'Password'} hint={v.role === 'TeamMember' ? 'Shared with the crew; they will not be asked to change it.' : 'They must change it at their first login.'}>
            <input type="password" value={v.password ?? ''} onChange={(e) => setV((s) => ({ ...s, password: e.target.value }))} minLength={8} autoComplete="new-password" required={!user} />
          </Field>
        </div>
        {v.role !== 'SuperAdmin' && (
          <Field
            label="Restaurants"
            hint={
              (v.role === 'TeamMember' ? 'Usually one restaurant per team login.' : 'Owners and managers can belong to several restaurants in the group.') +
              (editable.size < restaurants.length ? ' Restaurants you do not own are greyed out; their owners manage who works there.' : '')
            }
          >
            <div className="check-list" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))' }}>
              {restaurants.map((r) => (
                <label key={r.id} className="check" style={editable.has(r.id) ? undefined : { opacity: 0.55 }}>
                  <input type="checkbox" checked={v.restaurantIds.includes(r.id)} disabled={!editable.has(r.id)} onChange={() => toggleRestaurant(r.id)} /> {r.name}
                  {r.isActive ? '' : ' (inactive)'}
                </label>
              ))}
              {restaurants.length === 0 && <span className="muted">No restaurants in this group yet.</span>}
            </div>
          </Field>
        )}
        {v.role === 'Manager' && (
          <Field label="Manager permissions" hint="Every manager can enter cards. Tick what else this manager may do.">
            <div className="check-list">
              {catalog.map((p) => (
                <label key={p.name} className="check">
                  <input type="checkbox" checked={v.permissions.includes(p.name)} onChange={() => togglePerm(p.name)} />
                  <span><strong>{pretty(p.name)}</strong> <span className="muted">— {p.description}</span></span>
                </label>
              ))}
            </div>
          </Field>
        )}
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : user ? 'Save' : 'Create user'}</button>
        </div>
      </form>
    </Modal>
  )
}

function ResetPasswordModal({ user, onClose, onSaved }: { user: UserDto; onClose: () => void; onSaved: () => void }) {
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await api.put(`/users/${user.id}/password`, { password })
      onSaved()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <Modal title={`Reset password · ${user.displayName}`} onClose={onClose}>
      <form onSubmit={submit} className="grid">
        {error && <Alert kind="error">{error}</Alert>}
        <p className="muted">
          {user.role === 'TeamMember'
            ? 'This is the shared crew login. The new password takes effect immediately and the crew will not be asked to change it.'
            : 'The user will be asked to choose their own password the next time they sign in.'}
        </p>
        <Field label="Temporary password" hint="At least 8 characters.">
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} minLength={8} required autoComplete="new-password" autoFocus />
        </Field>
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : 'Set password'}</button>
        </div>
      </form>
    </Modal>
  )
}
