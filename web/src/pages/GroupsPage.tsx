import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { GroupDto, GroupRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Empty, Field, Modal, Spinner, fmtDate } from '../components/ui'

export default function GroupsPage() {
  const { refreshGroups, selectGroup, groupId } = useAuth()
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

  return (
    <>
      <div className="page-head">
        <h1>Investment groups</h1>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>+ New group</button>
      </div>
      <p className="muted">Each group has its own owners, restaurants, managers, team logins and settings. Pick a group in the top bar to work inside it.</p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {list === null ? (
        <Spinner />
      ) : list.length === 0 ? (
        <Empty>No groups yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Group</th><th className="num">Restaurants</th><th className="num">Users</th><th className="num">Cards</th><th>Created</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {list.map((g) => (
                <tr key={g.id} className={g.isActive ? '' : 'inactive'}>
                  <td><strong>{g.name}</strong> {g.id === groupId && <Badge kind="accent">selected</Badge>}</td>
                  <td className="num">{g.restaurantCount}</td>
                  <td className="num">{g.userCount}</td>
                  <td className="num">{g.commentCount}</td>
                  <td>{fmtDate(g.createdAt)}</td>
                  <td>{g.isActive ? <Badge kind="ok">Active</Badge> : <Badge kind="neutral">Inactive</Badge>}</td>
                  <td>
                    <div className="row-actions">
                      <button type="button" className="btn btn-sm" onClick={() => selectGroup(g.id)} disabled={g.id === groupId}>Work in</button>
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
          onSaved={async (g, msg) => {
            setEditing(null)
            setNotice(msg)
            await load()
            await refreshGroups()
            if (!groupId) selectGroup(g.id)
          }}
        />
      )}
    </>
  )
}

function GroupModal({ group, onClose, onSaved }: { group: GroupDto | null; onClose: () => void; onSaved: (g: GroupDto, message: string) => void }) {
  const [name, setName] = useState(group?.name ?? '')
  const [isActive, setIsActive] = useState(group?.isActive ?? true)
  const [withOwner, setWithOwner] = useState(!group)
  const [owner, setOwner] = useState({ username: '', displayName: '', email: '', password: '' })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const body: GroupRequest = {
        name,
        isActive,
        firstOwner:
          !group && withOwner
            ? { username: owner.username, displayName: owner.displayName, email: owner.email || null, password: owner.password, role: 'Owner', permissions: [], restaurantIds: [], isActive: true }
            : null,
      }
      const saved = group ? await api.put<GroupDto>(`/groups/${group.id}`, body) : await api.post<GroupDto>('/groups', body)
      onSaved(saved, group ? `${saved.name} was updated.` : `${saved.name} was created.${withOwner ? ` ${owner.displayName} must choose a new password at first login.` : ''}`)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={group ? `Edit ${group.name}` : 'New investment group'} onClose={onClose}>
      <form onSubmit={submit} className="grid">
        {error && <Alert kind="error">{error}</Alert>}
        <Field label="Group name"><input value={name} onChange={(e) => setName(e.target.value)} required placeholder="Hanson Restaurant Partners" /></Field>
        <Field label="Status">
          <select value={isActive ? '1' : '0'} onChange={(e) => setIsActive(e.target.value === '1')}>
            <option value="1">Active</option><option value="0">Inactive (owners and managers cannot sign in)</option>
          </select>
        </Field>
        {!group && (
          <>
            <label className="check">
              <input type="checkbox" checked={withOwner} onChange={(e) => setWithOwner(e.target.checked)} /> Create the group's first owner now
            </label>
            {withOwner && (
              <div className="grid" style={{ paddingLeft: '1.4rem' }}>
                <div className="form-row">
                  <Field label="Owner username"><input value={owner.username} onChange={(e) => setOwner({ ...owner, username: e.target.value })} required minLength={3} autoComplete="off" /></Field>
                  <Field label="Owner display name"><input value={owner.displayName} onChange={(e) => setOwner({ ...owner, displayName: e.target.value })} required /></Field>
                </div>
                <div className="form-row">
                  <Field label="Email (optional)"><input type="email" value={owner.email} onChange={(e) => setOwner({ ...owner, email: e.target.value })} autoComplete="off" /></Field>
                  <Field label="Temporary password" hint="They must change it at first login."><input type="password" value={owner.password} onChange={(e) => setOwner({ ...owner, password: e.target.value })} required minLength={8} autoComplete="new-password" /></Field>
                </div>
              </div>
            )}
          </>
        )}
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : group ? 'Save' : 'Create group'}</button>
        </div>
      </form>
    </Modal>
  )
}
