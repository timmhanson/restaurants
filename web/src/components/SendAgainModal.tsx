import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { CardSend, CommentSummary, MailStatus } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { MAIL_STATUS } from '../pages/MailCardsPage'
import AddressCheckNote from './AddressCheckNote'
import AddressSuggestInput from './AddressSuggestInput'
import { Alert, Badge, Field, Modal, Spinner, fmtDateTime } from './ui'

const money = (cents: number) => `$${(cents / 100).toFixed(2)}`
const HOW: Record<string, string> = { mail: 'Mailed for you', stamp: 'Printed here, stamp by hand' }
const line = (s: { streetAddress?: string | null; city?: string | null; state?: string | null; zip?: string | null }) =>
  s.streetAddress ? `${s.streetAddress}, ${[s.city, [s.state, s.zip].filter(Boolean).join(' ')].filter(Boolean).join(', ')}` : null

function Result({ s }: { s: CardSend }) {
  if (s.outcome === 'refused') return <><Badge kind="danger">not taken</Badge>{s.error && <div className="small" style={{ color: 'var(--danger)' }}>{s.error}</div>}</>
  if (s.outcome === 'cancelled') return <Badge kind="neutral">cancelled before printing</Badge>
  if (s.method === 'mail') {
    const status = MAIL_STATUS[s.mailStatus as MailStatus]
    return <><Badge kind={status?.kind ?? 'ok'}>{status?.label ?? s.mailStatus ?? 'sent'}</Badge>{s.test && <> <Badge kind="warn">test order</Badge></>}</>
  }
  return <Badge kind="ok">sent</Badge>
}

/**
 * A card's history of sends, and sending it again. Every attempt is listed with when it was made, how, to which
 * address and what came of it. To send the card again the address is checked (and corrected if it was wrong:
 * the addresses earlier attempts used stay in the history), then the card goes to the usual mailing or printing
 * screen, where what it costs is shown and confirmed before anything is sent.
 */
