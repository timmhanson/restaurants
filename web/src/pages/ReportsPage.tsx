import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { CartesianGrid, Legend, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api, qs } from '../api/client'
import type { CardStats, GroupReportRow, RestaurantReportRow, SystemCatalog, SystemReport, UserReportRow } from '../api/types'
import DataTable, { type Column } from '../components/DataTable'
import { OTHER, SERIES } from '../components/palette'
import { Alert, Badge, Spinner, daysAgo, fmtDate, fmtDateTime, today } from '../components/ui'

type Metric = 'activeUsers' | 'interactions' | 'cards'
const METRICS: { key: Metric; label: string }[] = [
  { key: 'activeUsers', label: 'Active people' },
  { key: 'interactions', label: 'Interactions' },
  { key: 'cards', label: 'Cards entered' },
]
const pct = (s: CardStats) => (s.cards ? Math.round((100 * s.replied) / s.cards) : null)

/** Card figures shared by the group and restaurant tables. */
function cardColumns<T extends { stats: CardStats }>(): Column<T>[] {
  return [
    { key: 'cards', label: 'Cards', num: true, value: (r) => r.stats.cards },
    { key: 'replied', label: 'Replied %', num: true, value: (r) => pct(r.stats), render: (r) => (pct(r.stats) == null ? '—' : `${pct(r.stats)}%`) },
    { key: 'waiting', label: 'Waiting', num: true, value: (r) => r.stats.waiting },
    {
      key: 'overWeek',
      label: 'Over a week',
      num: true,
      value: (r) => r.stats.waitingOverAWeek,
      render: (r) => (r.stats.waitingOverAWeek > 0 ? <Badge kind="danger">{r.stats.waitingOverAWeek}</Badge> : 0),
    },
    { key: 'days', label: 'Days to reply', num: true, value: (r) => r.stats.avgDaysToReply ?? null },
    { key: 'smg', label: 'SMG', num: true, value: (r) => r.stats.smg },
    { key: 'web', label: 'Culvers.com', num: true, value: (r) => r.stats.website },
    { key: 'phone', label: 'Phone', num: true, value: (r) => r.stats.phone },
  ]
}

/**
 * System Administrator reports across investment groups and restaurants: usage (who signs in and works in the
 * site) next to the guest-card figures (how many came in and how quickly they were answered).
 */
