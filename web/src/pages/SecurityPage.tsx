import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { AccountFlag, FailedSignInRow, RefusedRow, SystemSecurity } from '../api/types'
import DataTable from '../components/DataTable'
import Stat from '../components/Stat'
import { Alert, Badge, Spinner, fmtDate, fmtDateTime } from '../components/ui'
import { SignIns } from './OverviewPage'

const RANGES = [1, 7, 30, 90]

/** Who is getting in, who is being kept out, and which accounts need looking at. System Administrators only. */
export default function SecurityPage() {
  const [days, setDays] = useState(7)
  const [data, setData] = useState<SystemSecurity | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    setLoading(true)
    setError(null)
    api
      .get<SystemSecurity>(`/system/security?days=${days}`)
      .then(setData)
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [days])

  const range = days === 1 ? 'the last 24 hours' : `the last ${days} days`
  const from = (() => {
    const d = new Date()
    d.setDate(d.getDate() - days)
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
  })()

  return (
    <>
      <div className="page-head">
        <h1>Security</h1>
        <span className="grow" />
        <div className="chips" role="group" aria-label="Time range">
          {RANGES.map((d) => (
            <button key={d} type="button" className={`chip ${days === d ? 'on' : ''}`} aria-pressed={days === d} onClick={() => setDays(d)}>
              {d === 1 ? '24 hours' : `${d} days`}
            </button>
          ))}
        </div>
      </div>
      {error && <Alert kind="error">{error}</Alert>}
      {!data ? (
        <Spinner />
      ) : (
        <div style={{ opacity: loading ? 0.6 : 1 }}>
          <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
            <Stat value={data.signIns} label={`sign-ins in ${range}`} />
            <Stat value={data.failedSignIns} label="failed sign-ins (wrong username or password)" warn={data.failedSignIns >= 5} />
            <Stat value={data.refused} label="requests refused for lack of permission" warn={data.refused > 0} />
            <Stat value={data.staleCopies} label="requests from out-of-date copies of the site" />
          </div>

          <DataTable<FailedSignInRow>
            title="Failed sign-ins"
            help="Grouped by the username that was typed. Many attempts on one account, or on usernames that do not exist, can mean someone is guessing."
            rows={data.failed}
            rowKey={(r) => r.userName}
            filename={`failed-sign-ins-${days}d.csv`}
            defaultSort={{ key: 'attempts', desc: true }}
            empty={`No failed sign-ins in ${range}.`}
            columns={[
              { key: 'user', label: 'Username typed', value: (r) => r.userName, render: (r) => <><strong>{r.userName}</strong>{!r.knownAccount && <> <Badge kind="warn">no such account</Badge></>}</> },
              { key: 'attempts', label: 'Attempts', num: true, value: (r) => r.attempts, render: (r) => (r.attempts >= 5 ? <Badge kind="danger">{r.attempts}</Badge> : r.attempts) },
              { key: 'last', label: 'Most recent', value: (r) => r.last, render: (r) => fmtDateTime(r.last) },
              { key: 'ips', label: 'From', value: (r) => r.ipAddresses.join('; '), render: (r) => <span className="small">{r.ipAddresses.join(', ') || '—'}</span> },
              { key: 'log', label: '', noCsv: true, noSort: true, value: () => null, render: (r) => <Link className="btn btn-sm" to={`/activity?search=${encodeURIComponent(r.userName)}&from=${from}`}>Activity</Link> },
            ]}
          />

          <DataTable<RefusedRow>
            title="Refused access"
            help="Signed-in people who asked for something their role does not allow. A few are normal; a pattern is worth a conversation."
            rows={data.refusedBy}
            rowKey={(r) => r.userName ?? 'unknown'}
            filename={`refused-access-${days}d.csv`}
            defaultSort={{ key: 'count', desc: true }}
            empty={`Nobody was refused anything in ${range}.`}
            columns={[
              { key: 'who', label: 'Person', value: (r) => r.displayName ?? r.userName ?? '', render: (r) => <><strong>{r.displayName ?? r.userName ?? 'Unknown'}</strong><div className="muted small">{r.roleTitle}</div></> },
              { key: 'group', label: 'Investment group', value: (r) => r.groupName ?? null },
              { key: 'count', label: 'Times', num: true, value: (r) => r.count },
              { key: 'last', label: 'Most recent', value: (r) => r.last, render: (r) => fmtDateTime(r.last) },
              { key: 'what', label: 'What they asked for', value: (r) => r.paths.join('; '), render: (r) => <code className="small">{r.paths.join(', ')}</code> },
            ]}
          />

          <DataTable<AccountFlag>
            title="Accounts to look at"
            help="Accounts that are unused, locked out, or cannot look after their own password."
            rows={data.accounts}
            rowKey={(a) => `${a.id}-${a.issue}`}
            filename="accounts-to-look-at.csv"
            defaultSort={{ key: 'issue', desc: false }}
            empty="Every account is in good order."
            columns={[
              { key: 'who', label: 'Person', value: (a) => a.displayName, render: (a) => <><strong>{a.displayName}</strong><div className="muted small">{a.username}</div></> },
              { key: 'role', label: 'Role', value: (a) => a.roleTitle },
              { key: 'group', label: 'Investment group', value: (a) => a.groupName ?? null, render: (a) => a.groupName ?? <span className="muted">whole site</span> },
              { key: 'issue', label: 'What to look at', value: (a) => a.issue },
              { key: 'since', label: 'Since', value: (a) => a.since ?? null, render: (a) => (a.since ? fmtDate(a.since) : <span className="muted">—</span>) },
              { key: 'open', label: '', noCsv: true, noSort: true, value: () => null, render: (a) => <Link className="btn btn-sm" to={`/people?q=${encodeURIComponent(a.username)}`}>Open</Link> },
            ]}
          />

          <div className="panel">
            <div className="page-head">
              <h2 style={{ margin: 0 }}>System Administrators</h2>
              <span className="grow" />
              <Link className="btn btn-sm" to="/people?role=SuperAdmin">Manage</Link>
            </div>
            <p className="legend-note">These people can see and change everything, in every investment group. Keep the list short, with at least two.</p>
            <ul className="plain-list">
              {data.systemAdministrators.map((u) => (
                <li key={u.id}>
                  <strong>{u.displayName}</strong> <span className="muted">{u.username}</span> {!u.isActive && <Badge kind="danger">suspended</Badge>}
                  <span className="muted small"> · last sign-in {u.lastLoginAt ? fmtDateTime(u.lastLoginAt) : 'never'}</span>
                </li>
              ))}
            </ul>
          </div>

          <SignIns title={`Sign-ins in ${range} (latest 50)`} rows={data.recentSignIns} />
        </div>
      )}
    </>
  )
}
