import { useEffect, useState } from 'react'
import { Area, AreaChart, Bar, BarChart, CartesianGrid, Cell, Legend, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { api, qs } from '../api/client'
import type { AnalyticsDto, CountItem } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Empty, Field, Spinner, daysAgo, fmtDate } from '../components/ui'

const DAY_ORDER = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
const PALETTE = ['#005daa', '#e4572e', '#17a398', '#f3a712', '#7b61ff', '#a1c181', '#8d99ae', '#ef476f', '#06d6a0', '#ffd166']

function withOpen(items: CountItem[]) {
  return items.map((i) => ({ ...i, responded: i.responded ?? 0, open: i.count - (i.responded ?? 0) }))
}

export default function AnalyticsPage() {
  const { restaurants } = useAuth()
  const [restaurantId, setRestaurantId] = useState<number | ''>('')
  const [from, setFrom] = useState(daysAgo(180))
  const [to, setTo] = useState('')
  const [trend, setTrend] = useState<'week' | 'month'>('week')
  const [data, setData] = useState<AnalyticsDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    setLoading(true)
    api
      .get<AnalyticsDto>(`/analytics${qs({ restaurantId, from, to })}`)
      .then((d) => {
        setData(d)
        setError(null)
      })
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [restaurantId, from, to])

  const days = data ? DAY_ORDER.map((d) => withOpen(data.byDayOfWeek).find((x) => x.label === d) ?? { label: d, count: 0, responded: 0, open: 0 }).map((d) => ({ ...d, label: d.label.slice(0, 3) })) : []
  const trendData = data ? withOpen(trend === 'week' ? data.byWeek : data.byMonth).map((x) => ({ ...x, label: trend === 'week' ? fmtDate(x.label) : x.label })) : []
  const pct = data && data.totalComments ? Math.round((data.responded / data.totalComments) * 100) : 0

  return (
    <>
      <div className="page-head">
        <h1>Analytics</h1>
      </div>
      <div className="filters">
        <Field label="Restaurant">
          <select value={restaurantId} onChange={(e) => setRestaurantId(e.target.value ? Number(e.target.value) : '')}>
            <option value="">All my restaurants</option>
            {restaurants.map((r) => (
              <option key={r.id} value={r.id}>{r.name}</option>
            ))}
          </select>
        </Field>
        <Field label="Visits from">
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label="to">
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </Field>
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
            <div className="stat"><div className="stat-value">{data.unresponded}</div><div className="stat-label">Not yet responded ({100 - pct}%)</div></div>
            <div className="stat"><div className="stat-value">{data.repeatGuests}<small className="muted"> / {data.distinctGuests}</small></div><div className="stat-label">Repeat guests / distinct guests</div></div>
            <div className="stat"><div className="stat-value">{data.avgPerWeek}</div><div className="stat-label">Average cards per week</div></div>
          </div>

          <div className="grid grid-2">
            <div className="panel">
              <h2>By day of week</h2>
              <div className="chart-box">
                <ResponsiveContainer>
                  <BarChart data={days}>
                    <CartesianGrid strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="label" /><YAxis allowDecimals={false} /><Tooltip /><Legend />
                    <Bar dataKey="responded" name="Responded" stackId="a" fill="#17a398" />
                    <Bar dataKey="open" name="Not responded" stackId="a" fill="#e4572e" radius={[4, 4, 0, 0]} />
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
                    <Bar dataKey="responded" name="Responded" stackId="a" fill="#17a398" />
                    <Bar dataKey="open" name="Not responded" stackId="a" fill="#e4572e" radius={[0, 4, 4, 0]} />
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
                    <Area type="monotone" dataKey="count" name="Cards" stroke="var(--accent)" fill="var(--accent)" fillOpacity={0.15} />
                    <Area type="monotone" dataKey="responded" name="Responded" stroke="#17a398" fill="#17a398" fillOpacity={0.15} />
                  </AreaChart>
                </ResponsiveContainer>
              </div>
            </div>
            <div className="panel">
              <h2>What we sent</h2>
              <div className="chart-box">
                <ResponsiveContainer>
                  <PieChart>
                    <Pie data={data.byCouponOffer} dataKey="count" nameKey="label" outerRadius={90} innerRadius={45} paddingAngle={2}>
                      {data.byCouponOffer.map((_, i) => <Cell key={i} fill={PALETTE[i % PALETTE.length]} />)}
                    </Pie>
                    <Tooltip />
                    <Legend />
                  </PieChart>
                </ResponsiveContainer>
              </div>
            </div>
            <div className="panel">
              <h2>{restaurantId ? 'Entered by' : 'By restaurant'}</h2>
              <div className="chart-box">
                <ResponsiveContainer>
                  <BarChart data={withOpen(restaurantId ? data.byEnteredBy : data.byRestaurant)}>
                    <CartesianGrid strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="label" /><YAxis allowDecimals={false} /><Tooltip /><Legend />
                    <Bar dataKey="responded" name="Responded" stackId="a" fill="#17a398" />
                    <Bar dataKey="open" name="Not responded" stackId="a" fill="#e4572e" radius={[4, 4, 0, 0]} />
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
