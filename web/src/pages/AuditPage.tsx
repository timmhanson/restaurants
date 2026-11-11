import { useCallback, useEffect, useState } from 'react'
import { api, qs } from '../api/client'
import type { AuditDto, PagedResult } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Empty, Field, Spinner, daysAgo, fmtDateTime } from '../components/ui'

function Details({ raw }: { raw?: string | null }) {
  if (!raw) return <span className="muted">—</span>
  let pretty = raw
  try {
    pretty = JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    /* plain text */
  }
  const short = pretty.length > 90 ? pretty.slice(0, 90).replace(/\s+/g, ' ') + '…' : pretty.replace(/\s+/g, ' ')
  return (
    <details className="audit-details">
      <summary>{short}</summary>
      <pre>{pretty}</pre>
    </details>
  )
}

export default function AuditPage() {
  const { restaurants, groupId } = useAuth()
  const [actions, setActions] = useState<string[]>([])
  const [action, setAction] = useState('')
  const [restaurantId, setRestaurantId] = useState<number | ''>('')
  const [from, setFrom] = useState(daysAgo(30))
  const [to, setTo] = useState('')
  const [page, setPage] = useState(1)
  const pageSize = 100
  const [data, setData] = useState<PagedResult<AuditDto> | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<string[]>('/audit/actions').then(setActions).catch(() => setActions([]))
  }, [groupId])

  const load = useCallback(async () => {
    try {
      setData(
        await api.get<PagedResult<AuditDto>>(
          `/audit${qs({ action, restaurantId, from: from ? `${from}T00:00:00Z` : '', to: to ? `${to}T23:59:59Z` : '', page, pageSize })}`,
        ),
      )
      setError(null)
    } catch (e) {
      setError((e as Error).message)
    }
  }, [action, restaurantId, from, to, page])
  useEffect(() => {
    void load()
  }, [load, groupId])

  const totalPages = data ? Math.max(1, Math.ceil(data.total / pageSize)) : 1

  return (
    <>
      <div className="page-head">
        <h1>Audit log</h1>
        <span className="muted">Every sign-in, card entry, change and export by owners and managers.</span>
      </div>
      {error && <Alert kind="error">{error}</Alert>}
      <div className="filters">
        <Field label="Action">
          <select value={action} onChange={(e) => { setAction(e.target.value); setPage(1) }}>
            <option value="">Any</option>
            {actions.map((a) => <option key={a} value={a}>{a}</option>)}
          </select>
        </Field>
        <Field label="Restaurant">
          <select value={restaurantId} onChange={(e) => { setRestaurantId(e.target.value ? Number(e.target.value) : ''); setPage(1) }}>
            <option value="">Any</option>
            {restaurants.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
          </select>
        </Field>
        <Field label="From"><input type="date" value={from} onChange={(e) => { setFrom(e.target.value); setPage(1) }} /></Field>
        <Field label="To"><input type="date" value={to} onChange={(e) => { setTo(e.target.value); setPage(1) }} /></Field>
      </div>
      {!data ? (
        <Spinner />
      ) : data.items.length === 0 ? (
        <Empty>No audit entries match.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>When</th><th>Who</th><th>Action</th><th>Restaurant</th><th>Target</th><th>Details</th><th>IP</th></tr></thead>
            <tbody>
              {data.items.map((a) => (
                <tr key={a.id}>
                  <td style={{ whiteSpace: 'nowrap' }}>{fmtDateTime(a.timestamp)}</td>
                  <td>{a.userName}{a.userRole && <div className="muted" style={{ fontSize: '0.78rem' }}>{a.userRole}</div>}</td>
                  <td><code>{a.action}</code></td>
                  <td>{a.restaurantName || <span className="muted">—</span>}</td>
                  <td>{a.entityType ? `${a.entityType} ${a.entityId ?? ''}` : <span className="muted">—</span>}</td>
                  <td style={{ maxWidth: 420 }}><Details raw={a.details} /></td>
                  <td className="muted">{a.ipAddress}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="table-foot">
            <span>{data.total} entries</span>
            <span className="grow" />
            <button type="button" className="btn btn-sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>‹ Prev</button>
            <span>Page {page} of {totalPages}</span>
            <button type="button" className="btn btn-sm" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>Next ›</button>
          </div>
        </div>
      )}
    </>
  )
}
