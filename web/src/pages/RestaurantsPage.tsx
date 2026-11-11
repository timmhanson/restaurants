import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { Permission, PermissionInfo, RestaurantDto, RestaurantRequest, Role, StaffMember, StaffResponse, UserDto, UserRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Confirm, Empty, Field, Modal, Spinner } from '../components/ui'

const pretty = (p: string) => p.replace(/([A-Z])/g, ' $1').trim()
const address = (r: RestaurantDto) => [r.streetAddress, [r.city, r.state].filter(Boolean).join(', '), r.zip].filter(Boolean).join(' · ')

export default function RestaurantsPage() {
  const { user, refreshContext, isSuperAdmin, isGroupWide, groupId } = useAuth()
  const [list, setList] = useState<RestaurantDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [editing, setEditing] = useState<RestaurantDto | 'new' | null>(null)
  const [staffing, setStaffing] = useState<RestaurantDto | null>(null)
  const [deactivating, setDeactivating] = useState<RestaurantDto | null>(null)
  const [busy, setBusy] = useState(false)
  const needsGroup = isSuperAdmin && !groupId

  const load = useCallback(async () => {
    if (needsGroup) return setList([])
    try {
      setList(await api.get<RestaurantDto[]>('/restaurants?all=true'))
    } catch (e) {
      setError((e as Error).message)
      setList([])
    }
  }, [needsGroup])
  useEffect(() => {
    void load()
  }, [load, groupId])

  const changed = async (message?: string) => {
    if (message) setNotice(message)
    await load()
    await refreshContext()
  }

  const setOwnership = async (r: RestaurantDto, owned: boolean) => {
    setError(null)
    try {
      await api.put(`/restaurants/${r.id}/ownership`, { owned })
      await changed(owned ? `You now own ${r.name}. You can edit it and choose its managers.` : `You no longer own ${r.name}.`)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const deactivate = async () => {
    if (!deactivating) return
    setBusy(true)
    try {
      await api.delete(`/restaurants/${deactivating.id}`)
      setDeactivating(null)
      await changed(`${deactivating.name} was deactivated.`)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const mine = list?.filter((r) => r.ownedByMe) ?? []

  return (
    <>
      <div className="page-head">
        <h1>Restaurants &amp; staff</h1>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')} disabled={needsGroup}>+ Add restaurant</button>
      </div>
      {needsGroup ? (
        <Alert kind="warn">Pick an investment group in the top bar (or use "Set up" on the Groups page) to manage its restaurants.</Alert>
      ) : isGroupWide ? (
        <p className="muted">
          As {isSuperAdmin ? 'System Administrator' : 'Group Administrator'} you can edit and staff every restaurant in this group. Use <strong>Staff</strong> to
          choose each restaurant's owners, managers and team login. A restaurant with no specific owner is run by the group administrators.
        </p>
      ) : (
        <p className="muted">
          You manage the restaurants you own{mine.length ? ` (${mine.map((r) => r.name).join(', ')})` : ''}. Use <strong>Staff</strong> to choose who works at
          each of them, including co-owners. Your group administrators decide who owns the other restaurants.
        </p>
      )}
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {list === null ? (
        <Spinner />
      ) : list.length === 0 ? (
        needsGroup ? null : <Empty>No restaurants yet. Add the first one to start entering cards.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th>Restaurant</th><th>Owners</th><th className="num">Managers</th><th className="num">Team logins</th><th>Status</th><th></th></tr>
            </thead>
            <tbody>
              {list.map((r) => (
                <tr key={r.id} className={r.isActive ? '' : 'inactive'}>
                  <td>
                    <strong>{r.name}</strong> {r.ownedByMe && <Badge kind="accent">you own this</Badge>}
                    <div className="muted" style={{ fontSize: '0.8rem' }}>{address(r) || 'No address'}{r.phone ? ` · ${r.phone}` : ''}</div>
                  </td>
                  <td>
                    {r.owners?.length ? (
                      <div className="people">{r.owners.map((o) => <Badge key={o.id} kind={o.id === user?.id ? 'accent' : 'neutral'}>{o.displayName}</Badge>)}</div>
                    ) : (
                      <Badge kind="neutral" title="Run by the group administrators">No specific owner</Badge>
                    )}
                  </td>
                  <td className="num">{r.managerCount ?? 0}</td>
                  <td className="num">{r.teamLoginCount ?? 0}</td>
                  <td>{r.isActive ? <Badge kind="ok">Active</Badge> : <Badge kind="neutral">Inactive</Badge>}</td>
                  <td>
                    <div className="row-actions">
                      {r.canManage ? (
                        <>
                          <button type="button" className="btn btn-sm btn-primary" onClick={() => setStaffing(r)}>Staff</button>
                          <button type="button" className="btn btn-sm" onClick={() => setEditing(r)}>Edit</button>
                          {r.isActive && <button type="button" className="btn btn-sm btn-danger" onClick={() => setDeactivating(r)}>Deactivate</button>}
                        </>
                      ) : (
                        <button type="button" className="btn btn-sm" onClick={() => setStaffing(r)}>View staff</button>
                      )}
                      {user?.role === 'Owner' && r.ownedByMe && (
                        <button type="button" className="btn btn-sm" onClick={() => void setOwnership(r, false)}>Stop owning</button>
                      )}
                      {user?.role === 'Owner' && !r.ownedByMe && r.canClaim && (
                        <button type="button" className="btn btn-sm" onClick={() => void setOwnership(r, true)}>I own this</button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && (
        <RestaurantModal
          restaurant={editing === 'new' ? null : editing}
          isSuperAdmin={isGroupWide}
          onClose={() => setEditing(null)}
          onSaved={(saved, isNew) => {
            setEditing(null)
            void changed(isNew ? `${saved.name} was added.${isGroupWide ? ' Next, choose its owners and managers.' : ' You own it; choose its managers next.'}` : `${saved.name} was updated.`)
            if (isNew) setStaffing(saved)
          }}
        />
      )}
      {staffing && (
        <StaffModal
          restaurant={staffing}
          onClose={() => setStaffing(null)}
          onSaved={(msg) => {
            setStaffing(null)
            void changed(msg)
          }}
        />
      )}
      {deactivating && (
        <Confirm
          title="Deactivate restaurant?"
          danger
          busy={busy}
          confirmLabel="Deactivate"
          message={`Deactivate ${deactivating.name}? Its comments are kept, but nobody can enter new cards for it until it is reactivated from Edit.`}
          onCancel={() => setDeactivating(null)}
          onConfirm={() => void deactivate()}
        />
      )}
    </>
  )
}

/** Choose who works at one restaurant: owners, managers and the shared team login. */
function StaffModal({ restaurant, onClose, onSaved }: { restaurant: RestaurantDto; onClose: () => void; onSaved: (message: string) => void }) {
  const { user } = useAuth()
  const [data, setData] = useState<StaffResponse | null>(null)
  const [selected, setSelected] = useState<Set<number>>(new Set())
  const [catalog, setCatalog] = useState<PermissionInfo[]>([])
  const [adding, setAdding] = useState<Role | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  /** Loads the people list. The selection is taken from the server only on first load, so unsaved ticks survive a quick-add. */
  const load = useCallback(
    async (initial: boolean) => {
      const [staff, perms] = await Promise.all([
        api.get<StaffResponse>(`/restaurants/${restaurant.id}/staff`),
        api.get<{ permissions: PermissionInfo[] }>('/users/permissions'),
      ])
      setData(staff)
      setCatalog(perms.permissions)
      if (initial) setSelected(new Set([...staff.owners, ...staff.managers, ...staff.teamLogins].filter((m) => m.assigned).map((m) => m.id)))
    },
    [restaurant.id],
  )
  useEffect(() => {
    load(true).catch((e) => setError((e as Error).message))
  }, [load])

  const canManage = data?.canManage ?? false
  const toggle = (m: StaffMember) =>
    setSelected((s) => {
      const next = new Set(s)
      if (next.has(m.id)) next.delete(m.id)
      else next.add(m.id)
      return next
    })

  const save = async () => {
    if (!data) return
    setError(null)
    if (data.ownerRequired && !data.owners.some((o) => selected.has(o.id))) return setError('Every restaurant needs at least one owner. Tick an owner before saving.')
    setBusy(true)
    try {
      await api.put(`/restaurants/${restaurant.id}/staff`, { userIds: [...selected] })
      onSaved(`Staff for ${restaurant.name} was saved.`)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const section = (title: string, role: Role, members: StaffMember[], help: string, addLabel: string) => (
    <section className="staff-section">
      <h3>
        {title} <Badge kind="neutral">{members.filter((m) => selected.has(m.id)).length} assigned</Badge>
        <span className="grow" />
        {canManage && adding !== role && (
          <button type="button" className="btn btn-sm" onClick={() => setAdding(role)}>{addLabel}</button>
        )}
      </h3>
      <p className="muted" style={{ margin: 0, fontSize: '0.85rem' }}>{help}</p>
      {members.length === 0 ? (
        <p className="muted" style={{ fontSize: '0.85rem' }}>Nobody in this group yet.</p>
      ) : (
        <div className="staff-list">
          {members.map((m) => {
            const isMe = m.id === user?.id
            const locked = !canManage || (isMe && user?.role === 'Owner')
            return (
              <label key={m.id} className={`staff-item ${selected.has(m.id) ? 'on' : ''}`} title={isMe && user?.role === 'Owner' ? 'Use "Stop owning" on the restaurant list to remove yourself.' : undefined}>
                <input type="checkbox" checked={selected.has(m.id)} disabled={locked} onChange={() => toggle(m)} />
                <span>
                  <strong>{m.displayName}</strong> {isMe && <Badge kind="accent">you</Badge>} {!m.isActive && <Badge kind="neutral">inactive</Badge>}
                  <div className="sub">{m.username}{m.email && m.email !== m.username ? ` · ${m.email}` : ''}</div>
                  {role === 'Manager' && (
                    <div className="sub">{m.permissionNames.length ? m.permissionNames.map(pretty).join(', ') : 'Enter cards only'}</div>
                  )}
                  {m.otherRestaurants.length > 0 && <div className="sub">Also at {m.otherRestaurants.join(', ')}</div>}
                </span>
              </label>
            )
          })}
        </div>
      )}
      {adding === role && (
        <QuickAddUser
          role={role}
          restaurant={restaurant}
          catalog={catalog}
          onCancel={() => setAdding(null)}
          onCreated={async (u) => {
            setAdding(null)
            setSelected((s) => new Set([...s, u.id]))
            await load(false)
          }}
        />
      )}
    </section>
  )

  return (
    <Modal title={`Who works at ${restaurant.name}`} onClose={onClose} wide>
      {!data ? (
        error ? <Alert kind="error">{error}</Alert> : <Spinner />
      ) : (
        <div className="grid">
          {!canManage && <Alert kind="info">Only this restaurant's owners or a group administrator can change who works here.</Alert>}
          {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
          {data.groupAdmins.length > 0 && (
            <p className="muted" style={{ margin: 0 }}>
              Group administrators ({data.groupAdmins.map((a) => a.displayName).join(', ')}) see and manage every restaurant, so they are not listed below.
            </p>
          )}
          {section(
            'Owners',
            'Owner',
            data.owners,
            'Owners see everything for this restaurant, decide who works here, and can reset or suspend its managers and team login. ' +
              (data.ownerRequired ? 'Every restaurant needs at least one owner.' : 'Leave empty if the group administrators run it directly.'),
            '+ New owner',
          )}
          {section('Managers', 'Manager', data.managers, 'Managers can work at several restaurants, even ones with different owners. What each one may do is set on the Users page and applies everywhere they work.', '+ New manager')}
          {section('Team login', 'TeamMember', data.teamLogins, 'One shared login for the crew at this restaurant. It can only enter cards.', '+ New team login')}
          <div className="actions">
            <button type="button" className="btn" onClick={onClose} disabled={busy}>{canManage ? 'Cancel' : 'Close'}</button>
            {canManage && <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy}>{busy ? 'Saving…' : 'Save staff'}</button>}
          </div>
        </div>
      )}
    </Modal>
  )
}

/** Creates an owner, manager or team login that starts out assigned to this restaurant. */
function QuickAddUser({ role, restaurant, catalog, onCancel, onCreated }: {
  role: Role
  restaurant: RestaurantDto
  catalog: PermissionInfo[]
  onCancel: () => void
  onCreated: (u: UserDto) => void | Promise<void>
}) {
  const slug = restaurant.name.toLowerCase().replace(/culver'?s( of)?/g, '').replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')
  const [v, setV] = useState({
    displayName: role === 'TeamMember' ? `${restaurant.name} crew` : '',
    username: role === 'TeamMember' ? `team-${slug}` : '',
    email: '',
    password: '',
  })
  const [perms, setPerms] = useState<Permission[]>(['ViewComments', 'MarkResponded'])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const label = role === 'TeamMember' ? 'team login' : role.toLowerCase()

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    e.stopPropagation()
    setBusy(true)
    setError(null)
    try {
      const body: UserRequest = {
        username: v.username,
        displayName: v.displayName,
        email: v.email || null,
        password: v.password,
        role,
        permissions: role === 'Manager' ? perms : [],
        restaurantIds: [restaurant.id],
        isActive: true,
      }
      await onCreated(await api.post<UserDto>('/users', body))
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="quick-add" onSubmit={submit}>
      <strong>New {label} for {restaurant.name}</strong>
      {error && <Alert kind="error">{error}</Alert>}
      <div className="form-row">
        <Field label="Display name"><input value={v.displayName} onChange={(e) => setV({ ...v, displayName: e.target.value })} required autoFocus /></Field>
        <Field label="Username" hint={role === 'TeamMember' ? undefined : 'Email address or short name'}><input value={v.username} onChange={(e) => setV({ ...v, username: e.target.value })} required minLength={3} autoComplete="off" /></Field>
      </div>
      <div className="form-row">
        {role !== 'TeamMember' && (
          <Field label="Email" hint="Needed to change their own password."><input type="email" value={v.email} onChange={(e) => setV({ ...v, email: e.target.value })} autoComplete="off" /></Field>
        )}
        <Field label={role === 'TeamMember' ? 'Shared password' : 'Temporary password'} hint={role === 'TeamMember' ? 'Give this to the crew.' : 'They choose their own at first login.'}>
          <input type="password" value={v.password} onChange={(e) => setV({ ...v, password: e.target.value })} required minLength={8} autoComplete="new-password" />
        </Field>
      </div>
      {role === 'Manager' && (
        <Field label="This manager may also" hint="Every manager can enter cards.">
          <div className="check-list" style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))' }}>
            {catalog.map((p) => (
              <label key={p.name} className="check" title={p.description}>
                <input type="checkbox" checked={perms.includes(p.name)} onChange={() => setPerms((s) => (s.includes(p.name) ? s.filter((x) => x !== p.name) : [...s, p.name]))} />
                {pretty(p.name)}
              </label>
            ))}
          </div>
        </Field>
      )}
      <div className="actions" style={{ marginTop: 0 }}>
        <button type="button" className="btn btn-sm" onClick={onCancel} disabled={busy}>Cancel</button>
        <button type="submit" className="btn btn-sm btn-primary" disabled={busy}>{busy ? 'Creating…' : `Create ${label}`}</button>
      </div>
    </form>
  )
}

function RestaurantModal({ restaurant, isSuperAdmin, onClose, onSaved }: {
  restaurant: RestaurantDto | null
  isSuperAdmin: boolean
  onClose: () => void
  onSaved: (saved: RestaurantDto, isNew: boolean) => void
}) {
  const [v, setV] = useState<RestaurantRequest>({
    name: restaurant?.name ?? '',
    streetAddress: restaurant?.streetAddress ?? '',
    city: restaurant?.city ?? '',
    state: restaurant?.state ?? '',
    zip: restaurant?.zip ?? '',
    phone: restaurant?.phone ?? '',
    isActive: restaurant?.isActive ?? true,
  })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const set = (k: keyof RestaurantRequest, val: string | boolean) => setV((s) => ({ ...s, [k]: val }))
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const saved = restaurant ? await api.put<RestaurantDto>(`/restaurants/${restaurant.id}`, v) : await api.post<RestaurantDto>('/restaurants', v)
      onSaved(saved, !restaurant)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <Modal title={restaurant ? `Edit ${restaurant.name}` : 'Add restaurant'} onClose={onClose}>
      <form onSubmit={submit} className="grid">
        {error && <Alert kind="error">{error}</Alert>}
        {!restaurant && (
          <p className="muted" style={{ margin: 0 }}>
            {isSuperAdmin ? 'After saving, choose the restaurant’s owners and managers.' : 'You will own this restaurant. After saving, choose its managers.'}
          </p>
        )}
        <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} required placeholder="Culver's of Joliet" /></Field>
        <Field label="Street address"><input value={v.streetAddress ?? ''} onChange={(e) => set('streetAddress', e.target.value)} /></Field>
        <div className="form-row">
          <Field label="City"><input value={v.city ?? ''} onChange={(e) => set('city', e.target.value)} /></Field>
          <Field label="State"><input value={v.state ?? ''} onChange={(e) => set('state', e.target.value)} /></Field>
          <Field label="Zip"><input value={v.zip ?? ''} onChange={(e) => set('zip', e.target.value)} /></Field>
        </div>
        <div className="form-row">
          <Field label="Phone"><input value={v.phone ?? ''} onChange={(e) => set('phone', e.target.value)} /></Field>
          <Field label="Status">
            <select value={v.isActive ? '1' : '0'} onChange={(e) => set('isActive', e.target.value === '1')}>
              <option value="1">Active</option><option value="0">Inactive</option>
            </select>
          </Field>
        </div>
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : restaurant ? 'Save' : 'Add restaurant'}</button>
        </div>
      </form>
    </Modal>
  )
}
