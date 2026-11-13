import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { api, qs } from '../api/client'
import type { CommentSummary, PagedResult, PrintConfig } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { AddressBadge } from '../components/AddressCheckNote'
import { hasMailingAddress } from '../components/PostcardFace'
import { Alert, Badge, Empty, Field, Spinner, daysAgo, fmtDate, today } from '../components/ui'

type Which = 'not-sent' | 'date' | 'range'
type DateKind = 'entered' | 'visit'
const LIMIT = 200

/**
 * The Print cards tab: choose which coupon cards to print (everything not sent yet by default, or the cards for
 * one day or a range of days), for every restaurant or just one, set the print options, then preview and print.
 */
export default function PrintCenterPage() {
  const { restaurants, settings, isGroupWide } = useAuth()
  const nav = useNavigate()
  const [which, setWhich] = useState<Which>('not-sent')
  const [dateKind, setDateKind] = useState<DateKind>('entered')
  const [day, setDay] = useState(today())
  const [from, setFrom] = useState(daysAgo(6))
  const [to, setTo] = useState(today())
  const [restaurantId, setRestaurantId] = useState<number | ''>('')
  const [notSentOnly, setNotSentOnly] = useState(false)
  const [mailableOnly, setMailableOnly] = useState(true)
  const [skipPrinted, setSkipPrinted] = useState(false)
  const [postage, setPostage] = useState<'stamp' | 'bought'>('stamp')
  const [markSent, setMarkSent] = useState(true)
  const [config, setConfig] = useState<PrintConfig | null>(null)
  const [data, setData] = useState<PagedResult<CommentSummary> | null>(null)
  const [left, setLeft] = useState<number[]>([]) // cards unticked by hand
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<PrintConfig>('/comments/print-config').then(setConfig).catch(() => setConfig(null))
  }, [])

  useEffect(() => {
    const start = which === 'date' ? day : which === 'range' ? from : ''
    const end = which === 'date' ? day : which === 'range' ? to : ''
    const dates = which === 'not-sent' ? {} : dateKind === 'entered' ? { enteredFrom: start, enteredTo: end } : { from: start, to: end }
    setLoading(true)
    setError(null)
    api
      .get<PagedResult<CommentSummary>>(
        `/comments${qs({ restaurantId, status: which === 'not-sent' || notSentOnly ? 'waiting' : '', ...dates, sort: 'visitDate', dir: 'asc', page: 1, pageSize: LIMIT })}`,
      )
      .then((r) => {
        setData(r)
        setLeft([])
      })
      .catch((e) => setError((e as Error).message))
      .finally(() => setLoading(false))
  }, [which, dateKind, day, from, to, restaurantId, notSentOnly])

  // Printed and sent are different things: a card can have been printed without being marked as sent (the
  // "mark as sent" box was unticked, or a sent card was reopened, e.g. it came back undeliverable). Under
  // "Not sent yet" the option to leave those out is only offered when there are some.
  const printedBefore = (data?.items ?? []).filter((c) => c.printCount > 0 && (!mailableOnly || hasMailingAddress(c))).length
  const offerSkipPrinted = which !== 'not-sent' || printedBefore > 0
  const shown = useMemo(
    () => (data?.items ?? []).filter((c) => (!mailableOnly || hasMailingAddress(c)) && (!(skipPrinted && offerSkipPrinted) || c.printCount === 0)),
    [data, mailableOnly, skipPrinted, offerSkipPrinted],
  )
  const chosen = shown.filter((c) => !left.includes(c.id))
  const hiddenNoAddress = (data?.items ?? []).filter((c) => !hasMailingAddress(c)).length
  const card = settings?.postcard

  const go = () => nav(`/print-cards${qs({ ids: chosen.map((c) => c.id).join(','), postage, markSent: markSent ? '' : '0' })}`)

  return (
    <>
      <div className="page-head">
        <h1>Print cards</h1>
      </div>
      <p className="muted">Choose which coupon cards to print onto postcards, then preview and print them. Printing marks them as sent.</p>

      <div className="panel">
        <h2>Which cards</h2>
        <div className="source-choice" role="radiogroup" aria-label="Which cards to print">
          <label className={which === 'not-sent' ? 'selected' : ''}>
            <input type="radio" name="which" checked={which === 'not-sent'} onChange={() => setWhich('not-sent')} />
            <span className="source-label">Not sent yet</span>
            <span className="source-hint">Every card still waiting to go out to its guest.</span>
          </label>
          <label className={which === 'date' ? 'selected' : ''}>
            <input type="radio" name="which" checked={which === 'date'} onChange={() => setWhich('date')} />
            <span className="source-label">A specific date</span>
            <span className="source-hint">The cards for one day.</span>
          </label>
          <label className={which === 'range' ? 'selected' : ''}>
            <input type="radio" name="which" checked={which === 'range'} onChange={() => setWhich('range')} />
            <span className="source-label">A date range</span>
            <span className="source-hint">The cards between two days.</span>
          </label>
        </div>
        <div className="filters" style={{ marginTop: '0.75rem', marginBottom: 0 }}>
          {which !== 'not-sent' && (
            <Field label="Which date">
              <select value={dateKind} onChange={(e) => setDateKind(e.target.value as DateKind)}>
                <option value="entered">Date the card was entered</option>
                <option value="visit">Date of the guest's visit</option>
              </select>
            </Field>
          )}
          {which === 'date' && (
            <Field label="Date">
              <input type="date" value={day} max={today()} onChange={(e) => setDay(e.target.value)} />
            </Field>
          )}
          {which === 'range' && (
            <>
              <Field label="From">
                <input type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} />
              </Field>
              <Field label="To">
                <input type="date" value={to} min={from} max={today()} onChange={(e) => setTo(e.target.value)} />
              </Field>
            </>
          )}
          <Field label="Restaurant">
            <select value={restaurantId} onChange={(e) => setRestaurantId(e.target.value ? Number(e.target.value) : '')}>
              <option value="">{restaurants.length > 1 ? `All ${restaurants.length} restaurants` : 'All my restaurants'}</option>
              {restaurants.map((r) => (
                <option key={r.id} value={r.id}>{r.name}</option>
              ))}
            </select>
          </Field>
        </div>
        <div className="check-list" style={{ marginTop: '0.75rem' }}>
          {which !== 'not-sent' && (
            <label className="check">
              <input type="checkbox" checked={notSentOnly} onChange={(e) => setNotSentOnly(e.target.checked)} /> Leave out cards already marked as sent
            </label>
          )}
          <label className="check">
            <input type="checkbox" checked={mailableOnly} onChange={(e) => setMailableOnly(e.target.checked)} /> Only cards with a full mailing address (street, city, state and zip)
          </label>
          {offerSkipPrinted && (
            <label className="check">
              <input type="checkbox" checked={skipPrinted} onChange={(e) => setSkipPrinted(e.target.checked)} />
              <span>
                {which === 'not-sent'
                  ? `Leave out the ${printedBefore} card${printedBefore === 1 ? '' : 's'} already printed but not marked as sent`
                  : 'Leave out cards that have been printed before'}{' '}
                {which === 'not-sent' && <span className="muted">(printed without marking as sent, or reopened after sending)</span>}
              </span>
            </label>
          )}
        </div>
      </div>

      <div className="panel">
        <h2>Print options</h2>
        <fieldset className="role-choice">
          <legend>Postage</legend>
          <div className="source-choice two" role="radiogroup">
            <label className={postage === 'stamp' ? 'selected' : ''}>
              <input type="radio" name="postage" checked={postage === 'stamp'} onChange={() => setPostage('stamp')} />
              <span className="source-label">Stamps by hand</span>
              <span className="source-hint">Cards print with the guest's address and a box for the stamp. Nothing is charged.</span>
            </label>
            <label className={postage === 'bought' ? 'selected' : ''} style={config?.canBuyPostage ? undefined : { opacity: 0.6 }}>
              <input type="radio" name="postage" checked={postage === 'bought'} disabled={!config?.canBuyPostage} onChange={() => setPostage('bought')} />
              <span className="source-label">Buy and print postage</span>
              <span className="source-hint">
                {config?.canBuyPostage
                  ? `USPS postage is bought for each card through ${config.postageProvider}. You confirm the purchase on the next screen.`
                  : !config?.postageAvailable
                    ? 'Not set up on this site yet. Ask your System Administrator.'
                    : 'You need the "buy postage" permission. Ask an owner or administrator.'}
              </span>
            </label>
          </div>
        </fieldset>
        <div className="check-list" style={{ marginTop: '0.75rem' }}>
          <label className="check">
            <input type="checkbox" checked={markSent} onChange={(e) => setMarkSent(e.target.checked)} /> Mark the cards as sent once they have printed
          </label>
        </div>
        {card && (
          <p className="legend-note" style={{ marginTop: '0.75rem' }}>
            Printing on {card.stock === 'Preprinted' ? 'pre-printed cards (only the blanks are printed)' : 'blank postcards (the whole card is printed)'}, {card.widthIn} ×{' '}
            {card.heightIn} inches. {isGroupWide ? <Link to="/settings">Change the card stock, size or layout</Link> : 'Your investment group administrator can change the card stock, size or layout.'}
          </p>
        )}
      </div>

      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="panel">
        <div className="page-head">
          <h2 style={{ margin: 0 }}>
            {loading && !data ? 'Finding cards…' : `${chosen.length} card${chosen.length === 1 ? '' : 's'} to print`}
          </h2>
          <span className="grow" />
          <button type="button" className="btn btn-primary" disabled={chosen.length === 0} onClick={go}>
            Preview and print {chosen.length > 0 ? chosen.length : ''} card{chosen.length === 1 ? '' : 's'}
          </button>
        </div>
        {data && data.total > LIMIT && (
          <Alert kind="warn">
            {data.total} cards match, and {LIMIT} can be printed at a time. The first {LIMIT} (earliest visits) are listed; print them, then come back for the rest.
          </Alert>
        )}
        {mailableOnly && hiddenNoAddress > 0 && (
          <p className="legend-note">
            {hiddenNoAddress} matching card{hiddenNoAddress === 1 ? ' has' : 's have'} no full mailing address and {hiddenNoAddress === 1 ? 'is' : 'are'} left out. Untick the option above
            to include {hiddenNoAddress === 1 ? 'it' : 'them'}.
          </p>
        )}
        {!data ? (
          <Spinner />
        ) : shown.length === 0 ? (
          <Empty>{which === 'not-sent' ? 'Every card has been sent. Nothing is waiting to print.' : 'No cards match these choices.'}</Empty>
        ) : (
          <div className="table-wrap" style={{ opacity: loading ? 0.6 : 1 }}>
            <table>
              <thead>
                <tr>
                  <th>
                    <input
                      type="checkbox"
                      aria-label="Print every card listed"
                      checked={left.length === 0}
                      onChange={(e) => setLeft(e.target.checked ? [] : shown.map((c) => c.id))}
                    />
                  </th>
                  <th>Guest</th>
                  {restaurants.length > 1 && <th>Restaurant</th>}
                  <th>Visit</th>
                  <th>Entered</th>
                  <th>Offer</th>
                  <th>Mailing address</th>
                  <th>Status</th>
                </tr>
              </thead>
              <tbody>
                {shown.map((c) => (
                  <tr key={c.id}>
                    <td>
                      <input
                        type="checkbox"
                        aria-label={`Print ${c.guestFirstName} ${c.guestLastName}'s card`}
                        checked={!left.includes(c.id)}
                        onChange={(e) => setLeft((l) => (e.target.checked ? l.filter((id) => id !== c.id) : [...l, c.id]))}
                      />
                    </td>
                    <td><strong>{c.guestFirstName} {c.guestLastName}</strong></td>
                    {restaurants.length > 1 && <td>{c.restaurantName}</td>}
                    <td>{fmtDate(c.visitDate)}</td>
                    <td>{fmtDate(c.createdAt)}</td>
                    <td>{c.couponOffer || <span className="muted">—</span>}</td>
                    <td>
                      {hasMailingAddress(c) ? (
                        <>
                          {c.streetAddress}, {c.city}, {c.state} {c.zip}
                        </>
                      ) : (
                        <Badge kind="warn">no full address</Badge>
                      )}{' '}
                      {c.addressStatus && c.addressStatus !== 'Verified' && c.addressStatus !== 'Corrected' && <AddressBadge status={c.addressStatus} />}
                    </td>
                    <td>
                      {c.responded ? <Badge kind="ok">Sent</Badge> : <Badge kind="danger">Not sent</Badge>}{' '}
                      {c.printCount > 0 && <Badge kind="neutral">printed {c.printCount}×</Badge>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