export default function SendAgainModal({ card, onClose, onChanged }: { card: CommentSummary; onClose: () => void; onChanged: (card: CommentSummary) => void }) {
  const { has } = useAuth()
  const nav = useNavigate()
  const [sends, setSends] = useState<CardSend[] | null>(null)
  const [a, setA] = useState({ streetAddress: card.streetAddress ?? '', city: card.city ?? '', state: card.state ?? '', zip: card.zip ?? '' })
  const [saved, setSaved] = useState(a)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const guest = `${card.guestFirstName} ${card.guestLastName}`.trim()

  useEffect(() => {
    api.get<CardSend[]>(`/comments/${card.id}/sends`).then(setSends).catch((e) => setError((e as Error).message))
  }, [card.id])

  const canSend = has('MarkResponded')
  const changed = (Object.keys(a) as (keyof typeof a)[]).some((k) => a[k].trim() !== saved[k].trim())
  const complete = Boolean(a.streetAddress.trim() && a.city.trim() && a.state.trim() && a.zip.trim())
  // A mailed card that is still waiting to be printed is cancelled and corrected, not sent a second time.
  const notPrintedYet = card.mailStatus === 'ready' || card.mailStatus === 'printing'
  const went = (sends ?? []).filter((s) => s.outcome === 'sent')

  const saveAddress = async () => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const next = await api.put<CommentSummary>(`/comments/${card.id}/address`, a)
      const now = { streetAddress: next.streetAddress ?? '', city: next.city ?? '', state: next.state ?? '', zip: next.zip ?? '' }
      setA(now)
      setSaved(now)
      setNotice('The address was saved. The addresses used before stay in the history above.')
      onChanged(next)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title={`Sends · ${guest}`} onClose={onClose} wide>
      <div className="grid" data-no-track>
        {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
        <h3 style={{ margin: 0 }}>Every attempt to send this card</h3>
        {!sends ? (!error && <Spinner label="Loading…" />) : sends.length === 0 ? (
          <p className="muted" style={{ margin: 0 }}>This card has not been sent yet.</p>
        ) : (
          <div className="table-wrap">
            <table className="compact">
              <thead><tr><th>#</th><th>When</th><th>How</th><th>Address used</th><th>What came of it</th><th>By</th></tr></thead>
              <tbody>
                {sends.map((s) => (
                  <tr key={s.number}>
                    <td>{s.number}</td>
                    <td>{fmtDateTime(s.at)}{s.resend && <div className="muted small">sent again</div>}</td>
                    <td>{HOW[s.method] ?? s.method}{s.billedCents > 0 && <div className="muted small">{money(s.billedCents)}</div>}</td>
                    <td>{line(s) ?? <span className="muted">not recorded</span>}</td>
                    <td><Result s={s} /></td>
                    <td>{s.byName ?? <span className="muted">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {canSend && (
          <>
            <h3 style={{ margin: '0.5rem 0 0' }}>{went.length > 0 ? 'Send it again' : 'Send it'}</h3>
            {notPrintedYet ? (
              <Alert kind="info">
                This card is with the mailing service and has not been printed yet. To change where it goes, cancel it on Mail Cards (it is then not billed), correct the
                address and send it again.
              </Alert>
            ) : (
              <>
                <p className="muted" style={{ margin: 0 }}>
                  Check where it is going first. If the address was wrong, correct it here: this attempt uses the new address, and the earlier ones keep the address they used.
                </p>
                {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
                <Field label="Street address">
                  <AddressSuggestInput value={a.streetAddress} city={a.city} state={a.state} zip={a.zip} restaurantId={card.restaurantId}
                    onChange={(streetAddress) => setA((s) => ({ ...s, streetAddress }))}
                    onPick={(p) => setA((s) => ({ streetAddress: p.streetAddress, city: p.city ?? s.city, state: p.state ?? s.state, zip: p.zip ?? s.zip }))} />
                </Field>
                <div className="row">
                  <Field label="City"><input value={a.city} onChange={(e) => setA((s) => ({ ...s, city: e.target.value }))} autoComplete="off" /></Field>
                  <Field label="State"><input value={a.state} onChange={(e) => setA((s) => ({ ...s, state: e.target.value }))} maxLength={20} autoComplete="off" /></Field>
                  <Field label="Zip"><input value={a.zip} onChange={(e) => setA((s) => ({ ...s, zip: e.target.value }))} inputMode="numeric" autoComplete="off" /></Field>
                </div>
                <AddressCheckNote street={a.streetAddress} city={a.city} state={a.state} zip={a.zip} onUse={(s) => setA({ streetAddress: s.streetAddress, city: s.city, state: s.state, zip: s.zip })} onKeep={() => undefined} />
                <div className="actions">
                  {changed && <button type="button" className="btn btn-primary" onClick={() => void saveAddress()} disabled={busy || !complete}>{busy ? 'Saving…' : 'Save the address'}</button>}
                  <button type="button" className="btn" disabled={busy || changed || !complete} title={changed ? 'Save the address first' : undefined}
                    onClick={() => nav(`/print-cards?ids=${card.id}${went.length > 0 ? '&resend=1' : ''}`)}>
                    Print it here{went.length > 0 ? ' again' : ''}
                  </button>
                  {has('BuyPostage') && (
                    <button type="button" className={`btn ${changed ? '' : 'btn-primary'}`} disabled={busy || changed || !complete} title={changed ? 'Save the address first' : undefined}
                      onClick={() => nav(`/mail-cards?ids=${card.id}${went.length > 0 ? '&resend=1' : ''}`)}>
                      Have it mailed{went.length > 0 ? ' again' : ''}
                    </button>
                  )}
                </div>
                <p className="muted small" style={{ margin: 0 }}>
                  The next screen shows the card and what it costs, and nothing goes until you confirm there. A card mailed for you again is paid for again.
                </p>
              </>
            )}
          </>
        )}
        <div className="actions"><button type="button" className="btn" onClick={onClose}>Close</button></div>
      </div>
    </Modal>
  )
}
