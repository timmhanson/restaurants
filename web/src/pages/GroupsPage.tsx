import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { GroupDto, GroupRequest, GroupSettingsData, PermissionRules, RestaurantRequest, UserRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import GroupPeoplePanel from '../components/GroupPeoplePanel'
import GroupSettingsEditor from '../components/GroupSettingsEditor'
import PermissionRulesEditor, { type PermissionChange } from '../components/PermissionRulesEditor'
import { Alert, Badge, Empty, Field, Modal, Spinner, fmtDate } from '../components/ui'

export default function GroupsPage() {
  const { refreshGroups, selectGroup } = useAuth()
  const navigate = useNavigate()
  const [list, setList] = useState<GroupDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [editing, setEditing] = useState<GroupDto | 'new' | null>(null)
  // The group whose people are shown under its row.
  const [showPeople, setShowPeople] = useState<number | null>(null)

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

  /** Opens the group exactly as its Investment Group Administrator sees it; the dark blue bar leads back here. */
  const viewAs = (g: GroupDto, page = '/enter') => {
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
        Each investment group has its own administrators, restaurant owners, managers, team logins and settings. Create a group with its
        administrators, owners and restaurants in one step. <strong>Impersonate</strong> opens the group just as its Investment Group Administrator sees
        it (starting on Enter card). A dark blue bar at the top takes you back here.
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
            <thead><tr><th>Group</th><th>Investment Group administrators</th><th>Restaurant owners</th><th className="num">Restaurants</th><th className="num">Users</th><th className="num">Cards</th><th>Created</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {list.map((g) => (
                <tr key={g.id} className={g.isActive ? '' : 'inactive'}>
                  <td><strong>{g.name}</strong></td>
                  <td>{g.adminNames.length ? <div className="people">{g.adminNames.map((n) => <Badge key={n} kind="accent">{n}</Badge>)}</div> : <Badge kind="warn">None yet</Badge>}</td>
                  <td>{g.ownerNames.length ? <div className="people">{g.ownerNames.map((n) => <Badge key={n}>{n}</Badge>)}</div> : <span className="muted">none</span>}</td>
                  <td className="num">{g.restaurantCount}</td>
                  <td className="num">{g.userCount}</td>
                  <td className="num">{g.commentCount}</td>
                  <td>{fmtDate(g.createdAt)}</td>
                  <td>{g.isActive ? <Badge kind="ok">Active</Badge> : <Badge kind="neutral">Inactive</Badge>}</td>
                  <td>
                    <div className="row-actions" style={{ flexWrap: 'nowrap' }}>
                      <button type="button" className="btn btn-sm" style={{ whiteSpace: 'nowrap' }} aria-expanded={showPeople === g.id} onClick={() => setShowPeople(showPeople === g.id ? null : g.id)}>
                        {showPeople === g.id ? 'Hide users' : 'Users'}
                      </button>
                      <button type="button" className="btn btn-sm" onClick={() => setEditing(g)}>Edit</button>
                      <button type="button" className="btn btn-sm btn-primary" onClick={() => viewAs(g)} title="See and use this group just as its Investment Group Administrator does" aria-label={`Impersonate an administrator of ${g.name}`}>
                        Impersonate
                      </button>
                    </div>
                  </td>
                </tr>
              )).flatMap((row, i) => {
                const g = list[i]
                return showPeople === g.id
                  ? [row, <tr key={`${g.id}-people`}><td colSpan={9} style={{ padding: 0 }}><GroupPeoplePanel groupId={g.id} groupName={g.name} onOpenInGroup={() => viewAs(g, '/users')} onChanged={() => void load()} /></td></tr>]
                  : [row]
              })}
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
            if (isNew) viewAs(g, '/restaurants')
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
  // The group's settings (see GroupSettingsEditor), loaded with the dialog and saved with it.
  const [settings, setSettings] = useState<GroupSettingsData | null>(null)
  useEffect(() => {
    if (!group) return
    api.get<GroupSettingsData>(`/system/groups/${group.id}/settings`).then(setSettings).catch(() => setSettings(null))
  }, [group])
  // What owners and managers may do in the group, as the System Administrator decides it; saved with the dialog.
  const [permissions, setPermissions] = useState<PermissionRules | null>(null)
  const [permissionChanges, setPermissionChanges] = useState<PermissionChange[]>([])
  useEffect(() => {
    if (!group) return
    api.get<PermissionRules>(`/system/groups/${group.id}/permissions`).then(setPermissions).catch(() => setPermissions(null))
  }, [group])
  const changePermission = (c: PermissionChange) => {
    setPermissionChanges((list) => [...list, c])
    setPermissions((cur) => cur && {
      ...cur,
      permissions: cur.permissions.map((x) => {
        if (x.name !== c.name) return x
        const next = { ...x, ...(c.owners ? { siteOwners: c.owners as typeof x.siteOwners } : {}), ...(c.managers ? { siteManagers: c.managers as typeof x.siteManagers } : {}) }
        return { ...next, ownersCan: next.siteOwners === 'yes' || (next.siteOwners === 'group' && next.groupOwners === 'yes') }
      }),
    })
  }
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
        if (settings)
          await api.put(`/system/groups/${group.id}/settings`, {
            mail: settings.mail, mailMode: settings.mailMode, usualWay: settings.usualWay, signatureForAll: settings.signatureForAll?.trim() || null, payRule: settings.payRule,
            groupDecides: settings.groupDecides,
            restaurants: settings.restaurants.map((r) => ({ id: r.id, mailOn: r.mailOn, mailLive: r.mailLive, mailByDefault: r.mailByDefault, signatureName: r.signatureName?.trim() || null })),
          })
        if (permissions && permissionChanges.length > 0) await api.put(`/system/groups/${group.id}/permissions`, { rows: permissionChanges })
        return onSaved(saved, `${saved.name} was updated.`, false)
      }
      if (adminRows.length === 0) {
        setError('Add at least one investment group administrator. They manage every account and restaurant in the group.')
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
        `${saved.name} was created with ${adminRows.length} investment group administrator(s), ${ownerRows.length} owner(s) and ${restaurantRows.length} restaurant(s). ` +
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
      <Modal title={`Edit ${group.name}`} onClose={onClose} wide>
        <form onSubmit={submit} className="grid">
          {error && <Alert kind="error">{error}</Alert>}
          <Field label="Group name"><input value={name} onChange={(e) => setName(e.target.value)} required /></Field>
          <Field label="Status">
            <select value={isActive ? '1' : '0'} onChange={(e) => setIsActive(e.target.value === '1')}>
              <option value="1">Active</option><option value="0">Inactive (owners and managers cannot sign in)</option>
            </select>
          </Field>
          {settings && <GroupSettingsEditor value={settings} onChange={setSettings} />}
          {permissions && (
            <>
              <h3 style={{ margin: '0.5rem 0 0' }}>What owners and managers may do in this group</h3>
              <p className="muted small" style={{ margin: 0 }}>
                Investment Group Administrators can do everything in their group. For owners and for managers, each permission is yours for this group (all can, or
                none can), which takes precedence, or left to the group’s administrators. They in turn decide it for all their owners and managers, or have it set
                manager by manager; owners set that for their managers when “Choose what each manager may do” is allowed. A manager never holds what the owners do not.
              </p>
              <PermissionRulesEditor value={permissions} onChange={changePermission} />
            </>
          )}
          <p className="muted" style={{ margin: 0 }}>To add administrators, owners or restaurants, close this and use <strong>Impersonate</strong> on the group.</p>
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
            title="Investment Group administrators"
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
              Tick who owns each restaurant. A restaurant with no owner ticked is run by the investment group administrators. Afterwards, open a restaurant's
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
