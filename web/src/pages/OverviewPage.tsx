import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { GroupSnapshot, SignInRow, SystemOverview } from '../api/types'
import { useViewAs } from '../auth/useViewAs'
import DataTable from '../components/DataTable'
import Stat from '../components/Stat'
import { Alert, Badge, Spinner, fmtDateTime } from '../components/ui'

const SEVERITY = { high: 'Needs action', medium: 'Worth a look', info: 'For your information' } as const

/** The System Administrator's home: what needs attention, and how the whole site is doing right now. */
export default function OverviewPage() {
  const [data, setData] = useState<SystemOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const viewAs = useViewAs()

  const load = useCallback(() => {
    setLoading(true)
    setError(null)
    api
      .get<SystemOverview>('/system/overview')
      .then(setData)
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [])
  useEffect(load, [load])

  return (
    <>
      <div className="page-head">
        <h1>System overview</h1>
        <span className="grow" />
        {data && <span className="muted small">as of {fmtDateTime(data.generatedAt)}</span>}
        <button type="button" className="btn" onClick={load} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>
      {error && <Alert kind="error">{error}</Alert>}
      {!data ? (
        <Spinner />
      ) : (
        <>
          <div className="panel">
            <h2>Needs your attention</h2>
            {data.attention.length === 0 ? (
              <p className="muted" style={{ margin: 0 }}>Nothing right now. Every group has an administrator, nothing is failing and no cards are overdue.</p>
            ) : (
              <ul className="attention">
                {data.attention.map((a, i) => (
                  <li key={i} className={`attention-${a.severity}`}>
                    <span className="attention-mark" title={SEVERITY[a.severity]} aria-label={SEVERITY[a.severity]} />
                    <div className="attention-text">
                      <strong>{a.title}</strong>
                      <div className="muted small">{a.detail}</div>
                    </div>
                    {a.link && <Link className="btn btn-sm" to={a.link}>Open</Link>}
                  </li>
                ))}
              </ul>
            )}
          </div>

          <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
            <Stat value={data.activeGroups} label={`investment group${data.activeGroups === 1 ? '' : 's'}${data.groups > data.activeGroups ? ` (${data.groups - data.activeGroups} inactive)` : ''}`} />
            <Stat value={data.restaurants} label={`restaurant${data.restaurants === 1 ? '' : 's'}`} />
            <Stat value={data.people} label={`people with access, plus ${data.systemAdmins} System Administrator${data.systemAdmins === 1 ? '' : 's'}`} />
            <Stat value={`${data.activeToday} / ${data.active7d}`} label="people active today / in the last 7 days" />
            <Stat value={data.signInsToday} label={`sign-ins today${data.failedSignIns24h ? `, ${data.failedSignIns24h} failed in 24 hours` : ''}`} warn={data.failedSignIns24h >= 5} />
            <Stat value={`${data.cardsToday} / ${data.cards7d}`} label="cards entered today / in the last 7 days" />
            <Stat value={data.waitingOverAWeek} label={`cards waiting over a week (${data.waiting} waiting in all)`} warn={data.waitingOverAWeek > 0} />
            <Stat value={data.errors24h} label="server errors in the last 24 hours" warn={data.errors24h > 0} />
          </div>

          <DataTable<GroupSnapshot>
            title="Investment groups at a glance"
            help="Activity and cards are for the last 7 days. Open a group to see it exactly as its Investment Group Administrator does."
            rows={data.groupSnapshots}
            rowKey={(g) => g.id}
            filename="investment-groups-at-a-glance.csv"
            empty="No investment groups yet. Create the first one on Investment groups."
            columns={[
              { key: 'name', label: 'Investment group', value: (g) => g.name, render: (g) => <><strong>{g.name}</strong>{!g.isActive && <> <Badge kind="neutral">inactive</Badge></>}</> },
              { key: 'restaurants', label: 'Restaurants', num: true, value: (g) => g.restaurants },
              { key: 'people', label: 'People', num: true, value: (g) => g.people },
              { key: 'admins', label: 'Administrators', num: true, value: (g) => g.admins, render: (g) => (g.admins === 0 ? <Badge kind="danger">none</Badge> : g.admins) },
              { key: 'active', label: 'Active people', num: true, value: (g) => g.activePeople7d },
              { key: 'cards', label: 'Cards entered', num: true, value: (g) => g.cards7d },
              { key: 'waiting', label: 'Waiting', num: true, value: (g) => g.waiting },
              { key: 'over', label: 'Over a week', num: true, value: (g) => g.waitingOverAWeek, render: (g) => (g.waitingOverAWeek > 0 ? <Badge kind="danger">{g.waitingOverAWeek}</Badge> : 0) },
              { key: 'last', label: 'Last activity', value: (g) => g.lastActivity ?? null, render: (g) => (g.lastActivity ? fmtDateTime(g.lastActivity) : <span className="muted">none this week</span>) },
              {
                key: 'open', label: '', noCsv: true, noSort: true, value: () => null,
                render: (g) => <button type="button" className="btn btn-sm" onClick={() => viewAs(g.id)}>View as admin</button>,
              },
            ]}
          />

          <SignIns title="Latest sign-ins" rows={data.recentSignIns} more />
        </>
      )}
    </>
  )
}

/** Successful and failed sign-ins, newest first. */
export function SignIns({ title, rows, more = false }: { title: string; rows: SignInRow[]; more?: boolean }) {
  return (
    <div className="panel">
      <div className="page-head">
        <h2 style={{ margin: 0 }}>{title}</h2>
        <span className="grow" />
        {more && <Link className="btn btn-sm" to="/security">Security</Link>}
      </div>
      {rows.length === 0 ? (
        <p className="muted">No sign-ins recorded yet.</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th>When</th><th>Who</th><th>Investment group</th><th>Result</th><th>From</th></tr>
            </thead>
            <tbody>
              {rows.map((s, i) => (
                <tr key={i}>
                  <td style={{ whiteSpace: 'nowrap' }}>{fmtDateTime(s.at)}</td>
                  <td>
                    {s.displayName ?? s.userName ?? <span className="muted">(blank)</span>}
                    <div className="muted small">{s.displayName ? s.userName : null}{s.roleTitle ? ` · ${s.roleTitle}` : ''}</div>
                  </td>
                  <td>{s.groupName ?? <span className="muted">—</span>}</td>
                  <td>{s.succeeded ? <Badge kind="ok">Signed in</Badge> : <Badge kind="danger">Failed</Badge>}</td>
                  <td className="small">{s.ipAddress ?? '—'}<div className="muted">{device(s.userAgent)}</div></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

/** A short "Chrome on Mac" from the browser's long description. */
export function device(ua?: string | null) {
  if (!ua) return ''
  const browser = /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Firefox\//.test(ua) ? 'Firefox' : /Safari\//.test(ua) ? 'Safari' : ua.split('/')[0]
  const os = /iPhone|iPad/.test(ua) ? 'iPhone/iPad' : /Android/.test(ua) ? 'Android' : /Macintosh/.test(ua) ? 'Mac' : /Windows/.test(ua) ? 'Windows' : /Linux/.test(ua) ? 'Linux' : ''
  return os ? `${browser} on ${os}` : browser
}
