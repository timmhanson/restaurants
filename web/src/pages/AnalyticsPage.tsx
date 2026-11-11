import { useEffect, useMemo, useState } from 'react'
import { Area, AreaChart, Bar, BarChart, CartesianGrid, Cell, Legend, Line, LineChart, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api, downloadBlob, qs } from '../api/client'
import type { AnalyticsDto, CountItem, RestaurantRollup } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Empty, Spinner, daysAgo, fmtDate } from '../components/ui'

const DAY_ORDER = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
/** Categorical palette, fixed order, validated for colour-vision deficiency. Never cycled: extra series fold into "Other". */
const SERIES = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#6250d6']
const OTHER = '#8d99ae'
const REPLIED = '#1baf7a'
const WAITING = '#eb6834'

function withOpen(items: CountItem[]) {
  return items.map((i) => ({ ...i, responded: i.responded ?? 0, open: i.count - (i.responded ?? 0) }))
}

/** Keeps the largest categories and folds the rest into "Other" so colours never repeat. */
function foldTop(items: CountItem[], keep = SERIES.length - 1) {
  if (items.length <= SERIES.length) return items
  const top = items.slice(0, keep)
  const rest = items.slice(keep)
  return [...top, { label: 'Other', count: rest.reduce((n, i) => n + i.count, 0) }]
}

type SortKey = keyof Pick<RestaurantRollup, 'name' | 'total' | 'responseRate' | 'waiting' | 'waitingOverAWeek' | 'oldestWaitingDays' | 'avgDaysToReply' | 'repeatGuests' | 'avgPerWeek'>

