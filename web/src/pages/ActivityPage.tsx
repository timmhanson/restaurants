import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api, downloadBlob, qs } from '../api/client'
import type { InteractionDto, InteractionKind, InteractionPage, SystemCatalog } from '../api/types'
import { Alert, Badge, Empty, Spinner, daysAgo, fmtDateTime, today } from '../components/ui'

const KINDS: { value: InteractionKind; label: string }[] = [
  { value: 'Page', label: 'Pages opened' },
  { value: 'Click', label: 'Clicks' },
  { value: 'Choice', label: 'Choices' },
  { value: 'Api', label: 'Server requests' },
  { value: 'SignOut', label: 'Sign-outs' },
]
const FILTERS = ['from', 'to', 'groupId', 'restaurantId', 'userId', 'kind', 'search', 'errorsOnly', 'sessionId', 'page'] as const

function describe(i: InteractionDto) {
  switch (i.kind) {
    case 'Page':
      return <>Opened <code>{i.path}</code></>
    case 'Click':
      return <>Clicked <strong>{i.action ?? 'something'}</strong>{i.detail ? <span className="muted"> · {i.detail}</span> : null}<div className="muted small">on {i.path}</div></>
    case 'Choice':
      return <>Chose <strong>{i.detail || '(blank)'}</strong> for {i.action ?? 'a field'}<div className="muted small">on {i.path}</div></>
    case 'SignOut':
      return <>Signed out</>
    default:
      return (
        <>
          <code>{i.method} {i.path}</code>
          {i.action && <div><Badge kind="accent">{i.action}</Badge></div>}
        </>
      )
  }
}

/**
 * Everything everyone did, newest first: pages opened, clicks and choices in the browser and every request to
 * the server. System Administrators only. Filters live in the address so a view can be bookmarked or shared.
 */
