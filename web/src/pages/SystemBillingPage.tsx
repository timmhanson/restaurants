import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { BillingSettings, GroupBillingRow, SiteMailStatement, SystemBilling, SystemInvoice } from '../api/types'
import Stat from '../components/Stat'
import { Alert, Badge, Confirm, Empty, Field, Modal, Spinner, fmtDate, fmtDateTime, fmtDay, today } from '../components/ui'

export const money = (cents: number) => `$${(cents / 100).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`
const toCents = (dollars: string) => Math.round(Number(dollars) * 100)
const dollars = (cents: number) => (cents / 100).toFixed(2)

export const INVOICE_STATUS: Record<string, { label: string; kind: 'neutral' | 'warn' | 'danger' | 'ok' | 'accent' }> = {
  open: { label: 'Unpaid', kind: 'warn' },
  processing: { label: 'Payment on its way', kind: 'accent' },
  paid: { label: 'Paid', kind: 'ok' },
  void: { label: 'Cancelled', kind: 'neutral' },
}
const HOW: Record<string, string> = { check: 'Check', bank: 'Bank transfer', card: 'Card', cash: 'Cash', other: 'Other' }

/**
 * Billing, for the System Administrator. The site pays the mail house for every card it mails, then bills each
 * investment group: the cost plus the site's fee. Here: the prices and the fee (for everyone and per group), monthly
 * limits, when and how bills go out, every bill, and marking the ones that have been paid.
 */
