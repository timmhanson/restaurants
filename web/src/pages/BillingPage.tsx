import { useEffect, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { BillingActionResult, BillingContact, GroupBilling, Invoice } from '../api/types'
import Stat from '../components/Stat'
import { Alert, Badge, Confirm, Empty, Field, Spinner, fmtDate, fmtDateTime, fmtDay } from '../components/ui'
import { INVOICE_STATUS, money } from './SystemBillingPage'

/**
 * Billing, for an investment group: what having cards printed and mailed costs, this month's use against the
 * group's limit, each month by restaurant, the monthly bills, and the payment method they are paid with. Owners see
 * the use of their own restaurants; the group's administrators also see the bills, save a payment method and pay.
 */
export default function BillingPage() {
  const [params, setParams] = useSearchParams()
  const [data, setData] = useState<GroupBilling | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ ok: boolean; message: string } | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [paying, setPaying] = useState<Invoice | null>(null)
  const [removing, setRemoving] = useState(false)
  const [autoPayAsk, setAutoPayAsk] = useState(false)
  const [open, setOpen] = useState<number | null>(null)
  const finished = useRef(false)

  useEffect(() => {
    // Back from the payment service's page: read what was saved there, once, and tidy the address.
    const session = params.get('session_id')
    const saved = params.get('saved')
    if (saved && !finished.current) {
      finished.current = true
      setParams({}, { replace: true })
      if (saved === '1' && session) {
        setBusy('finish')
        api.post<BillingActionResult>('/billing/payment-method/finish', { sessionId: session })
          .then((r) => { setData(r.billing); setNotice({ ok: r.ok, message: r.message }) })
          .catch((e) => setError((e as Error).message))
          .finally(() => setBusy(null))
        return
      }
      setNotice({ ok: false, message: 'No payment method was saved. Nothing has changed.' })
    }
    api.get<GroupBilling>('/billing').then(setData).catch((e) => setError((e as Error).message))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const run = async (what: string, work: () => Promise<void>) => {
    setBusy(what)
    setError(null)
    setNotice(null)
    try {
      await work()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const addMethod = () => run('start', async () => {
    const { url } = await api.post<{ url: string }>('/billing/payment-method/start')
    // The bank account or card is typed on the payment service's own page, never on this site.
    window.location.assign(url)
  })

  if (!data) return error ? <Alert kind="error">{error}</Alert> : <Spinner label={busy === 'finish' ? 'Saving your payment method…' : 'Loading…'} />
  const d = data
  const left = Math.max(0, d.limitCents - d.usedCents)
  const pct = (n: number) => `${n}%`
  // What the saved payment method adds to each bill: the card fee for a credit card, the bank fee for a bank account.
  const feePercent = d.method?.kind === 'bank' ? d.bankFeePercent : d.method?.kind === 'card' && d.method.funding === 'credit' ? d.cardFeePercent : 0
  const feeName = d.method?.kind === 'bank' ? 'bank account fee' : 'credit card fee'
  const amount = (i: Invoice) => i.dueCents + i.payFeeCents

  return (
    <>
      <div className="page-head">
        <h1>Billing</h1>
      </div>
      <p className="muted">
        Cards chosen as <strong>Mailed for you</strong> on <Link to="/print">Print cards</Link> are printed and mailed by {d.businessName}, and billed to your
        investment group once a month. Cards you print yourself cost nothing here.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind={notice.ok ? 'success' : 'warn'} onClose={() => setNotice(null)}>{notice.message}</Alert>}
      {!d.mailAvailable && <Alert kind="info">Having cards mailed for you is not switched on for this investment group at the moment. You can still print your own.</Alert>}
      {d.mailAvailable && d.test && <Alert kind="info">Mailing is in test mode: nothing is printed, mailed or billed yet.</Alert>}
      {d.mailAvailable && d.payRequired && !d.method && d.canManage && d.creditCents > 0 && (
        <Alert kind="info">
          You have {money(d.creditCents)} of credit, so cards can be mailed for you now and are paid from it. Save a payment method below to keep mailing once the
          credit is used.
        </Alert>
      )}
      {d.mailAvailable && d.payRequired && !d.method && d.canManage && d.creditCents <= 0 && (
        <Alert kind="warn">
          {d.payAvailable
            ? 'Save a payment method below before cards can be mailed for you. Until then, print the cards from Print cards.'
            : 'A payment method is needed before cards can be mailed for you, and paying on the site is not set up yet. Until it is, print the cards from Print cards.'}
        </Alert>
      )}

      <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
        <Stat value={money(d.centsPerCard)} label="for each card printed and mailed (postage included)" />
        <Stat value={`${d.cardsThisMonth}`} label={`card${d.cardsThisMonth === 1 ? '' : 's'} mailed this month${d.canManage ? '' : ' at your restaurants'}`} />
        <Stat value={money(d.usedCents)} label={d.canManage ? `of this month’s ${money(d.limitCents)} limit (${money(left)} left)` : 'so far this month at your restaurants'} warn={d.canManage && d.limitCents > 0 && left < d.centsPerCard} />
        {d.canManage && <Stat value={money(d.owedCents)} label="in unpaid bills" warn={d.invoices.some((i) => i.overdue)} />}
        {d.canManage && d.creditCents > 0 && <Stat value={money(d.creditCents)} label="credit, which comes off your next bills by itself" />}
      </div>

      {d.canManage && (
        <div className="panel">
          <h2>Bills</h2>
          <p className="muted">
            A bill for each month’s mailed cards is created on day {d.billDay} of the next month and is due {d.dueDays} day{d.dueDays === 1 ? '' : 's'} later.
            {d.payInstructions ? ` ${d.payInstructions}` : ''}
          </p>
          {d.invoices.length === 0 ? (
            <Empty>No bills yet. The first one comes after the first month in which cards are mailed for you.</Empty>
          ) : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Bill</th><th>For</th><th>Cards</th><th>Amount</th><th>Due</th><th>Status</th><th /></tr></thead>
                <tbody>
                  {d.invoices.map((i) => {
                    const s = INVOICE_STATUS[i.status] ?? INVOICE_STATUS.open
                    return (
                      <FragmentRows key={i.id}>
                        <tr>
                          <td><button type="button" className="btn-link" onClick={() => setOpen(open === i.id ? null : i.id)} aria-expanded={open === i.id} aria-label={`${open === i.id ? 'Hide' : 'Show'} details of bill ${i.number}`}>{i.number}</button></td>
                          <td>{i.monthName}</td>
                          <td>{i.cards}</td>
                          <td><strong>{money(i.totalCents)}</strong>{i.creditCents > 0 && <div className="muted small">− {money(i.creditCents)} credit{i.dueCents > 0 ? `, ${money(i.dueCents)} to pay` : ''}</div>}{i.surchargeCents > 0 && <div className="muted small">+ {money(i.surchargeCents)} {i.paidHow === 'bank' ? 'bank account fee' : 'credit card fee'}</div>}</td>
                          <td>{fmtDate(i.dueOn)}</td>
                          <td>
                            {i.overdue ? <Badge kind="danger">Overdue</Badge> : <Badge kind={s.kind}>{s.label}</Badge>}
                            {i.status === 'paid' && <div className="muted small">{fmtDay(i.paidAt)}</div>}
                            {i.paymentError && <div className="small" style={{ color: 'var(--danger)' }}>{i.paymentError}</div>}
                          </td>
                          <td>
                            {i.status === 'open' && d.payAvailable && d.method && (
                              <button type="button" className="btn btn-sm btn-primary" disabled={busy !== null} onClick={() => setPaying(i)} aria-label={`Pay bill ${i.number}`}>Pay {money(amount(i))}</button>
                            )}
                          </td>
                        </tr>
                        {open === i.id && (
                          <tr>
                            <td colSpan={7}>
                              {i.lines.map((l) => `${l.restaurant}: ${l.cards} card${l.cards === 1 ? '' : 's'}, ${money(l.cents)}`).join(' · ')}
                              <span className="muted small"> · created {fmtDateTime(i.issuedAt)}</span>
                            </td>
                          </tr>
                        )}
                      </FragmentRows>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {d.canManage && (
        <div className="panel">
          <h2>Payment method</h2>
          {!d.payAvailable ? (
            <p className="muted" style={{ margin: 0 }}>
              Paying on the site is not set up yet. {d.payInstructions ?? `Pay each bill as ${d.businessName} asks.`}
            </p>
          ) : (
            <>
              <p className="muted">
                Pay bills from a bank account or by card. A <strong>bank account adds {d.bankFeePercent > 0 ? pct(d.bankFeePercent) : 'nothing'}</strong> to each
                bill, a <strong>credit card adds {d.cardFeePercent > 0 ? pct(d.cardFeePercent) : 'nothing'}</strong>, and a debit card adds nothing. The account or card number is typed on our payment provider’s secure page and never reaches this site.
                {d.payTest && ' Payments are in test mode: no money moves.'}
              </p>
              {d.method ? (
                <>
                  <dl className="kv">
                    <dt>Saved</dt>
                    <dd>
                      <Badge kind="ok">{d.method.kind === 'bank' ? 'bank account' : 'card'}</Badge> {d.method.label}
                      {feePercent > 0 && <> <Badge kind="warn">{pct(feePercent)} fee on each bill</Badge></>}
                    </dd>
                    <dt>Added</dt>
                    <dd>{d.method.setAt ? fmtDateTime(d.method.setAt) : ''}{d.method.setBy ? ` by ${d.method.setBy}` : ''}</dd>
                    <dt>Automatic payment</dt>
                    <dd>
                      <label className="check">
                        <input type="checkbox" checked={d.autoPay} disabled={busy !== null}
                          onChange={(e) => (e.target.checked ? setAutoPayAsk(true) : void run('auto', async () => setData(await api.put<GroupBilling>('/billing/auto-pay', { on: false }))))} />
                        <span>Pay each new bill with this by itself when it is created</span>
                      </label>
                    </dd>
                  </dl>
                  <div className="row-actions" style={{ marginTop: '0.75rem' }}>
                    <button type="button" className="btn" onClick={() => void addMethod()} disabled={busy !== null}>{busy === 'start' ? 'Opening…' : 'Replace'}</button>
                    <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove</button>
                  </div>
                </>
              ) : (
                <>
                  <p><Badge kind="neutral">none saved</Badge> {d.payRequired ? 'One is needed before cards can be mailed for you.' : 'Without one, pay each bill as it asks.'}</p>
                  <button type="button" className="btn btn-primary" onClick={() => void addMethod()} disabled={busy !== null}>{busy === 'start' ? 'Opening…' : 'Add a bank account or card'}</button>
                </>
              )}
            </>
          )}
        </div>
      )}

      <div className="panel">
        <h2>Cards mailed, month by month</h2>
        {d.months.length === 0 ? (
          <Empty>No cards have been mailed for {d.canManage ? 'your investment group' : 'your restaurants'} in the last 12 months.</Empty>
        ) : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Month</th><th>Restaurant</th><th>Cards</th><th>Amount</th></tr></thead>
              <tbody>
                {d.months.map((m) => (
                  <FragmentRows key={m.month}>
                    {m.restaurants.map((r, n) => (
                      <tr key={r.restaurantId}>
                        <td>{n === 0 ? <strong>{m.monthName}</strong> : ''}</td>
                        <td>{r.restaurant}</td>
                        <td>{r.cards}</td>
                        <td>{money(r.totalCents)}</td>
                      </tr>
                    ))}
                    {m.restaurants.length > 1 && (
                      <tr><td /><td><strong>Total</strong></td><td><strong>{m.cards}</strong></td><td><strong>{money(m.totalCents)}</strong></td></tr>
                    )}
                  </FragmentRows>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {d.canManage && <ContactPanel contact={d.contact} busy={busy !== null}
        onSave={(c) => run('contact', async () => { setData(await api.put<GroupBilling>('/billing/contact', c)); setNotice({ ok: true, message: 'Saved where your bills go.' }) })} />}
      {d.canManage && d.credits.length > 0 && (
        <div className="panel">
          <h2>Credit</h2>
          <p className="muted">
            Credit {d.businessName} has given your investment group, and where it was used. Credit comes off your bills by itself, oldest bill first.
            {d.creditCents > 0 ? ` ${money(d.creditCents)} is unused.` : ' None is unused at the moment.'}
          </p>
          <div className="table-wrap">
            <table>
              <thead><tr><th>Date</th><th>What</th><th>Bill</th><th>Amount</th></tr></thead>
              <tbody>
                {d.credits.map((c, n) => (
                  <tr key={n}>
                    <td>{fmtDay(c.at)}</td>
                    <td>{c.label}</td>
                    <td>{c.invoiceNumber ?? '—'}</td>
                    <td>{c.amountCents >= 0 ? <strong>+ {money(c.amountCents)}</strong> : `− ${money(-c.amountCents)}`}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      {d.businessEmail && <p className="muted small">Questions about a bill: {d.businessEmail}</p>}

      {paying && (
        <Confirm
          title={`Pay bill ${paying.number}?`}
          message={
            `${money(amount(paying))} will be taken from ${d.method?.label}`
            + (paying.payFeeCents > 0 ? `: the ${money(paying.dueCents)} owed on the bill plus the ${pct(feePercent)} ${feeName} of ${money(paying.payFeeCents)}.` : '.')
            + (d.method?.kind === 'bank' ? ' A bank payment takes a few working days to arrive; the bill shows as paid when it does.' : '')
            + (d.payTest ? ' Payments are in test mode, so no money moves.' : '')
          }
          confirmLabel={`Pay ${money(amount(paying))}`}
          busy={busy === 'pay'}
          onCancel={() => setPaying(null)}
          onConfirm={() => void run('pay', async () => {
            const r = await api.post<BillingActionResult>(`/billing/invoices/${paying.id}/pay`, { confirmCents: amount(paying) })
            setData(r.billing)
            setNotice({ ok: r.ok, message: r.message })
            setPaying(null)
          })}
        />
      )}
      {autoPayAsk && d.method && (
        <Confirm
          title="Pay each bill automatically?"
          message={
            `Each new monthly bill will be taken from ${d.method.label} when it is created, without anyone pressing Pay`
            + (feePercent > 0 ? `, with the ${pct(feePercent)} ${feeName} added` : '')
            + `. A month’s bill can never be more than your ${money(d.limitCents)} limit${feePercent > 0 ? ' plus that fee' : ''}. You can switch this off at any time.`
          }
          confirmLabel="Switch on"
          busy={busy === 'auto'}
          onCancel={() => setAutoPayAsk(false)}
          onConfirm={() => void run('auto', async () => { setData(await api.put<GroupBilling>('/billing/auto-pay', { on: true })); setAutoPayAsk(false) })}
        />
      )}
      {removing && (
        <Confirm
          title="Remove the payment method?"
          message={`${d.method?.label} will no longer be used, and automatic payment stops.${d.payRequired ? ' Cards cannot be mailed for you until another is saved; you can still print your own.' : ''}`}
          confirmLabel="Remove"
          danger
          busy={busy === 'remove'}
          onCancel={() => setRemoving(false)}
          onConfirm={() => void run('remove', async () => { setData(await api.delete<GroupBilling>('/billing/payment-method')); setRemoving(false) })}
        />
      )}
    </>
  )
}

/** Groups table rows without adding an element between tbody and tr. */
function FragmentRows({ children }: { children: React.ReactNode }) {
  return <>{children}</>
}

function ContactPanel({ contact, busy, onSave }: { contact: BillingContact; busy: boolean; onSave: (c: BillingContact) => void }) {
  const [c, setC] = useState(contact)
  useEffect(() => setC(contact), [contact])
  const set = (k: keyof BillingContact, v: string) => setC((x) => ({ ...x, [k]: v }))
  const submit = (e: FormEvent) => { e.preventDefault(); onSave(c) }
  return (
    <div className="panel">
      <h2>Where bills go</h2>
      <form onSubmit={submit} className="grid">
        <div className="grid grid-2">
          <Field label="Name on the bill" hint="Leave empty to use the investment group’s name.">
            <input value={c.name ?? ''} onChange={(e) => set('name', e.target.value)} maxLength={200} />
          </Field>
          <Field label="Email for bills" hint="Leave empty to send them to the group’s administrators.">
            <input type="email" value={c.email ?? ''} onChange={(e) => set('email', e.target.value)} maxLength={200} />
          </Field>
        </div>
        <div className="grid grid-4">
          <Field label="Mailing address (street)"><input value={c.street ?? ''} onChange={(e) => set('street', e.target.value)} maxLength={200} /></Field>
          <Field label="City"><input value={c.city ?? ''} onChange={(e) => set('city', e.target.value)} maxLength={100} /></Field>
          <Field label="State"><input value={c.state ?? ''} onChange={(e) => set('state', e.target.value)} maxLength={20} /></Field>
          <Field label="Zip"><input value={c.zip ?? ''} onChange={(e) => set('zip', e.target.value)} maxLength={20} /></Field>
        </div>
        <p className="muted small" style={{ margin: 0 }}>The mailing address is only used if printed statements are posted.</p>
        <div className="row-actions">
          <button type="submit" className="btn btn-primary" disabled={busy}>Save</button>
        </div>
      </form>
    </div>
  )
}