export default function ActivityPage() {
  const [params, setParams] = useSearchParams()
  const [catalog, setCatalog] = useState<SystemCatalog | null>(null)
  const [data, setData] = useState<InteractionPage | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<number | null>(null)
  const [search, setSearch] = useState(params.get('search') ?? '')

  const f = Object.fromEntries(FILTERS.map((k) => [k, params.get(k) ?? ''])) as Record<(typeof FILTERS)[number], string>
  const from = f.from || daysAgo(6)
  const to = f.to || today()
  const query = { ...f, from, to, pageSize: 100 }

  const set = (changes: Partial<Record<(typeof FILTERS)[number], string>>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(changes)) (v ? next.set(k, v) : next.delete(k))
    if (!('page' in changes)) next.delete('page')
    setParams(next, { replace: true })
  }

  useEffect(() => {
    api.get<SystemCatalog>('/system/catalog').then(setCatalog).catch((e) => setError((e as Error).message))
  }, [])

  const key = params.toString()
  useEffect(() => {
    setLoading(true)
    setError(null)
    api
      .get<InteractionPage>(`/system/activity${qs(query)}`)
      .then(setData)
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])

  // Debounce the free-text search.
  useEffect(() => {
    if (search === f.search) return
    const t = window.setTimeout(() => set({ search }), 400)
    return () => window.clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search])

  const restaurants = useMemo(() => (catalog?.restaurants ?? []).filter((r) => !f.groupId || String(r.groupId) === f.groupId), [catalog, f.groupId])
  const people = useMemo(() => (catalog?.users ?? []).filter((u) => !f.groupId || String(u.groupId ?? '') === f.groupId || f.groupId === '0'), [catalog, f.groupId])
  const page = Number(f.page || 1)
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  const exportCsv = async () => {
    try {
      const blob = await api.blob(`/system/activity/export${qs({ ...query, page: undefined, pageSize: undefined })}`)
      downloadBlob(blob, `interaction-log-${from}-to-${to}.csv`)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>Activity log</h1>
        <span className="grow" />
        <Link className="btn" to="/reports">Reports</Link>
        <button type="button" className="btn" onClick={exportCsv} disabled={!data?.total}>
          ⬇ Download CSV
        </button>
      </div>
      <p className="muted">
        Every page opened, button clicked and choice made, and every request to the server, tagged with the person, investment group and
        restaurant. What people type into fields is never recorded. Only System Administrators can see this log.
      </p>
      <div className="filters">
        <label className="field">
          <span className="field-label">Investment group</span>
          <select value={f.groupId} onChange={(e) => set({ groupId: e.target.value, restaurantId: '' })}>
            <option value="">All groups</option>
            <option value="0">No group (system administration)</option>
            {catalog?.groups.map((g) => (
              <option key={g.id} value={g.id}>{g.name}</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Restaurant</span>
          <select value={f.restaurantId} onChange={(e) => set({ restaurantId: e.target.value })}>
            <option value="">All restaurants</option>
            {restaurants.map((r) => (
              <option key={r.id} value={r.id}>{r.name}</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">Person</span>
          <select value={f.userId} onChange={(e) => set({ userId: e.target.value })}>
            <option value="">Everyone</option>
            {people.map((u) => (
              <option key={u.id} value={u.id}>{u.displayName} ({u.roleTitle})</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">What</span>
          <select value={f.kind} onChange={(e) => set({ kind: e.target.value })}>
            <option value="">Everything</option>
            {KINDS.map((k) => (
              <option key={k.value} value={k.value}>{k.label}</option>
            ))}
          </select>
        </label>
        <label className="field">
          <span className="field-label">From</span>
          <input type="date" value={from} max={to} onChange={(e) => set({ from: e.target.value })} />
        </label>
        <label className="field">
          <span className="field-label">to</span>
          <input type="date" value={to} min={from} onChange={(e) => set({ to: e.target.value })} />
        </label>
        <label className="field wide">
          <span className="field-label">Search</span>
          <input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Page, action, button, person…" />
        </label>
        <label className="check" style={{ alignSelf: 'center' }}>
          <input type="checkbox" checked={f.errorsOnly === 'true'} onChange={(e) => set({ errorsOnly: e.target.checked ? 'true' : '' })} /> Errors and refusals only
        </label>
      </div>
      {f.sessionId && (
        <Alert kind="info" onClose={() => set({ sessionId: '' })}>
          Showing one browser visit (session {f.sessionId.slice(0, 8)}…). Close this to see everything again.
        </Alert>
      )}
      {error && <Alert kind="error">{error}</Alert>}
      {!data ? (
        loading && <Spinner />
      ) : data.total === 0 ? (
        <Empty>Nothing logged for these filters.</Empty>
      ) : (
        <div style={{ opacity: loading ? 0.6 : 1 }}>
          <p className="muted small">
            {data.total.toLocaleString()} entr{data.total === 1 ? 'y' : 'ies'} · newest first · click a row for details
          </p>
          <div className="table-wrap">
            <table className="activity-table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Who</th>
                  <th>Where</th>
                  <th>What</th>
                  <th className="num">Result</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((i) => (
                  <ActivityRow key={i.id} i={i} open={open === i.id} onToggle={() => setOpen(open === i.id ? null : i.id)} onSession={(s) => set({ sessionId: s })} />
                ))}
              </tbody>
            </table>
          </div>
          <div className="actions" style={{ justifyContent: 'center' }}>
            <button type="button" className="btn btn-sm" disabled={page <= 1} onClick={() => set({ page: String(page - 1) })}>
              ← Newer
            </button>
            <span className="muted" style={{ alignSelf: 'center' }}>Page {page} of {pages}</span>
            <button type="button" className="btn btn-sm" disabled={page >= pages} onClick={() => set({ page: String(page + 1) })}>
              Older →
            </button>
          </div>
        </div>
      )}
    </>
  )
}

function ActivityRow({ i, open, onToggle, onSession }: { i: InteractionDto; open: boolean; onToggle: () => void; onSession: (s: string) => void }) {
  const failed = (i.status ?? 0) >= 400
  return (
    <>
      <tr className={`clickable ${open ? 'open' : ''}`} onClick={onToggle}>
        <td style={{ whiteSpace: 'nowrap' }}>{fmtDateTime(i.at)}</td>
        <td>
          {i.displayName ?? i.userName ?? <span className="muted">not signed in</span>}
          {i.roleTitle && <div className="muted small">{i.roleTitle}{i.viewingAs ? ' · viewing as admin' : ''}</div>}
        </td>
        <td>
          {i.groupName ?? <span className="muted">—</span>}
          {i.restaurantName && <div className="muted small">{i.restaurantName}</div>}
        </td>
        <td>{describe(i)}</td>
        <td className="num">
          {i.status != null && <Badge kind={failed ? 'danger' : 'ok'}>{i.status}</Badge>}
          {i.durationMs != null && <div className="muted small">{i.durationMs} ms</div>}
        </td>
      </tr>
      {open && (
        <tr className="detail-row">
          <td colSpan={5}>
            <dl className="kv">
              <dt>Username</dt>
              <dd>{i.userName ?? '—'}</dd>
              <dt>Full path</dt>
              <dd><code>{i.path}</code></dd>
              <dt>Browser visit</dt>
              <dd>
                {i.sessionId ? (
                  <button type="button" className="link-btn" onClick={() => onSession(i.sessionId!)}>
                    Show everything in this visit
                  </button>
                ) : (
                  '—'
                )}
              </dd>
              <dt>IP address</dt>
              <dd>{i.ipAddress ?? '—'}</dd>
              <dt>Device</dt>
              <dd className="small">{i.userAgent ?? '—'}</dd>
            </dl>
          </td>
        </tr>
      )}
    </>
  )
}
