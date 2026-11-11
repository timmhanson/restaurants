import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { GroupDto, GroupRequest, RestaurantRequest, UserRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Empty, Field, Modal, Spinner, fmtDate } from '../components/ui'

export default function GroupsPage() {
  const { refreshGroups, selectGroup, groupId } = useAuth()
  const navigate = useNavigate()
  const [list, setList] = useState<GroupDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [editing, setEditing] = useState<GroupDto | 'new' | null>(null)

  const load = useCallback(async () => {
    try {
      setList(await api.get<GroupDto[]>('/groups'))
    } catch (e) {
      setError((e as Error).message)
      setList([])
    }
  }, [])
  useEffect(() => {
    void load()
  }, [load])

  const setUp = (g: GroupDto, page = '/restaurants') => {
    selectGroup(g.id)
    navigate(page)
  }

  return (
    <>
      <div className="page-head">
        <h1>Investment groups</h1>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>+ New group</button>
      </div>
      <p className="muted">
        Each group has its own owners, restaurants, managers, team logins and settings. Create a group with its owners and restaurants in one step,
        then use <strong>Set up</strong> to choose who owns and manages each restaurant.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {list === null ? (
        <Spinner />
      ) : list.length === 0 ? (
        <Empty>No groups yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Group</th><th>Owners</th><th className="num">Restaurants</th><th className="num">Users</th><th className="num">Cards</th><th>Created</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {list.map((g) => (
                <tr key={g.id} className={g.isActive ? '' : 'inactive'}>
                  <td><strong>{g.name}</strong> {g.id === groupId && <Badge kind="accent">selected</Badge>}</td>
                  <td>{g.ownerNames.length ? <div className="people">{g.ownerNames.map((n) => <Badge key={n}>{n}</Badge>)}</div> : <Badge kind="warn">No owners yet</Badge>}</td>
                  <td className="num">{g.restaurantCount}</td>
                  <td className="num">{g.userCount}</td>
                  <td className="num">{g.commentCount}</td>
                  <td>{fmtDate(g.createdAt)}</td>
                  <td>{g.isActive ? <Badge kind="ok">Active</Badge> : <Badge kind="neutral">Inactive</Badge>}</td>
                  <td>
                    <div className="row-actions">
                      <button type="button" className="btn btn-sm btn-primary" onClick={() => setUp(g)}>Set up</button>
                      <button type="button" className="btn btn-sm" onClick={() => setUp(g, '/users')}>Users</button>
                      <button type="button" className="btn btn-sm" onClick={() => setEditing(g)}>Edit</button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && (
        <GroupModal
          group={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={async (g, msg, isNew) => {
            setEditing(null)
            setNotice(msg)
            await load()
            await refreshGroups()
            if (isNew) setUp(g)
          }}
        />
      )}
    </>
  )
}

type OwnerDraft = { username: string; displayName: string; email: string; password: string }
type RestaurantDraft = { name: string; streetAddress: string; city: string; state: string; zip: string }
const blankOwner = (): OwnerDraft => ({ username: '', displayName: '', email: '', password: '' })
const blankRestaurant = (): RestaurantDraft => ({ name: '', streetAddress: '', city: '', state: '', zip: '' })

function GroupModal({ group, onClose, onSaved }: { group: GroupDto | null; onClose: () => void; onSaved: (g: GroupDto, message: string, isNew: boolean) => void }) {
  const [name, setName] = useState(group?.name ?? '')
  const [isActive, setIsActive] = useState(group?.isActive ?? true)
  const [owners, setOwners] = useState<OwnerDraft[]>([blankOwner()])
  const [restaurants, setRestaurants] = useState<RestaurantDraft[]>([blankRestaurant()])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const setOwner = (i: number, k: keyof OwnerDraft, val: string) => setOwners((s) => s.map((o, j) => (j === i ? { ...o, [k]: val } : o)))
  const setRestaurant = (i: number, k: keyof RestaurantDraft, val: string) => setRestaurants((s) => s.map((r, j) => (j === i ? { ...r, [k]: val } : r)))
  // Rows left completely empty are skipped, so the defaults never force an owner or restaurant.
  const ownerRows = owners.filter((o) => Object.values(o).some((x) => x.trim()))
  const restaurantRows = restaurants.filter((r) => Object.values(r).some((x) => x.trim()))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (group) {
        const saved = await api.put<GroupDto>(`/groups/${group.id}`, { name, isActive } satisfies GroupRequest)
        return onSaved(saved, `${saved.name} was updated.`, false)
      }
      const body: GroupRequest = {
        name,
        isActive,
        owners: ownerRows.map<UserRequest>((o) => ({
          username: o.username, displayName: o.displayName, email: o.email || null, password: o.password, role: 'Owner', permissions: [], restaurantIds: [], isActive: true,
        })),
        restaurants: restaurantRows.map<RestaurantRequest>((r) => ({ ...r, isActive: true })),
      }
      const saved = await api.post<GroupDto>('/groups', body)
      onSaved(
        saved,
        `${saved.name} was created with ${ownerRows.length} owner(s) and ${restaurantRows.length} restaurant(s).` +
          (ownerRows.length ? ' Owners choose their own password at first sign-in.' : ''),
        true,
      )
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (group) {
    return (
      <Modal title={`Edit ${group.name}`} onClose={onClose}>
        <form onSubmit={submit} className="grid">
          {error && <Alert kind="error">{error}</Alert>}
          <Field label="Group name"><input value={name} onChange={(e) => setName(e.target.value)} required /></Field>
          <Field label="Status">
            <select value={isActive ? '1' : '0'} onChange={(e) => setIsActive(e.target.value === '1')}>
              <option value="1">Active</option><option value="0">Inactive (owners and managers cannot sign in)</option>
            </select>
          </Field>
          <p className="muted" style={{ margin: 0 }}>To add owners or restaurants, close this and use <strong>Set up</strong> or <strong>Users</strong> on the group.</p>
          <div className="actions">
            <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
            <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
          </div>
        </form>
      </Modal>
    )
  }

  return (
    <Modal title="New investment group" onClose={onClose} wide>
      <form onSubmit={submit} className="grid">
        {error && <Alert kind="error">{error}</Alert>}
        <div className="steps">
          <section>
            <h3>Group</h3>
            <Field label="Group name"><input value={name} onChange={(e) => setName(e.target.value)} required placeholder="Hanson Restaurant Partners" autoFocus /></Field>
          </section>

          <section>
            <h3>Owners</h3>
            <p className="muted" style={{ marginTop: 0, fontSize: '0.85rem' }}>
              The investors in this group. Each signs in with a temporary password and must choose their own. Add an email so they can change it later.
            </p>
            <div className="grid" style={{ gap: '0.5rem' }}>
              {owners.map((o, i) => (
                <div key={i} className="repeat-row">
                  <Field label="Name"><input value={o.displayName} onChange={(e) => setOwner(i, 'displayName', e.target.value)} placeholder="Pat Owner" /></Field>
                  <Field label="Username"><input value={o.username} onChange={(e) => setOwner(i, 'username', e.target.value)} placeholder="pat@example.com" autoComplete="off" /></Field>
                  <Field label="Email"><input type="email" value={o.email} onChange={(e) => setOwner(i, 'email', e.target.value)} autoComplete="off" /></Field>
                  <Field label="Temporary password"><input type="password" value={o.password} onChange={(e) => setOwner(i, 'password', e.target.value)} minLength={8} autoComplete="new-password" /></Field>
                  <button type="button" className="btn btn-sm" onClick={() => setOwners((s) => (s.length === 1 ? [blankOwner()] : s.filter((_, j) => j !== i)))} aria-label="Remove owner row">Remove</button>
                </div>
              ))}
            </div>
            <button type="button" className="btn btn-sm" style={{ marginTop: '0.5rem' }} onClick={() => setOwners((s) => [...s, blankOwner()])}>+ Another owner</button>
          </section>

          <section>
            <h3>Restaurants</h3>
            <p className="muted" style={{ marginTop: 0, fontSize: '0.85rem' }}>
              Every owner above starts out owning every restaurant here. Afterwards, open a restaurant's <strong>Staff</strong> screen to change its owners and add managers.
            </p>
            <div className="grid" style={{ gap: '0.5rem' }}>
              {restaurants.map((r, i) => (
                <div key={i} className="repeat-row">
                  <Field label="Name"><input value={r.name} onChange={(e) => setRestaurant(i, 'name', e.target.value)} placeholder="Culver's of Joliet" /></Field>
                  <Field label="Street address"><input value={r.streetAddress} onChange={(e) => setRestaurant(i, 'streetAddress', e.target.value)} /></Field>
                  <Field label="City"><input value={r.city} onChange={(e) => setRestaurant(i, 'city', e.target.value)} /></Field>
                  <Field label="State / zip">
                    <span style={{ display: 'flex', gap: '0.3rem' }}>
                      <input value={r.state} onChange={(e) => setRestaurant(i, 'state', e.target.value)} style={{ maxWidth: '4.5rem' }} aria-label="State" />
                      <input value={r.zip} onChange={(e) => setRestaurant(i, 'zip', e.target.value)} aria-label="Zip" />
                    </span>
                  </Field>
                  <button type="button" className="btn btn-sm" onClick={() => setRestaurants((s) => (s.length === 1 ? [blankRestaurant()] : s.filter((_, j) => j !== i)))} aria-label="Remove restaurant row">Remove</button>
                </div>
              ))}
            </div>
            <button type="button" className="btn btn-sm" style={{ marginTop: '0.5rem' }} onClick={() => setRestaurants((s) => [...s, blankRestaurant()])}>+ Another restaurant</button>
          </section>
        </div>
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Creating…' : 'Create group'}</button>
        </div>
      </form>
    </Modal>
  )
}
