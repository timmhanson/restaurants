import { useEffect, useMemo, useState } from 'react'
import { api } from '../api/client'
import type { DirectoryRestaurant } from '../api/types'
import { useViewAs } from '../auth/useViewAs'
import DataTable from '../components/DataTable'
import { Alert, Badge, Spinner, fmtDate } from '../components/ui'

/**
 * Every restaurant in every investment group, for the System Administrator. A directory, not a place to work in
 * a restaurant: "Open in group" views the restaurant's group as its administrator to change anything.
 */
export default function AllRestaurantsPage() {
  const [rows, setRows] = useState<DirectoryRestaurant[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [group, setGroup] = useState('')
  const [search, setSearch] = useState('')
  const [only, setOnly] = useState('')
  const viewAs = useViewAs()

  useEffect(() => {
    api.get<DirectoryRestaurant[]>('/system/restaurants').then(setRows).catch((e) => {
      setError((e as Error).message)
      setRows([])
    })
  }, [])

  const groups = useMemo(() => [...new Map((rows ?? []).map((r) => [r.groupId, r.groupName])).entries()].sort((a, b) => a[1].localeCompare(b[1])), [rows])
  const shown = useMemo(() => {
    const q = search.trim().toLowerCase()
    return (rows ?? []).filter(
      (r) =>
        (!group || String(r.groupId) === group) &&
        (!q || `${r.name} ${r.city ?? ''} ${r.state ?? ''} ${r.groupName} ${r.owners.join(' ')}`.toLowerCase().includes(q)) &&
        (only === '' ||
          (only === 'unstaffed' && r.owners.length + r.managers + r.teamLogins === 0) ||
          (only === 'overdue' && r.waitingOverAWeek > 0) ||
          (only === 'quiet' && r.cards === 0) ||
          (only === 'inactive' && (!r.isActive || !r.groupIsActive))),
    )
  }, [rows, group, search, only])

  return (
    <>
      <div className="page-head">
        <h1>Restaurants</h1>
      </div>
      <p className="muted">
        Every restaurant across all investment groups. To add a restaurant or change its staff, open its group with <strong>Open in group</strong>;
        you will see it as that group's administrator.
      </p>
      <div className="filters">
        <label className="field">
          <span className="field-label">Investment group</span>
          <select value={group} onChange={(e) => setGroup(e.target.value)}>
            <option value="">All groups</option>
            {groups.map(([id, name]) => (
              <option key={id} value={id}>{name}</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Show</span>
          <select value={only} onChange={(e) => setOnly(e.target.value)}>
            <option value="">All restaurants</option>
            <option value="overdue">With cards waiting over a week</option>
            <option value="unstaffed">With no owner, manager or team login</option>
            <option value="quiet">With no cards yet</option>
            <option value="inactive">Inactive</option>
          </select>
        </label>
        <label className="field wide">
          <span className="field-label">Search</span>
          <input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Restaurant, city, group or owner…" />
        </label>
      </div>
      {error && <Alert kind="error">{error}</Alert>}
      {rows === null ? (
        <Spinner />
      ) : (
        <DataTable<DirectoryRestaurant>
          title={`${shown.length} restaurant${shown.length === 1 ? '' : 's'}`}
          rows={shown}
          rowKey={(r) => r.id}
          filename="restaurants.csv"
          defaultSort={{ key: 'group', desc: false }}
          empty={rows.length === 0 ? 'No restaurants yet. Add them when you create an investment group.' : 'No restaurants match these filters.'}
          columns={[
            { key: 'group', label: 'Investment group', value: (r) => `${r.groupName} ${r.name}`, render: (r) => <>{r.groupName}{!r.groupIsActive && <> <Badge kind="neutral">inactive</Badge></>}</> },
            { key: 'name', label: 'Restaurant', value: (r) => r.name, render: (r) => <><strong>{r.name}</strong>{!r.isActive && <> <Badge kind="neutral">inactive</Badge></>}</> },
            { key: 'place', label: 'Location', value: (r) => [r.city, r.state].filter(Boolean).join(', ') || null },
            { key: 'owners', label: 'Owners', value: (r) => r.owners.join('; ') || null, render: (r) => (r.owners.length ? r.owners.join(', ') : <span className="muted">run by the group's administrators</span>) },
            { key: 'managers', label: 'Managers', num: true, value: (r) => r.managers },
            { key: 'team', label: 'Team logins', num: true, value: (r) => r.teamLogins },
            { key: 'cards', label: 'Cards', num: true, value: (r) => r.cards },
            { key: 'waiting', label: 'Waiting', num: true, value: (r) => r.waiting },
            { key: 'over', label: 'Over a week', num: true, value: (r) => r.waitingOverAWeek, render: (r) => (r.waitingOverAWeek > 0 ? <Badge kind="danger">{r.waitingOverAWeek}</Badge> : 0) },
            { key: 'last', label: 'Last visit', value: (r) => r.lastVisit ?? null, render: (r) => (r.lastVisit ? fmtDate(r.lastVisit) : <span className="muted">—</span>) },
            { key: 'added', label: 'Added', value: (r) => r.createdAt, render: (r) => fmtDate(r.createdAt) },
            {
              key: 'open', label: '', noCsv: true, noSort: true, value: () => null,
              render: (r) => <button type="button" className="btn btn-sm" onClick={() => viewAs(r.groupId, '/restaurants')}>Open in group</button>,
            },
          ]}
        />
      )}
    </>
  )
}