export default function AnalyticsPage() {
  const { restaurants } = useAuth()
  const [picked, setPicked] = useState<number[]>([])
  const [from, setFrom] = useState(daysAgo(180))
  const [to, setTo] = useState('')
  const [trend, setTrend] = useState<'week' | 'month'>('week')
  const [data, setData] = useState<AnalyticsDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    setLoading(true)
    api
      .get<AnalyticsDto>(`/analytics${qs({ restaurantIds: picked.join(','), from, to })}`)
      .then((d) => {
        setData(d)
        setError(null)
      })
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [picked, from, to])

  const togglePick = (id: number) => setPicked((p) => (p.includes(id) ? p.filter((x) => x !== id) : [...p, id]))
  const multi = (data?.restaurants.length ?? 0) > 1

  const days = data ? DAY_ORDER.map((d) => withOpen(data.byDayOfWeek).find((x) => x.label === d) ?? { label: d, count: 0, responded: 0, open: 0 }).map((d) => ({ ...d, label: d.label.slice(0, 3) })) : []
  const trendData = data ? withOpen(trend === 'week' ? data.byWeek : data.byMonth).map((x) => ({ ...x, label: trend === 'week' ? fmtDate(x.label) : x.label })) : []
  const pct = data && data.totalComments ? Math.round((data.responded / data.totalComments) * 100) : 0

  return (
    <>
      <div className="page-head">
        <h1>Analytics</h1>
      </div>
      <div className="filters">
        {restaurants.length > 1 && (
          <div className="field wide">
            <span className="field-label">Restaurants</span>
            <div className="chips" role="group" aria-label="Restaurants to include">
              <button type="button" className={`chip ${picked.length === 0 ? 'on' : ''}`} onClick={() => setPicked([])}>All my restaurants ({restaurants.length})</button>
              {restaurants.map((r) => (
                <button key={r.id} type="button" className={`chip ${picked.includes(r.id) ? 'on' : ''}`} aria-pressed={picked.includes(r.id)} onClick={() => togglePick(r.id)}>
                  {r.name}
                </button>
              ))}
            </div>
          </div>
        )}
        <label className="field">
          <span className="field-label">Visits from</span>
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label className="field">
          <span className="field-label">to</span>
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>
      {error && <Alert kind="error">{error}</Alert>}
      {loading && !data ? (
        <Spinner />
      ) : !data ? null : data.totalComments === 0 ? (
        <Empty>No cards in this range yet.</Empty>
      ) : (
        <>
          <div className="grid grid-4" style={{ marginBottom: '1rem' }}>
            <div className="stat"><div className="stat-value">{data.totalComments}</div><div className="stat-label">Cards issued</div></div>
            <div className="stat">
              <div className="stat-value">{data.unresponded}</div>
              <div className="stat-label">Waiting for a reply ({100 - pct}%){data.waitingOverAWeek > 0 && <> · <strong className="text-danger">{data.waitingOverAWeek} over a week</strong></>}</div>
            </div>
            <div className="stat">
              <div className="stat-value">{data.avgDaysToReply ?? '—'}{data.avgDaysToReply != null && <small className="muted"> days</small>}</div>
              <div className="stat-label">Average time from visit to reply</div>
            </div>
            <div className="stat">
              <div className="stat-value">{data.repeatGuests}<small className="muted"> / {data.distinctGuests}</small></div>
              <div className="stat-label">Repeat guests / distinct guests{data.crossRestaurantGuests > 0 && ` · ${data.crossRestaurantGuests} at more than one restaurant`}</div>
            </div>
          </div>

          {multi && <Comparison rows={data.restaurants} from={from} to={to} />}
          {multi && <MonthlyByRestaurant data={data} />}

          <div className="grid grid-2">
            <div className="panel">
              <h2>By day of week</h2>
              <div className="chart-box">
                <ResponsiveContainer>
                  <BarChart data={days}>
                    <CartesianGrid strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="label" /><YAxis allowDecimals={false} /><Tooltip /><Legend />
                    <Bar dataKey="responded" name="Replied" stackId="a" fill={REPLIED} />
                    <Bar dataKey="open" name="Waiting" stackId="a" fill={WAITING} radius={[4, 4, 0, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </div>
            <div className="panel">
              <h2>By reason</h2>
              <div className="chart-box">
                <ResponsiveContainer>
                  <BarChart data={withOpen(data.byReason)} layout="vertical" margin={{ left: 30 }}>
                    <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                    <XAxis type="number" allowDecimals={false} /><YAxis type="category" dataKey="label" width={120} /><Tooltip /><Legend />
                    <Bar dataKey="responded" name="Replied" stackId="a" fill={REPLIED} />
                    <Bar dataKey="open" name="Waiting" stackId="a" fill={WAITING} radius={[0, 4, 4, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </div>
            <div className="panel" style={{ gridColumn: '1 / -1' }}>
              <div className="page-head">
                <h2 style={{ margin: 0 }}>Trend</h2>
                <span className="grow" />
                <div className="toggle">
                  <button type="button" className={trend === 'week' ? 'on' : ''} onClick={() => setTrend('week')}>By week</button>
                  <button type="button" className={trend === 'month' ? 'on' : ''} onClick={() => setTrend('month')}>By month</button>
                </div>
              </div>
              <div className="chart-box">
                <ResponsiveContainer>
                  <AreaChart data={trendData}>
                    <CartesianGrid strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="label" /><YAxis allowDecimals={false} /><Tooltip /><Legend />
                    <Area type="monotone" dataKey="count" name="Cards" stroke={SERIES[0]} fill={SERIES[0]} fillOpacity={0.12} strokeWidth={2} />
                    <Area type="monotone" dataKey="responded" name="Replied" stroke={REPLIED} fill={REPLIED} fillOpacity={0.15} strokeWidth={2} />
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            </div>
            <div className="panel">
              <h2>What we sent</h2>
              <div className="chart-box">
                <ResponsiveContainer>
                  <PieChart>
                    <Pie data={foldTop(data.byCouponOffer)} dataKey="count" nameKey="label" outerRadius={90} innerRadius={45} paddingAngle={2}>
                      {foldTop(data.byCouponOffer).map((c, i) => <Cell key={c.label} fill={c.label === 'Other' ? OTHER : SERIES[i]} />)}
                    </Pie>
                    <Tooltip />
                    <Legend />
                  </PieChart>
                </ResponsiveContainer>
              </div>
            </div>
            <div className="panel">
              <h2>How comments came in</h2>
              {data.bySource.length === 0 ? (
                <Empty>No comments in this range.</Empty>
              ) : (
                <div className="chart-box">
                  <ResponsiveContainer>
                    <BarChart data={withOpen(data.bySource)} layout="vertical" margin={{ left: 30 }}>
                      <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                      <XAxis type="number" allowDecimals={false} /><YAxis type="category" dataKey="label" width={150} /><Tooltip /><Legend />
                      <Bar dataKey="responded" name="Replied" stackId="a" fill={REPLIED} />
                      <Bar dataKey="open" name="Waiting" stackId="a" fill={WAITING} radius={[0, 4, 4, 0]} />
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              )}
            </div>
            <div className="panel">
              <h2>How we replied</h2>
              {data.byReplyMethod.length === 0 ? (
                <Empty>No replies recorded in this range yet.</Empty>
              ) : (
                <div className="chart-box">
                  <ResponsiveContainer>
                    <BarChart data={data.byReplyMethod} layout="vertical" margin={{ left: 30 }}>
                      <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                      <XAxis type="number" allowDecimals={false} /><YAxis type="category" dataKey="label" width={130} /><Tooltip />
                      <Bar dataKey="count" name="Replies" fill={SERIES[0]} radius={[0, 4, 4, 0]} />
                    </BarChart>
                  </ResponsiveContainer>
                </div>
              )}
            </div>
            <div className="panel">
              <h2>{multi ? 'By restaurant' : 'Entered by'}</h2>
              <div className="chart-box">
                <ResponsiveContainer>
                  <BarChart data={withOpen(multi ? data.byRestaurant : data.byEnteredBy)}>
                    <CartesianGrid strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="label" /><YAxis allowDecimals={false} /><Tooltip /><Legend />
                    <Bar dataKey="responded" name="Replied" stackId="a" fill={REPLIED} />
                    <Bar dataKey="open" name="Waiting" stackId="a" fill={WAITING} radius={[4, 4, 0, 0]} />
                  </BarChart>
                </ResponsiveContainer>
              </div>
            </div>
          </div>

          <div className="panel">
            <h2>Repeat guests</h2>
            <p className="legend-note">Guests with more than one card on file in this range. Frequent names may be worth a closer look.</p>
            {data.topRepeatGuests.length === 0 ? (
              <Empty>No repeat guests in this range.</Empty>
            ) : (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr><th>Guest</th><th>Contact</th><th className="num">Cards</th><th>Last visit</th><th>Restaurants</th><th>Reasons</th></tr>
                  </thead>
                  <tbody>
                    {data.topRepeatGuests.map((g) => (
                      <tr key={g.guestKey}>
                        <td>{g.name}</td>
                        <td>{g.phone || g.address || <span className="muted">—</span>}</td>
                        <td className="num"><Badge kind={g.count >= 3 ? 'danger' : 'warn'}>{g.count}</Badge></td>
                        <td>{fmtDate(g.lastVisit)}</td>
                        <td>{g.restaurants.join(', ')}</td>
                        <td>{g.reasons.join(', ')}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </>
      )}
    </>
  )
}

/** Side-by-side figures for each restaurant in the roll-up, sortable, with a CSV download. */
function Comparison({ rows, from, to }: { rows: RestaurantRollup[]; from: string; to: string }) {
  const [sort, setSort] = useState<SortKey>('total')
  const [desc, setDesc] = useState(true)
  const sorted = useMemo(() => {
    const val = (r: RestaurantRollup) => r[sort] ?? -1
    return [...rows].sort((a, b) => {
      const x = val(a), y = val(b)
      const c = typeof x === 'string' ? x.localeCompare(String(y)) : (x as number) - (y as number)
      return desc ? -c : c
    })
  }, [rows, sort, desc])
  const head = (key: SortKey, label: string, num = true) => (
    <th className={`sortable ${num ? 'num' : ''}`} onClick={() => (sort === key ? setDesc(!desc) : (setSort(key), setDesc(key !== 'name')))} aria-sort={sort === key ? (desc ? 'descending' : 'ascending') : 'none'}>
      {label}{sort === key ? (desc ? ' ↓' : ' ↑') : ''}
    </th>
  )
  const total = rows.reduce((n, r) => n + r.total, 0)

  const download = () => {
    const cols = ['Restaurant', 'Cards', 'Replied', 'Waiting', 'Reply rate %', 'Waiting over a week', 'Oldest wait (days)', 'Avg days to reply', 'Guests', 'Repeat guests', 'Cards per week', 'Top reason', 'Last visit']
    const esc = (v: unknown) => {
      const t = v == null ? '' : String(v)
      return /[",\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t
    }
    const lines = [cols.join(','), ...sorted.map((r) => [r.name, r.total, r.responded, r.waiting, r.responseRate, r.waitingOverAWeek, r.oldestWaitingDays, r.avgDaysToReply, r.guests, r.repeatGuests, r.avgPerWeek, r.topReason, r.lastVisit].map(esc).join(','))]
    downloadBlob(new Blob(['\ufeff' + lines.join('\r\n')], { type: 'text/csv' }), `restaurant-rollup-${from || 'start'}-to-${to || 'today'}.csv`)
  }

  return (
    <div className="panel">
      <div className="page-head">
        <h2 style={{ margin: 0 }}>Restaurant comparison</h2>
        <span className="grow" />
        <button type="button" className="btn btn-sm" onClick={download}>⬇ Download roll-up</button>
      </div>
      <p className="legend-note">Each restaurant's cards and how quickly they were answered. Click a column to sort.</p>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              {head('name', 'Restaurant', false)}
              {head('total', 'Cards')}
              {head('responseRate', 'Replied')}
              {head('waiting', 'Waiting')}
              {head('waitingOverAWeek', 'Over a week')}
              {head('oldestWaitingDays', 'Oldest wait')}
              {head('avgDaysToReply', 'Days to reply')}
              {head('repeatGuests', 'Repeat guests')}
              {head('avgPerWeek', 'Per week')}
              <th>Top reason</th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((r) => (
              <tr key={r.restaurantId}>
                <td><strong>{r.name}</strong>{r.lastVisit && <div className="muted small">last visit {fmtDate(r.lastVisit)}</div>}</td>
                <td className="num">
                  {r.total}
                  <div className="share" aria-hidden="true"><span style={{ width: `${total ? (100 * r.total) / total : 0}%` }} /></div>
                </td>
                <td className="num">{r.total ? `${r.responseRate}%` : '—'}</td>
                <td className="num">{r.waiting}</td>
                <td className="num">{r.waitingOverAWeek > 0 ? <Badge kind="danger">{r.waitingOverAWeek}</Badge> : 0}</td>
                <td className="num">{r.oldestWaitingDays != null ? `${r.oldestWaitingDays} d` : '—'}</td>
                <td className="num">{r.avgDaysToReply ?? '—'}</td>
                <td className="num">{r.repeatGuests}<span className="muted"> / {r.guests}</span></td>
                <td className="num">{r.avgPerWeek}</td>
                <td>{r.topReason ?? <span className="muted">—</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

/** Cards per month, one line per restaurant. Beyond seven restaurants the smallest fold into "Other". */
function MonthlyByRestaurant({ data }: { data: AnalyticsDto }) {
  const { chartRows, series } = useMemo(() => {
    const totals = data.restaurants.filter((r) => r.total > 0).sort((a, b) => b.total - a.total)
    const shown = totals.length > SERIES.length ? totals.slice(0, SERIES.length - 1) : totals
    const shownIds = new Set(shown.map((r) => r.restaurantId))
    const keyOf = (id: number) => (shownIds.has(id) ? `r${id}` : 'other')
    const months = [...new Set(data.monthlyByRestaurant.map((m) => m.month))].sort()
    const rows = months.map((month) => {
      const row: Record<string, string | number> = { month }
      shown.forEach((r) => (row[`r${r.restaurantId}`] = 0))
      if (totals.length > shown.length) row.other = 0
      data.monthlyByRestaurant.filter((m) => m.month === month).forEach((m) => (row[keyOf(m.restaurantId)] = Number(row[keyOf(m.restaurantId)] ?? 0) + m.count))
      return row
    })
    const s = shown.map((r, i) => ({ key: `r${r.restaurantId}`, name: r.name, color: SERIES[i] }))
    if (totals.length > shown.length) s.push({ key: 'other', name: `Other restaurants (${totals.length - shown.length})`, color: OTHER })
    return { chartRows: rows, series: s }
  }, [data])

  if (chartRows.length < 2) return null
  return (
    <div className="panel">
      <h2>Cards per month by restaurant</h2>
      <div className="chart-box">
        <ResponsiveContainer>
          <LineChart data={chartRows} margin={{ right: 16 }}>
            <CartesianGrid strokeDasharray="3 3" vertical={false} />
            <XAxis dataKey="month" /><YAxis allowDecimals={false} />
            <Tooltip />
            <Legend />
            {series.map((s) => (
              <Line key={s.key} type="monotone" dataKey={s.key} name={s.name} stroke={s.color} strokeWidth={2} dot={{ r: 4, fill: s.color, strokeWidth: 2, stroke: 'var(--surface)' }} activeDot={{ r: 5, fill: s.color, stroke: 'var(--surface)', strokeWidth: 2 }} />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </div>
    </div>
  )
}