export default function SystemBillingPage() {
  const [params, setParams] = useSearchParams()
  const [data, setData] = useState<SystemBilling | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [editGroup, setEditGroup] = useState<GroupBillingRow | null>(null)
  const [paying, setPaying] = useState<SystemInvoice | null>(null)
  const [voiding, setVoiding] = useState<SystemInvoice | null>(null)
  const [open, setOpen] = useState<number | null>(null)
  const groupId = params.get('groupId') ?? ''
  const status = params.get('status') ?? ''
  const month = params.get('month') ?? ''

  const query = `?groupId=${encodeURIComponent(groupId)}&status=${encodeURIComponent(status)}&month=${encodeURIComponent(month)}`
  const load = useCallback(() => {
    api.get<SystemBilling>(`/system/billing${query}`).then(setData).catch((e) => setError((e as Error).message))
  }, [query])
  useEffect(load, [load])

  const setFilter = (name: string, value: string) => {
    const next = new URLSearchParams(params)
    if (value) next.set(name, value)
    else next.delete(name)
    setParams(next, { replace: true })
  }

  /** Runs one action, shows what happened, and reloads with the current filters. */
  const act = async (work: () => Promise<{ message?: string } | unknown>) => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const result = (await work()) as { message?: string } | null
      if (result && typeof result === 'object' && 'message' in result && result.message) setNotice(result.message)
      load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (!data) return error ? <Alert kind="error">{error}</Alert> : <Spinner />
  const months = [...new Set(data.invoices.map((i) => i.month))].sort().reverse()

  return (
    <>
      <div className="page-head">
        <h1>Billing</h1>
      </div>
      <p className="muted">
        The site pays the mail house for each card it prints and mails, then bills each investment group for its cards: the cost plus your fee. Groups see
        one price per card and never the split.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {!data.mailReady && (
        <Alert kind="warn">No mailing key has been entered, so no group can have cards mailed yet. <Link to="/connected-systems">Enter it on Connected Systems</Link>.</Alert>
      )}
      {data.mailReady && data.mailTest && <Alert kind="info">The mailing key is a test key: nothing is printed, mailed or billed until a live key is entered on Connected Systems.</Alert>}
      {!data.emailReady && data.settings.emailBills && (
        <Alert kind="warn">Email is not set up on this site, so bills cannot be emailed. <Link to="/system">System status</Link> shows the email service.</Alert>
      )}

      <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
        <Stat value={money(data.owedCents)} label="owed to you (unpaid bills)" />
        <Stat value={money(data.overdueCents)} label="of that, overdue" warn={data.overdueCents > 0} />
        <Stat value={money(data.groups.reduce((n, g) => n + g.billedThisMonthCents, 0))} label="mailed so far this month (billed next month)" />
        <Stat value={data.payReady ? (data.payTest ? 'Test mode' : 'On') : 'Off'} label="paying on the site (Stripe)" />
      </div>

      {data.unbilledMonths.length > 0 && (
        <div className="panel">
          <h2>Months waiting to be billed</h2>
          <p className="muted">
            Bills are created by themselves on day {data.settings.billDay} of each month for the month before. These finished months have mailed cards with no bill yet.
          </p>
          <div className="row-actions">
            {data.unbilledMonths.map((m) => (
              <button key={m} type="button" className="btn btn-primary" disabled={busy} onClick={() => void act(() => api.post('/system/billing/issue', { month: m }))}>
                Create and send bills for {m}
              </button>
            ))}
          </div>
        </div>
      )}

      <div className="panel">
        <h2>Bills</h2>
        <div className="filters">
          <label>
            Investment group{' '}
            <select value={groupId} onChange={(e) => setFilter('groupId', e.target.value)}>
              <option value="">All groups</option>
              {data.groups.map((g) => <option key={g.groupId} value={g.groupId}>{g.group}</option>)}
            </select>
          </label>
          <label>
            Status{' '}
            <select value={status} onChange={(e) => setFilter('status', e.target.value)}>
              <option value="">Any</option>
              <option value="open">Unpaid</option>
              <option value="overdue">Overdue</option>
              <option value="processing">Payment on its way</option>
              <option value="paid">Paid</option>
              <option value="void">Cancelled</option>
            </select>
          </label>
          <label>
            Month{' '}
            <select value={month} onChange={(e) => setFilter('month', e.target.value)}>
              <option value="">Any</option>
              {(month && !months.includes(month) ? [month, ...months] : months).map((m) => <option key={m} value={m}>{m}</option>)}
            </select>
          </label>
        </div>
        {data.invoices.length === 0 ? (
          <Empty>No bills match. A bill is created for each group that had cards mailed in a month, once the month is over.</Empty>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Bill</th><th>Group</th><th>Month</th><th>Cards</th><th>Cost</th><th>Your fee</th><th>Billed</th><th>Due</th><th>Status</th><th>Sent</th><th />
                </tr>
              </thead>
              <tbody>
                {data.invoices.map((i) => (
                  <InvoiceRows key={i.id} invoice={i} open={open === i.id} onToggle={() => setOpen(open === i.id ? null : i.id)} busy={busy}
                    onPaid={() => setPaying(i)} onVoid={() => setVoiding(i)}
                    onReopen={() => void act(() => api.post(`/system/billing/invoices/${i.id}/unpaid`))}
                    onSend={() => void act(() => api.post(`/system/billing/invoices/${i.id}/send`))} />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="panel">
        <h2>Investment groups</h2>
        <p className="muted">
          Having cards printed and mailed is off for a group until you switch it on here. Until then the group sees the choice on Print cards greyed out, with
          a note that it has to be switched on first. Each group pays the cost of a card ({money(data.settings.costCents)}) plus your fee. Give a group its
          own fee or its own monthly limit here; otherwise it uses the ones under Settings below.
        </p>
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th>Group</th><th>Mailing</th><th>Your fee per card</th><th>Price per card</th><th>Monthly limit</th><th>This month</th><th>Unpaid bills</th><th>Pays with</th><th /></tr>
            </thead>
            <tbody>
              {data.groups.map((g) => (
                <tr key={g.groupId}>
                  <td><strong>{g.group}</strong>{!g.active && <> <Badge kind="neutral">inactive</Badge></>}</td>
                  <td>
                    {g.mailEnabled ? <Badge kind="ok">switched on</Badge> : <Badge kind="neutral">not switched on</Badge>}{' '}
                    <button type="button" className="btn btn-sm" disabled={busy} aria-label={`${g.mailEnabled ? 'Switch off' : 'Switch on'} mailing for ${g.group}`}
                      onClick={() => void act(() => api.put(`/system/billing/groups/${g.groupId}`, { feeCents: g.feeCents ?? null, limitCents: g.limitCents ?? null, mailEnabled: !g.mailEnabled }))}>
                      {g.mailEnabled ? 'Switch off' : 'Switch on'}
                    </button>
                  </td>
                  <td>{money(g.effectiveFeeCents)} {g.feeCents == null ? <span className="muted small">(site default)</span> : <Badge kind="accent">own fee</Badge>}</td>
                  <td>{money(data.settings.costCents + g.effectiveFeeCents)}</td>
                  <td>
                    {g.effectiveLimitCents === 0 ? <Badge kind="danger">mailing off</Badge> : money(g.effectiveLimitCents)}{' '}
                    {g.limitCents == null ? <span className="muted small">(site default)</span> : g.effectiveLimitCents > 0 && <Badge kind="accent">own limit</Badge>}
                  </td>
                  <td>{g.cardsThisMonth} card{g.cardsThisMonth === 1 ? '' : 's'}, {money(g.billedThisMonthCents)}</td>
                  <td>{g.owedCents > 0 ? <strong>{money(g.owedCents)}</strong> : '—'}</td>
                  <td>
                    {g.payLabel ?? <span className="muted">no payment method</span>}
                    {g.autoPay && <> <Badge kind="ok">automatic</Badge></>}
                  </td>
                  <td><button type="button" className="btn btn-sm" aria-label={`Change fee and limit for ${g.group}`} onClick={() => setEditGroup(g)}>Change</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <SettingsPanel settings={data.settings} payReady={data.payReady} busy={busy}
        onSave={(s) => act(async () => { await api.put('/system/billing/settings', s); return { message: 'Billing settings saved.' } })} />

      <StatementPanel groups={data.groups} />

      {editGroup && (
        <GroupTermsModal group={editGroup} settings={data.settings} busy={busy} onClose={() => setEditGroup(null)}
          onSave={(fee, limit) => act(async () => {
            await api.put(`/system/billing/groups/${editGroup.groupId}`, { feeCents: fee, limitCents: limit })
            setEditGroup(null)
            return { message: `Saved for ${editGroup.group}.` }
          })} />
      )}
      {paying && (
        <MarkPaidModal invoice={paying} busy={busy} onClose={() => setPaying(null)}
          onSave={(how, note, paidOn) => act(async () => {
            await api.post(`/system/billing/invoices/${paying.id}/paid`, { how, note, paidOn })
            setPaying(null)
            return { message: `Bill ${paying.number} marked as paid.` }
          })} />
      )}
      {voiding && (
        <Confirm title={`Cancel bill ${voiding.number}?`} danger confirmLabel="Cancel the bill" busy={busy}
          message={`${voiding.group} will no longer see this bill for ${money(voiding.totalCents)} or be asked to pay it. You have already paid the mail house for these cards. It can be put back later with Reopen.`}
          onCancel={() => setVoiding(null)}
          onConfirm={() => void act(async () => { await api.post(`/system/billing/invoices/${voiding.id}/void`); setVoiding(null); return { message: `Bill ${voiding.number} cancelled.` } })} />
      )}
    </>
  )
}

function InvoiceRows({ invoice: i, open, onToggle, busy, onPaid, onVoid, onReopen, onSend }: {
  invoice: SystemInvoice; open: boolean; onToggle: () => void; busy: boolean; onPaid: () => void; onVoid: () => void; onReopen: () => void; onSend: () => void
}) {
  const s = INVOICE_STATUS[i.status] ?? INVOICE_STATUS.open
  return (
    <>
      <tr>
        <td><button type="button" className="btn-link" onClick={onToggle} aria-expanded={open} aria-label={`${open ? 'Hide' : 'Show'} details of bill ${i.number}`}>{i.number}</button></td>
        <td>{i.group}</td>
        <td>{i.monthName}</td>
        <td>{i.cards}</td>
        <td>{money(i.costCents)}</td>
        <td>{money(i.feeCents)}</td>
        <td><strong>{money(i.totalCents)}</strong>{i.surchargeCents > 0 && <div className="muted small">+ {money(i.surchargeCents)} {i.paidHow === 'bank' ? 'bank fee' : 'card fee'}</div>}</td>
        <td>{fmtDate(i.dueOn)}</td>
        <td>
          {i.overdue ? <Badge kind="danger">Overdue</Badge> : <Badge kind={s.kind}>{s.label}</Badge>}
          {i.status === 'paid' && <div className="muted small">{fmtDay(i.paidAt)}{i.paidHow ? `, ${HOW[i.paidHow]?.toLowerCase() ?? i.paidHow}` : ''}</div>}
          {i.paymentError && <div className="small" style={{ color: 'var(--danger)' }}>Payment failed</div>}
        </td>
        <td>
          {i.emailedAt ? <span className="small">Emailed {fmtDay(i.emailedAt)}</span> : <span className="muted small">Not emailed</span>}
          {i.printedAt && <div className="small">Posted {fmtDay(i.printedAt)}</div>}
          {i.deliveryError && <div className="small" style={{ color: 'var(--danger)' }}>Not sent</div>}
        </td>
        <td>
          <div className="row-actions">
            {i.status === 'open' && <button type="button" className="btn btn-sm btn-primary" disabled={busy} onClick={onPaid} aria-label={`Mark bill ${i.number} as paid`}>Mark paid</button>}
            {i.status !== 'void' && <button type="button" className="btn btn-sm" disabled={busy} onClick={onSend} aria-label={`Send bill ${i.number} again`}>Send again</button>}
            {(i.status === 'void' || (i.status === 'paid' && !i.paymentRef)) && (
              <button type="button" className="btn btn-sm" disabled={busy} onClick={onReopen} aria-label={`Reopen bill ${i.number}`}>Reopen</button>
            )}
            {i.status === 'open' && <button type="button" className="btn btn-sm btn-danger" disabled={busy} onClick={onVoid} aria-label={`Cancel bill ${i.number}`}>Cancel bill</button>}
          </div>
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={11} style={{ background: 'var(--surface-2, #f7f8fa)' }}>
            <dl className="kv">
              <dt>By restaurant</dt>
              <dd>{i.lines.map((l) => `${l.restaurant}: ${l.cards} card${l.cards === 1 ? '' : 's'}, ${money(l.cents)}`).join(' · ')}</dd>
              <dt>Created</dt>
              <dd>{fmtDateTime(i.issuedAt)}</dd>
              {i.emailedTo && (<><dt>Emailed to</dt><dd>{i.emailedTo}</dd></>)}
              {i.deliveryError && (<><dt>Sending</dt><dd style={{ color: 'var(--danger)' }}>{i.deliveryError}</dd></>)}
              {i.status === 'paid' && (<><dt>Paid</dt><dd>{fmtDateTime(i.paidAt)}{i.paidHow ? ` by ${HOW[i.paidHow]?.toLowerCase() ?? i.paidHow}` : ''}{i.paidBy ? `, recorded by ${i.paidBy}` : ''}{i.paidNote ? ` (${i.paidNote})` : ''}</dd></>)}
              {i.paymentRef && (<><dt>Stripe payment</dt><dd><code>{i.paymentRef}</code></dd></>)}
              {i.paymentError && (<><dt>Payment problem</dt><dd style={{ color: 'var(--danger)' }}>{i.paymentError}</dd></>)}
            </dl>
          </td>
        </tr>
      )}
    </>
  )
}

function MarkPaidModal({ invoice, busy, onClose, onSave }: { invoice: SystemInvoice; busy: boolean; onClose: () => void; onSave: (how: string, note: string, paidOn: string) => void }) {
  const [how, setHow] = useState('check')
  const [note, setNote] = useState('')
  const [paidOn, setPaidOn] = useState(today())
  const submit = (e: FormEvent) => { e.preventDefault(); onSave(how, note, paidOn) }
  return (
    <Modal title={`Mark bill ${invoice.number} as paid`} onClose={onClose}>
      <form onSubmit={submit} className="grid">
        <p style={{ margin: 0 }}>{invoice.group} paid <strong>{money(invoice.totalCents)}</strong> for {invoice.monthName} outside the site.</p>
        <Field label="How it was paid">
          <select value={how} onChange={(e) => setHow(e.target.value)}>
            {Object.entries(HOW).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </Field>
        <Field label="Date received">
          <input type="date" value={paidOn} max={today()} onChange={(e) => setPaidOn(e.target.value)} required />
        </Field>
        <Field label="Note" hint="For your records, such as a check number. The group does not see it.">
          <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} />
        </Field>
        <div className="row-actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : 'Mark as paid'}</button>
        </div>
      </form>
    </Modal>
  )
}

function GroupTermsModal({ group, settings, busy, onClose, onSave }: {
  group: GroupBillingRow; settings: BillingSettings; busy: boolean; onClose: () => void; onSave: (feeCents: number | null, limitCents: number | null) => void
}) {
  const [ownFee, setOwnFee] = useState(group.feeCents != null)
  const [fee, setFee] = useState(dollars(group.feeCents ?? settings.feeCents))
  const [ownLimit, setOwnLimit] = useState(group.limitCents != null)
  const [limit, setLimit] = useState(dollars(group.limitCents ?? settings.monthlyLimitCents))
  const submit = (e: FormEvent) => { e.preventDefault(); onSave(ownFee ? toCents(fee) : null, ownLimit ? toCents(limit) : null) }
  const price = settings.costCents + (ownFee ? toCents(fee) || 0 : settings.feeCents)
  return (
    <Modal title={`Fee and limit for ${group.group}`} onClose={onClose}>
      <form onSubmit={submit} className="grid">
        <label className="check">
          <input type="checkbox" checked={ownFee} onChange={(e) => setOwnFee(e.target.checked)} />
          <span>Give this group its own fee per card <span className="muted">(otherwise {money(settings.feeCents)}, the site’s)</span></span>
        </label>
        {ownFee && (
          <Field label="Your fee per card ($)" hint={`Added to the ${money(settings.costCents)} cost. This group would pay ${money(price)} a card.`}>
            <input type="number" min="0" max="5" step="0.01" value={fee} onChange={(e) => setFee(e.target.value)} required />
          </Field>
        )}
        <label className="check">
          <input type="checkbox" checked={ownLimit} onChange={(e) => setOwnLimit(e.target.checked)} />
          <span>Give this group its own monthly limit <span className="muted">(otherwise {money(settings.monthlyLimitCents)}, the site’s)</span></span>
        </label>
        {ownLimit && (
          <Field label="Most it can be billed in a month ($)" hint="Once reached, the group cannot mail more cards until next month. $0 switches mailing off for this group.">
            <input type="number" min="0" max="100000" step="1" value={limit} onChange={(e) => setLimit(e.target.value)} required />
          </Field>
        )}
        <p className="muted small" style={{ margin: 0 }}>Changes apply to cards mailed from now on. Cards already mailed keep the price they were sent at.</p>
        <div className="row-actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        </div>
      </form>
    </Modal>
  )
}

function SettingsPanel({ settings, payReady, busy, onSave }: { settings: BillingSettings; payReady: boolean; busy: boolean; onSave: (s: BillingSettings) => void }) {
  const [s, setS] = useState(settings)
  const [cost, setCost] = useState(dollars(settings.costCents))
  const [fee, setFee] = useState(dollars(settings.feeCents))
  const [limit, setLimit] = useState(dollars(settings.monthlyLimitCents))
  useEffect(() => {
    setS(settings)
    setCost(dollars(settings.costCents))
    setFee(dollars(settings.feeCents))
    setLimit(dollars(settings.monthlyLimitCents))
  }, [settings])
  const set = <K extends keyof BillingSettings>(k: K, v: BillingSettings[K]) => setS((x) => ({ ...x, [k]: v }))
  const submit = (e: FormEvent) => {
    e.preventDefault()
    onSave({ ...s, costCents: toCents(cost), feeCents: toCents(fee), monthlyLimitCents: toCents(limit), siteUrl: s.siteUrl || window.location.origin })
  }
  const price = (toCents(cost) || 0) + (toCents(fee) || 0)
  return (
    <div className="panel">
      <h2>Settings</h2>
      <form onSubmit={submit} className="grid">
        <h3 style={{ margin: 0 }}>Prices</h3>
        <div className="grid grid-3">
          <Field label="What a card costs you ($)" hint="What the mail house charges you for one printed, mailed card. PostGrid’s free plan is about $0.90.">
            <input type="number" min="0.01" max="10" step="0.01" value={cost} onChange={(e) => setCost(e.target.value)} required />
          </Field>
          <Field label="Your fee per card ($)" hint={`Added on top, unless a group has its own. Groups pay ${money(price)} a card.`}>
            <input type="number" min="0" max="5" step="0.01" value={fee} onChange={(e) => setFee(e.target.value)} required />
          </Field>
          <Field label="Monthly limit per group ($)" hint="The most one group can be billed in a month, unless it has its own. It caps what you are owed. $0 switches mailing off.">
            <input type="number" min="0" max="100000" step="1" value={limit} onChange={(e) => setLimit(e.target.value)} required />
          </Field>
        </div>

        <h3 style={{ margin: 0 }}>Monthly bills</h3>
        <div className="grid grid-3">
          <Field label="Bill day" hint="The day of the month last month’s bills are created and sent (1 to 28).">
            <input type="number" min="1" max="28" value={s.billDay} onChange={(e) => set('billDay', Number(e.target.value))} required />
          </Field>
          <Field label="Days to pay" hint="A bill is overdue this many days after its date.">
            <input type="number" min="0" max="90" value={s.dueDays} onChange={(e) => set('dueDays', Number(e.target.value))} required />
          </Field>
          <Field label="Credit card fee (%)" hint="Added when a group pays by credit card. Never added for a debit card.">
            <input type="number" min="0" max="10" step="0.01" value={s.cardFeePercent} onChange={(e) => set('cardFeePercent', Number(e.target.value))} required />
          </Field>
          <Field label="Bank account fee (%)" hint={`Added when a group pays from a connected bank account. On a $100.00 bill this adds ${money(Math.round(100 * s.bankFeePercent))}.`}>
            <input type="number" min="0" max="10" step="0.01" value={s.bankFeePercent} onChange={(e) => set('bankFeePercent', Number(e.target.value))} required />
          </Field>
        </div>
        <label className="check">
          <input type="checkbox" checked={s.emailBills} onChange={(e) => set('emailBills', e.target.checked)} />
          <span>Email each bill to the group <span className="muted">(to its billing email, or its administrators when it has none)</span></span>
        </label>
        <label className="check">
          <input type="checkbox" checked={s.printBills} onChange={(e) => set('printBills', e.target.checked)} />
          <span>Also post a printed statement <span className="muted">(through the mail house, about $1 a letter charged to you; needs your address below and the group’s billing address)</span></span>
        </label>
        <label className="check">
          <input type="checkbox" checked={s.requirePaymentMethod} onChange={(e) => set('requirePaymentMethod', e.target.checked)} />
          <span>
            A group must save a payment method before any of its cards is sent to be mailed{' '}
            <span className="muted">
              {payReady ? '(until then it prints its own cards)' : '(no group can mail until the Stripe key is entered on Connected Systems; untick to allow mailing without one)'}
            </span>
          </span>
        </label>

        <h3 style={{ margin: 0 }}>Who the bills are from</h3>
        <div className="grid grid-2">
          <Field label="Business name" hint="Shown at the top of every bill and as the sender of the email.">
            <input value={s.businessName} onChange={(e) => set('businessName', e.target.value)} maxLength={120} required />
          </Field>
          <Field label="Email for questions about a bill">
            <input type="email" value={s.businessEmail ?? ''} onChange={(e) => set('businessEmail', e.target.value)} maxLength={200} />
          </Field>
        </div>
        <div className="grid grid-4">
          <Field label="Street"><input value={s.street ?? ''} onChange={(e) => set('street', e.target.value)} maxLength={200} /></Field>
          <Field label="City"><input value={s.city ?? ''} onChange={(e) => set('city', e.target.value)} maxLength={100} /></Field>
          <Field label="State"><input value={s.state ?? ''} onChange={(e) => set('state', e.target.value)} maxLength={20} /></Field>
          <Field label="Zip"><input value={s.zip ?? ''} onChange={(e) => set('zip', e.target.value)} maxLength={20} /></Field>
        </div>
        <Field label="How to pay, printed on every bill" hint="For example where to send a check. Leave empty if groups only pay on the site.">
          <textarea rows={2} value={s.payInstructions ?? ''} onChange={(e) => set('payInstructions', e.target.value)} maxLength={600} />
        </Field>
        <Field label="This site’s address" hint="Used for the link in emailed bills.">
          <input type="url" value={s.siteUrl ?? window.location.origin} onChange={(e) => set('siteUrl', e.target.value)} />
        </Field>
        <div className="row-actions">
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : 'Save settings'}</button>
        </div>
      </form>
    </div>
  )
}

/** One month's mailed cards by group and restaurant, split into the mail house's cost and the site's fee. */
function StatementPanel({ groups }: { groups: GroupBillingRow[] }) {
  const [data, setData] = useState<SiteMailStatement | null>(null)
  const [month, setMonth] = useState('')
  const [groupId, setGroupId] = useState('')
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    api.get<SiteMailStatement>(`/system/billing/statement?month=${encodeURIComponent(month)}&groupId=${encodeURIComponent(groupId)}`)
      .then(setData).catch((e) => setError((e as Error).message))
  }, [month, groupId])
  return (
    <div className="panel">
      <h2>What was mailed, month by month</h2>
      <p className="muted">Every card that was really mailed, including the month still running. Your fee is what you keep after paying the mail house.</p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!data ? !error && <Spinner /> : (
        <>
          <div className="filters">
            <label>
              Month{' '}
              <select value={data.month} onChange={(e) => setMonth(e.target.value)}>
                {data.months.map((m) => <option key={m} value={m}>{m}</option>)}
              </select>
            </label>
            <label>
              Investment group{' '}
              <select value={groupId} onChange={(e) => setGroupId(e.target.value)}>
                <option value="">All groups</option>
                {groups.map((g) => <option key={g.groupId} value={g.groupId}>{g.group}</option>)}
              </select>
            </label>
          </div>
          {data.rows.length === 0 ? <Empty>No cards were mailed in {data.month}.</Empty> : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Group</th><th>Restaurant</th><th>Cards</th><th>Mail house cost</th><th>Your fee</th><th>Billed to the group</th></tr></thead>
                <tbody>
                  {data.rows.map((r) => (
                    <tr key={r.restaurantId}><td>{r.group}</td><td>{r.restaurant}</td><td>{r.cards}</td><td>{money(r.costCents)}</td><td>{money(r.feeCents)}</td><td>{money(r.totalCents)}</td></tr>
                  ))}
                  <tr><td colSpan={2}><strong>Total</strong></td><td><strong>{data.cards}</strong></td><td><strong>{money(data.costCents)}</strong></td><td><strong>{money(data.feeCents)}</strong></td><td><strong>{money(data.totalCents)}</strong></td></tr>
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </div>
  )
}
