import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { GroupDto, GroupRequest, RestaurantRequest, UserRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Empty, Field, Modal, Spinner, fmtDate } from '../components/ui'

export default function GroupsPage() {
  const { refreshGroups, selectGroup } = useAuth()
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

  /** Opens the group exactly as its Group Administrator sees it; the yellow bar leads back here. */
  const viewAs = (g: GroupDto, page = '/enter') => {
    selectGroup(g.id)
    navigate(page)
  }
  const setUp = (g: GroupDto) => viewAs(g, '/restaurants')

  return (
    <>
      <div className="page-head">
        <h1>Investment groups</h1>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>+ New group</button>
      </div>
      <p className="muted">
        Each investment group has its own group administrators, restaurant owners, managers, team logins and settings. Create a group with its
        administrators, owners and restaurants in one step. <strong>View as admin</strong> opens the group just as its Group Administrator sees
        it (starting on Enter card); <strong>Set up</strong> opens its restaurants and staff. A yellow bar at the top takes you back here.
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
            <thead><tr><th>Group</th><th>Group administrators</th><th>Restaurant owners</th><th className="num">Restaurants</th><th className="num">Users</th><th className="num">Cards</th><th>Created</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {list.map((g) => (
                <tr key={g.id} className={g.isActive ? '' : 'inactive'}>
                  <td>
                    <strong>{g.name}</strong>
                    <div>
                      <button type="button" className="btn btn-sm btn-primary view-as" onClick={() => viewAs(g)} title="See this group as its Group Administrator does">
                        View as admin
                      </button>
                    </div>
                  </td>
                  <td>{g.adminNames.length ? <div className="people">{g.adminNames.map((n) => <Badge key={n} kind="accent">{n}</Badge>)}</div> : <Badge kind="warn">None yet</Badge>}</td>
                  <td>{g.ownerNames.length ? <div className="people">{g.ownerNames.map((n) => <Badge key={n}>{n}</Badge>)}</div> : <span className="muted">none</span>}</td>
                  <td className="num">{g.restaurantCount}</td>
                  <td className="num">{g.userCount}</td>
                  <td className="num">{g.commentCount}</td>
                  <td>{fmtDate(g.createdAt)}</td>
                  <td>{g.isActive ? <Badge kind="ok">Active</Badge> : <Badge kind="neutral">Inactive</Badge>}</td>
                  <td>
                    <div className="row-actions">
                      <button type="button" className="btn btn-sm" onClick={() => setUp(g)}>Set up</button>
                      <button type="button" className="btn btn-sm" onClick={() => viewAs(g, '/users')}>Users</button>
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

function PeopleSection({ title, help, people, setPeople, addLabel }: {
  title: string
  help: string
  people: PersonDraft[]
  setPeople: (f: (s: PersonDraft[]) => PersonDraft[]) => void
  addLabel: string
}) {
  const set = (i: number, k: Exclude<keyof PersonDraft, 'key'>, val: string) => setPeople((s) => s.map((p, j) => (j === i ? { ...p, [k]: val } : p)))
  return (
    <section>
      <h3>{title}</h3>
      <p className="muted" style={{ marginTop: 0, fontSize: '0.85rem' }}>{help} Each signs in with a temporary password and must choose their own; add an email so they can change it later.</p>
      <div className="grid" style={{ gap: '0.5rem' }}>
        {people.map((p, i) => (
          <div key={p.key} className="repeat-row">
            <Field label="Name"><input value={p.displayName} onChange={(e) => set(i, 'displayName', e.target.value)} placeholder="Pat Partner" /></Field>
            <Field label="Username"><input value={p.username} onChange={(e) => set(i, 'username', e.target.value)} placeholder="pat@example.com" autoComplete="off" /></Field>
            <Field label="Email"><input type="email" value={p.email} onChange={(e) => set(i, 'email', e.target.value)} autoComplete="off" /></Field>
            <Field label="Temporary password"><input type="password" value={p.password} onChange={(e) => set(i, 'password', e.target.value)} minLength={8} autoComplete="new-password" /></Field>
            <button type="button" className="btn btn-sm" onClick={() => setPeople((s) => (s.length === 1 ? [blankPerson()] : s.filter((_, j) => j !== i)))} aria-label={`Remove ${title} row`}>Remove</button>
          </div>
        ))}
      </div>
      <button type="button" className="btn btn-sm" style={{ marginTop: '0.5rem' }} onClick={() => setPeople((s) => [...s, blankPerson()])}>{addLabel}</button>
    </section>
  )
}

type PersonDraft = { key: string; username: string; displayName: string; email: string; password: string }
/** notOwnedBy lists owner rows that do NOT own this restaurant, so a newly added owner owns everything by default. */
type RestaurantDraft = { name: string; streetAddress: string; city: string; state: string; zip: string; notOwnedBy: string[] }
let nextKey = 0
const blankPerson = (): PersonDraft => ({ key: `p${nextKey++}`, username: '', displayName: '', email: '', password: '' })
const blankRestaurant = (): RestaurantDraft => ({ name: '', streetAddress: '', city: '', state: '', zip: '', notOwnedBy: [] })
const filled = (p: PersonDraft) => [p.username, p.displayName, p.email, p.password].some((x) => x.trim())
const toRequest = (p: PersonDraft, role: 'GroupAdmin' | 'Owner'): UserRequest => ({
  username: p.username, displayName: p.displayName, email: p.email || null, password: p.password, role, permissions: [], restaurantIds: [], isActive: true,
})

function GroupModal({ group, onClose, onSaved }: { group: GroupDto | null; onClose: () => void; onSaved: (g: GroupDto, message: string, isNew: boolean) => void }) {
  const [name, setName] = useState(group?.name ?? '')
  const [isActive, setIsActive] = useState(group?.isActive ?? true)
  const [admins, setAdmins] = useState<PersonDraft[]>([blankPerson()])
  const [owners, setOwners] = useState<PersonDraft[]>([blankPerson()])
  const [restaurants, setRestaurants] = useState<RestaurantDraft[]>([blankRestaurant()])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const setRestaurant = (i: number, k: Exclude<keyof RestaurantDraft, 'notOwnedBy'>, val: string) => setRestaurants((s) => s.map((r, j) => (j === i ? { ...r, [k]: val } : r)))
  const toggleOwnerOf = (i: number, ownerKey: string) =>
    setRestaurants((s) => s.map((r, j) => (j === i ? { ...r, notOwnedBy: r.notOwnedBy.includes(ownerKey) ? r.notOwnedBy.filter((k) => k !== ownerKey) : [...r.notOwnedBy, ownerKey] } : r)))
  // Rows left completely empty are skipped, so the defaults never force an extra owner or restaurant.
  const adminRows = admins.filter(filled)
  const ownerRows = owners.filter(filled)
  const restaurantRows = restaurants.filter((r) => [r.name, r.streetAddress, r.city, r.state, r.zip].some((x) => x.trim()))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (group) {
        const saved = await api.put<GroupDto>(`/groups/${group.id}`, { name, isActive } satisfies GroupRequest)
        return onSaved(saved, `${saved.name} was updated.`, false)
      }
      if (adminRows.length === 0) {
        setError('Add at least one group administrator. They manage every account and restaurant in the group.')
        return
      }
      const body: GroupRequest = {
        name,
        isActive,
        admins: adminRows.map((a) => toRequest(a, 'GroupAdmin')),
        owners: ownerRows.map((o) => toRequest(o, 'Owner')),
        restaurants: restaurantRows.map<RestaurantRequest>(({ notOwnedBy, ...r }) => ({
          ...r,
          isActive: true,
          ownerUsernames: ownerRows.filter((o) => !notOwnedBy.includes(o.key)).map((o) => o.username),
        })),
      }
      const saved = await api.post<GroupDto>('/groups', body)
      onSaved(
        saved,
        `${saved.name} was created with ${adminRows.length} group administrator(s), ${ownerRows.length} owner(s) and ${restaurantRows.length} restaurant(s). ` +
          'Everyone chooses their own password at first sign-in.',
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
          <p className="muted" style={{ margin: 0 }}>To add administrators, owners or restaurants, close this and use <strong>Set up</strong> or <strong>Users</strong> on the group.</p>
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

          <PeopleSection
            title="Group administrators"
            help="Run the whole group: every restaurant, every user account and the settings. At least one is required; the overall investor is usually one."
            people={admins}
            setPeople={setAdmins}
            addLabel="+ Another administrator"
          />
          <PeopleSection
            title="Restaurant owners (optional)"
            help="Owners of specific restaurants. They run everything at the restaurants they own, including the managers who work there. Tick which restaurants each one owns below."
            people={owners}
            setPeople={setOwners}
            addLabel="+ Another owner"
          />

          <section>
            <h3>Restaurants</h3>
            <p className="muted" style={{ marginTop: 0, fontSize: '0.85rem' }}>
              Tick who owns each restaurant. A restaurant with no owner ticked is run by the group administrators. Afterwards, open a restaurant's
              <strong> Staff</strong> screen to add managers.
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
                  {ownerRows.length > 0 && (
                    <div className="owned-by">
                      <span className="muted small">Owned by:</span>
                      {ownerRows.map((o) => (
                        <label key={o.key} className="check small">
                          <input type="checkbox" checked={!r.notOwnedBy.includes(o.key)} onChange={() => toggleOwnerOf(i, o.key)} /> {o.displayName || o.username || 'New owner'}
                        </label>
                      ))}
                    </div>
                  )}
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
