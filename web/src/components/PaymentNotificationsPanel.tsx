import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { PaymentNotification, PaymentNotificationDetail } from '../api/types'
import { Alert, Badge, Empty, Spinner, fmtDateTime } from './ui'

const money = (cents: number, currency?: string | null) =>
  `${(currency ?? 'usd').toLowerCase() === 'usd' ? '$' : ''}${(cents / 100).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}${
    currency && currency.toLowerCase() !== 'usd' ? ` ${currency.toUpperCase()}` : ''}`

/** A notification's badge: money arriving is good, a failure or money going back is not. */
export function NotificationBadge({ n }: { n: PaymentNotification }) {
  const kind = n.needsALook || n.type.endsWith('failed') ? 'danger' : n.type.endsWith('succeeded') ? 'ok' : n.type.endsWith('processing') ? 'accent' : 'neutral'
  return <Badge kind={kind} title={n.type}>{n.label}</Badge>
}

/**
 * What Stripe has told the site: every notification its webhook has received, newest first, for the System
 * Administrator. A row opens to show every data point Stripe sent in it. Payer details are in there, so the
 * rows are marked data-no-track and nothing from them reaches the activity log.
 */
export default function PaymentNotificationsPanel({ groupId, refreshKey }: { groupId: string; refreshKey?: unknown }) {
  const [rows, setRows] = useState<PaymentNotification[] | null>(null)
  const [kind, setKind] = useState('all')
  const [open, setOpen] = useState<number | null>(null)
  const [detail, setDetail] = useState<PaymentNotificationDetail | null>(null)
  const [find, setFind] = useState('')
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api.get<PaymentNotification[]>(`/system/billing/notifications?groupId=${encodeURIComponent(groupId)}&kind=${kind}&take=200`)
      .then(setRows).catch((e) => setError((e as Error).message))
  }, [groupId, kind])
  useEffect(() => { load() }, [load, refreshKey])

  const toggle = (n: PaymentNotification) => {
    if (open === n.id) return setOpen(null)
    setOpen(n.id)
    setDetail(null)
    setFind('')
    api.get<PaymentNotificationDetail>(`/system/billing/notifications/${n.id}`).then(setDetail).catch((e) => setError((e as Error).message))
  }
  const shown = detail?.dataPoints.filter((d) => !find.trim() || `${d.name} ${d.value}`.toLowerCase().includes(find.trim().toLowerCase())) ?? []

  return (
    <div className="panel">
      <h2>Payment notifications from Stripe</h2>
      <p className="muted">
        Everything Stripe has told this site through its webhook, kept whole: payments arriving, failing or being cancelled, refunds and disputes. Open one to see
        every data point Stripe sent. Refunds and disputes are marked, because money that came in has gone back out or may. Stripe never sends a card or bank
        account number, only the last four digits.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="toolbar">
        <label>
          Show
          <select value={kind} onChange={(e) => { setKind(e.target.value); setOpen(null) }}>
            <option value="all">Every notification</option>
            <option value="bills">Only those about a bill</option>
            <option value="look">Refunds and disputes</option>
          </select>
        </label>
        <button type="button" className="btn btn-sm" onClick={load}>Refresh</button>
      </div>
      {!rows ? (!error && <Spinner />) : rows.length === 0 ? (
        <Empty>
          {kind === 'all'
            ? 'Nothing has arrived yet. Once the webhook is added in Stripe and its signing secret is saved on Connected Systems, each notification shows here as it comes.'
            : 'No notifications of that kind.'}
        </Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Received</th><th>What happened</th><th>Bill</th><th>Group</th><th>Amount</th><th>Stripe says</th><th>What the site did</th></tr></thead>
            <tbody>
              {rows.map((n) => (
                <NotificationRows key={n.id} n={n} open={open === n.id} onToggle={() => toggle(n)}>
                  {open === n.id && (
                    <tr data-no-track>
                      <td colSpan={7} style={{ background: 'var(--surface-2, #f7f8fa)' }}>
                        {!detail ? <Spinner /> : (
                          <>
                            <div className="toolbar" style={{ marginBottom: 6 }}>
                              <strong>{detail.dataPoints.length} data points</strong>
                              <span className="muted small">
                                Stripe’s id <code>{n.eventId}</code>{n.deliveries > 1 ? ` · sent ${n.deliveries} times` : ''}{n.happenedAt ? ` · happened ${fmtDateTime(n.happenedAt)}` : ''}
                              </span>
                              <span className="grow" />
                              <input value={find} onChange={(e) => setFind(e.target.value)} placeholder="Find a data point" aria-label="Find a data point in this notification" style={{ maxWidth: 220 }} />
                            </div>
                            <div className="table-wrap" style={{ maxHeight: 360, overflow: 'auto' }}>
                              <table className="data-points">
                                <tbody>
                                  {shown.map((d) => <tr key={d.name}><td><code>{d.name}</code></td><td style={{ wordBreak: 'break-word' }}>{d.value}</td></tr>)}
                                  {shown.length === 0 && <tr><td colSpan={2} className="muted">Nothing matches.</td></tr>}
                                </tbody>
                              </table>
                            </div>
                          </>
                        )}
                      </td>
                    </tr>
                  )}
                </NotificationRows>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function NotificationRows({ n, open, onToggle, children }: { n: PaymentNotification; open: boolean; onToggle: () => void; children: React.ReactNode }) {
  return (
    <>
      <tr>
        <td>
          <button type="button" className="btn-link" onClick={onToggle} aria-expanded={open} aria-label={`${open ? 'Hide' : 'Show'} every data point of the notification received ${fmtDateTime(n.receivedAt)}`}>
            {fmtDateTime(n.receivedAt)}
          </button>
        </td>
        <td><NotificationBadge n={n} /> {n.test && <Badge kind="warn" title="Sent by Stripe in test mode: no real money moved">test</Badge>}<div className="muted small"><code>{n.type}</code></div></td>
        <td style={{ whiteSpace: 'nowrap' }}>{n.bill ?? <span className="muted">not a bill here</span>}</td>
        <td>{n.group ?? <span className="muted">—</span>}</td>
        <td>{n.amountCents != null ? money(n.amountCents, n.currency) : <span className="muted">—</span>}</td>
        <td>{n.status ?? <span className="muted">—</span>}{n.reason && <div className="small" style={{ color: 'var(--danger)' }}>{n.reason}</div>}</td>
        <td className="small">{n.outcome ?? <span className="muted">—</span>}</td>
      </tr>
      {children}
    </>
  )
}
