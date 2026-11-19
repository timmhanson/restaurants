import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { api } from '../api/client'
import type { CardCheck } from '../api/types'
import { followUpsChanged } from '../components/FollowUpNotice'
import { Alert, Badge, Confirm, Spinner, fmtDate } from '../components/ui'

/**
 * Where the QR code on a coupon card leads. Only someone signed in gets this far. It shows whether the code is
 * genuine and the card as the site has it (guest, offer, dates, whether it has been redeemed), so the card in hand
 * can be compared with the record, and lets someone with permission mark the coupon redeemed.
 */
export default function VerifyCardPage() {
  const { code = '' } = useParams()
  const [check, setCheck] = useState<CardCheck | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [asking, setAsking] = useState(false)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<string | null>(null)

  const load = () => {
    setError(null)
    api.get<CardCheck>(`/cards/check/${encodeURIComponent(code)}`).then(setCheck).catch((e) => setError((e as Error).message))
  }
  useEffect(load, [code])

  const redeem = async () => {
    if (!check?.card) return
    setBusy(true)
    setError(null)
    try {
      await api.post(`/comments/${check.card.commentId}/redeem`, {})
      setDone('Marked as redeemed today. This card cannot be used again.')
      followUpsChanged()
      load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
      setAsking(false)
    }
  }

  const card = check?.card
  const state = !check ? null : !check.genuine ? 'bad' : !card ? 'other' : card.redeemedOn ? 'used' : card.expired ? 'expired' : 'good'

  return (
    <>
      <div className="page-head">
        <h1>Check a coupon card</h1>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {done && <Alert kind="success" onClose={() => setDone(null)}>{done}</Alert>}
      {!check ? (
        !error && <Spinner label="Checking the card…" />
      ) : (
        <div className="panel" style={{ maxWidth: 640 }}>
          <p style={{ marginTop: 0 }}>
            {state === 'good' && <Badge kind="ok">Genuine</Badge>}
            {state === 'used' && <Badge kind="danger">Already redeemed</Badge>}
            {state === 'expired' && <Badge kind="warn">Expired</Badge>}
            {state === 'other' && <Badge kind="neutral">Genuine, another restaurant’s</Badge>}
            {state === 'bad' && <Badge kind="danger">Not genuine</Badge>}
          </p>
          <Alert kind={state === 'good' ? 'success' : state === 'bad' || state === 'used' ? 'error' : state === 'expired' ? 'warn' : 'info'}>{check.message}</Alert>
          {card && (
            <>
              <dl className="kv">
                <dt>Guest</dt>
                <dd><strong>{card.guestName}</strong></dd>
                <dt>Restaurant</dt>
                <dd>{card.restaurant}</dd>
                <dt>Offer</dt>
                <dd><strong>{card.offer || 'None recorded'}</strong></dd>
                <dt>Date issued</dt>
                <dd>{fmtDate(card.dateIssued)}</dd>
                <dt>Good until</dt>
                <dd>{fmtDate(card.expiresOn)}{card.expired ? ' (past)' : ''}</dd>
                <dt>Redeemed</dt>
                <dd>{card.redeemedOn ? `${fmtDate(card.redeemedOn)}${card.redeemedByName ? ` by ${card.redeemedByName}` : ''}` : 'Not yet'}</dd>
                <dt>Card ID</dt>
                <dd><code style={{ fontSize: '0.85em' }}>{card.cardId}</code></dd>
              </dl>
              <p className="muted small">
                If the offer or the dates printed on the card differ from what is shown here, the card has been changed: go by this page, not the card.
              </p>
              <div className="row-actions">
                {check.canRedeem && (
                  <button type="button" className="btn btn-primary" onClick={() => setAsking(true)} disabled={busy}>Mark coupon redeemed</button>
                )}
                {card.canOpen && <Link className="btn" to={`/comments?card=${card.commentId}`}>Open the full record</Link>}
              </div>
              {!check.canRedeem && !card.redeemedOn && (
                <p className="muted small" style={{ marginBottom: 0 }}>You can check cards but not mark them redeemed. A manager or owner can.</p>
              )}
            </>
          )}
        </div>
      )}
      {asking && card && (
        <Confirm
          title="Mark this coupon redeemed?"
          message={`${card.guestName} used “${card.offer || 'the coupon'}” at ${card.restaurant} today${card.expired ? `, after it expired on ${fmtDate(card.expiresOn)}` : ''}. The card cannot be used again.`}
          confirmLabel="Mark redeemed"
          busy={busy}
          onConfirm={() => void redeem()}
          onCancel={() => setAsking(false)}
        />
      )}
    </>
  )
}