export default function ReportsPage() {
  const [catalog, setCatalog] = useState<SystemCatalog | null>(null)
  const [from, setFrom] = useState(daysAgo(29))
  const [to, setTo] = useState(today())
  const [groupIds, setGroupIds] = useState<number[]>([])
  const [restaurantIds, setRestaurantIds] = useState<number[]>([])
  const [metric, setMetric] = useState<Metric>('activeUsers')
  const [data, setData] = useState<SystemReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<SystemCatalog>('/system/catalog').then(setCatalog).catch((e) => setError((e as Error).message))
  }, [])

  useEffect(() => {
    setLoading(true)
    setError(null)
    api
      .get<SystemReport>(`/system/reports${qs({ from, to, groupIds: groupIds.join(','), restaurantIds: restaurantIds.join(',') })}`)
      .then(setData)
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [from, to, groupIds, restaurantIds])

  const restaurantChoices = useMemo(
    () => (catalog?.restaurants ?? []).filter((r) => groupIds.length === 0 || groupIds.includes(r.groupId)),
    [catalog, groupIds],
  )
  const toggle = (list: number[], id: number) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id])
  const pickGroups = (ids: number[]) => {
    setGroupIds(ids)
    // Drop restaurants that are no longer in a picked group.
    setRestaurantIds((r) => r.filter((id) => ids.length === 0 || ids.includes(catalog?.restaurants.find((x) => x.id === id)?.groupId ?? -1)))
  }
  const preset = (days: number) => {
    setFrom(daysAgo(days - 1))
    setTo(today())
  }
  const range = `${from}-to-${to}`

  return (
    <>
      <div className="page-head">
        <h1>Reports</h1>
        <span className="grow" />
        <Link className="btn" to="/activity">Activity log</Link>
      </div>
      <p className="muted">
        Every investment group and restaurant in one place. Card figures count visits in the date range; activity counts what people did in the
        site in that range. Only System Administrators can see this page.
      </p>

      <div className="filters">
        <div className="field wide">
          <span className="field-label">Investment groups</span>
          <div className="chips" role="group" aria-label="Investment groups to include">
            <button type="button" className={`chip ${groupIds.length === 0 ? 'on' : ''}`} onClick={() => pickGroups([])}>
              All groups
            </button>
            {catalog?.groups.map((g) => (
              <button key={g.id} type="button" className={`chip ${groupIds.includes(g.id) ? 'on' : ''}`} aria-pressed={groupIds.includes(g.id)} onClick={() => pickGroups(toggle(groupIds, g.id))}>
                {g.name}
              </button>
            ))}
          </div>
        </div>
      </div>
      <div className="filters">
        <div className="field wide">
          <span className="field-label">Restaurants</span>
          <div className="chips" role="group" aria-label="Restaurants to include">
            <button type="button" className={`chip ${restaurantIds.length === 0 ? 'on' : ''}`} onClick={() => setRestaurantIds([])}>
              All {groupIds.length ? 'in these groups' : 'restaurants'} ({restaurantChoices.length})
            </button>
            {restaurantChoices.map((r) => (
              <button key={r.id} type="button" className={`chip ${restaurantIds.includes(r.id) ? 'on' : ''}`} aria-pressed={restaurantIds.includes(r.id)} onClick={() => setRestaurantIds(toggle(restaurantIds, r.id))}>
                {r.name}
              </button>
            ))}
          </div>
        </div>
      </div>
      <div className="filters">
        <label className="field">
          <span className="field-label">From</span>
          <input type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">to</span>
          <input type="date" value={to} min={from} max={today()} onChange={(e) => setTo(e.target.value)} />
        </label>
        <div className="chips" role="group" aria-label="Quick ranges" style={{ alignSelf: 'center' }}>
          {[7, 30, 90, 365].map((d) => (
            <button key={d} type="button" className="chip" onClick={() => preset(d)}>
              Last {d} days
            </button>
          ))}
        </div>
      </div>

      {error && <Alert kind="error">{error}</Alert>}
      {!data ? (
        loading && <Spinner />
      ) : (
        <div style={{ opacity: loading ? 0.6 : 1 }}>
          <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
            <Stat value={data.totals.cards} label={`cards (${data.groups} group${data.groups === 1 ? '' : 's'}, ${data.restaurants} restaurant${data.restaurants === 1 ? '' : 's'})`} />
            <Stat value={pct(data.totals) == null ? '—' : `${pct(data.totals)}%`} label={`replied (${data.totals.replied} of ${data.totals.cards})`} />
            <Stat value={data.totals.waitingOverAWeek} label="waiting over a week" warn={data.totals.waitingOverAWeek > 0} />
            <Stat value={data.totals.avgDaysToReply ?? '—'} label="average days from visit to reply" />
            <Stat value={data.activeUsers} label={`active people of ${data.users} with access`} />
            <Stat value={data.signIns} label={`sign-ins${data.failedSignIns ? ` (${data.failedSignIns} failed)` : ''}`} warn={data.failedSignIns > 0} />
            <Stat value={data.interactions.toLocaleString()} label="interactions logged" />
            <Stat value={`${data.totals.smg} / ${data.totals.website} / ${data.totals.phone}`} label="SMG / Culvers.com / phone" />
          </div>

          <DailyChart data={data} catalog={catalog} metric={metric} setMetric={setMetric} />

          <DataTable<GroupReportRow>
            title="By investment group"
            help="Click a group name to focus the report on it."
            rows={data.byGroup}
            rowKey={(r) => r.id}
            filename={`report-by-group-${range}.csv`}
            defaultSort={{ key: 'name', desc: false }}
            columns={[
              {
                key: 'name',
                label: 'Investment group',
                value: (r) => r.name,
                render: (r) => (
                  <>
                    <button type="button" className="link-btn" onClick={() => pickGroups([r.id])}>
                      <strong>{r.name}</strong>
                    </button>
                    {!r.isActive && <Badge kind="neutral">inactive</Badge>}
                  </>
                ),
              },
              { key: 'restaurants', label: 'Restaurants', num: true, value: (r) => r.restaurants },
              { key: 'users', label: 'People', num: true, value: (r) => r.users },
              { key: 'active', label: 'Active', num: true, value: (r) => r.activeUsers },
              { key: 'signIns', label: 'Sign-ins', num: true, value: (r) => r.signIns },
              { key: 'interactions', label: 'Interactions', num: true, value: (r) => r.interactions },
              { key: 'last', label: 'Last activity', value: (r) => r.lastActivity ?? null, render: (r) => (r.lastActivity ? fmtDateTime(r.lastActivity) : <span className="muted">none</span>) },
              ...cardColumns<GroupReportRow>(),
              { key: 'log', label: '', noCsv: true, noSort: true, value: () => null, render: (r) => <Link className="btn btn-sm" to={`/activity?groupId=${r.id}&from=${from}&to=${to}`}>Activity</Link> },
            ]}
          />

          <DataTable<RestaurantReportRow>
            title="By restaurant"
            help="Activity at a restaurant is what was done about it plus everything done by the people who work there."
            rows={data.byRestaurant}
            rowKey={(r) => r.id}
            filename={`report-by-restaurant-${range}.csv`}
            defaultSort={{ key: 'group', desc: false }}
            columns={[
              { key: 'group', label: 'Investment group', value: (r) => `${r.groupName} ${r.name}`, render: (r) => r.groupName },
              { key: 'name', label: 'Restaurant', value: (r) => r.name, render: (r) => <><strong>{r.name}</strong>{!r.isActive && <> <Badge kind="neutral">inactive</Badge></>}</> },
              { key: 'staff', label: 'Staff', num: true, value: (r) => r.staff },
              { key: 'active', label: 'Active', num: true, value: (r) => r.activeUsers },
              { key: 'interactions', label: 'Interactions', num: true, value: (r) => r.interactions },
              { key: 'last', label: 'Last activity', value: (r) => r.lastActivity ?? null, render: (r) => (r.lastActivity ? fmtDateTime(r.lastActivity) : <span className="muted">none</span>) },
              ...cardColumns<RestaurantReportRow>(),
              { key: 'lastVisit', label: 'Last visit', value: (r) => r.lastVisit ?? null, render: (r) => (r.lastVisit ? fmtDate(r.lastVisit) : <span className="muted">—</span>) },
              { key: 'log', label: '', noCsv: true, noSort: true, value: () => null, render: (r) => <Link className="btn btn-sm" to={`/activity?restaurantId=${r.id}&from=${from}&to=${to}`}>Activity</Link> },
            ]}
          />

          <DataTable<UserReportRow>
            title="Most active people"
            help="The 50 people with the most interactions in the range."
            rows={data.topUsers}
            rowKey={(r) => r.id}
            filename={`report-people-${range}.csv`}
            defaultSort={{ key: 'interactions', desc: true }}
            columns={[
              { key: 'name', label: 'Person', value: (r) => r.displayName, render: (r) => <><strong>{r.displayName}</strong><div className="muted small">{r.username}</div></> },
              { key: 'role', label: 'Role', value: (r) => r.roleTitle },
              { key: 'group', label: 'Investment group', value: (r) => r.groupName ?? '' },
              { key: 'interactions', label: 'Interactions', num: true, value: (r) => r.interactions },
              { key: 'signIns', label: 'Sign-ins', num: true, value: (r) => r.signIns },
              { key: 'pages', label: 'Pages viewed', num: true, value: (r) => r.pageViews },
              { key: 'cards', label: 'Cards entered', num: true, value: (r) => r.cardsEntered },
              { key: 'seen', label: 'Last seen', value: (r) => r.lastSeen ?? null, render: (r) => (r.lastSeen ? fmtDateTime(r.lastSeen) : '—') },
              { key: 'log', label: '', noCsv: true, noSort: true, value: () => null, render: (r) => <Link className="btn btn-sm" to={`/activity?userId=${r.id}&from=${from}&to=${to}`}>Activity</Link> },
            ]}
          />
        </div>
      )}
    </>
  )
}

function Stat({ value, label, warn = false }: { value: React.ReactNode; label: string; warn?: boolean }) {
  return (
    <div className="stat">
      <div className="stat-value" style={warn ? { color: 'var(--danger)' } : undefined}>{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  )
}

/** One line per investment group. Colours follow the group (catalog order), never its rank; beyond seven, groups fold into Other. */
function DailyChart({ data, catalog, metric, setMetric }: { data: SystemReport; catalog: SystemCatalog | null; metric: Metric; setMetric: (m: Metric) => void }) {
  const { rows, series } = useMemo(() => {
    const order = (catalog?.groups ?? []).map((g) => g.id)
    const present = [...new Set(data.daily.map((d) => d.groupId ?? 0))]
    const keyOf = (gid: number) => {
      const idx = order.indexOf(gid)
      return gid !== 0 && idx >= 0 && idx < SERIES.length - 1 ? `g${gid}` : 'other'
    }
    const name = (gid: number) => catalog?.groups.find((g) => g.id === gid)?.name ?? 'Group'
    const s = present
      .filter((gid) => keyOf(gid) !== 'other')
      .sort((a, b) => order.indexOf(a) - order.indexOf(b))
      .map((gid) => ({ key: keyOf(gid), name: name(gid), color: SERIES[order.indexOf(gid)] }))
    if (present.some((gid) => keyOf(gid) === 'other')) s.push({ key: 'other', name: present.includes(0) ? 'Other / no group' : 'Other groups', color: OTHER })
    // A row for every day in the range so quiet days show as zero.
    const byDay = new Map<string, Record<string, number | string>>()
    for (let d = new Date(data.from + 'T00:00:00'); d <= new Date(data.to + 'T00:00:00'); d.setDate(d.getDate() + 1)) {
      const iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
      byDay.set(iso, Object.fromEntries([['date', iso], ...s.map((x) => [x.key, 0])]))
    }
    for (const d of data.daily) {
      const row = byDay.get(d.date)
      if (!row) continue
      const k = keyOf(d.groupId ?? 0)
      row[k] = (row[k] as number) + d[metric]
    }
    return { rows: [...byDay.values()], series: s }
  }, [data, catalog, metric])

  return (
    <div className="panel">
      <div className="page-head">
        <h2 style={{ margin: 0 }}>Day by day</h2>
        <span className="grow" />
        <div className="chips" role="group" aria-label="What to chart">
          {METRICS.map((m) => (
            <button key={m.key} type="button" className={`chip ${metric === m.key ? 'on' : ''}`} aria-pressed={metric === m.key} onClick={() => setMetric(m.key)}>
              {m.label}
            </button>
          ))}
        </div>
      </div>
      {series.length === 0 ? (
        <p className="muted">No activity in this range.</p>
      ) : (
        <div className="chart-box">
          <ResponsiveContainer>
            <LineChart data={rows} margin={{ top: 8, right: 16, left: 0, bottom: 0 }}>
              <CartesianGrid strokeDasharray="3 3" vertical={false} />
              <XAxis dataKey="date" tickFormatter={(d: string) => fmtDate(d).replace(/, \d{4}$/, '')} minTickGap={24} />
              <YAxis allowDecimals={false} />
              <Tooltip labelFormatter={(d) => fmtDate(String(d))} />
              {series.length > 1 && <Legend />}
              {series.map((s) => (
                <Line key={s.key} type="monotone" dataKey={s.key} name={s.name} stroke={s.color} strokeWidth={2} dot={false} activeDot={{ r: 4 }} />
              ))}
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}
    </div>
  )
}
