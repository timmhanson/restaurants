import CorrectAddressModal from '../components/CorrectAddressModal'
import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api, downloadBlob } from '../api/client'
import type { CommentSummary, MailOutcome, MailPreview, MailStatus, MailStatusInfo, PagedResult, PrintConfig } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { AddressBadge } from '../components/AddressCheckNote'
import { followUpsChanged } from '../components/FollowUpNotice'
import { hasMailingAddress } from '../components/PostcardFace'
import { Alert, Badge, Confirm, Spinner } from '../components/ui'

const money = (cents: number) => `$${(cents / 100).toFixed(2)}`
const name = (c?: CommentSummary) => (c ? `${c.guestFirstName} ${c.guestLastName}` : 'a card')

/** Where a mailed card is, in plain words. */
export const MAIL_STATUS: Record<MailStatus, { label: string; kind: 'neutral' | 'warn' | 'danger' | 'ok' | 'accent' }> = {
  ready: { label: 'waiting to print', kind: 'accent' },
  printing: { label: 'printing', kind: 'accent' },
  processed_for_delivery: { label: 'handed to USPS', kind: 'ok' },
  completed: { label: 'mailed', kind: 'ok' },
  cancelled: { label: 'cancelled', kind: 'neutral' },
}

export function MailStatusBadge({ status, test }: { status?: MailStatus | null; test?: boolean }) {
  if (!status) return null
  const s = MAIL_STATUS[status] ?? { label: status, kind: 'neutral' as const }
  return (
    <>
      <Badge kind={s.kind} title="This card is printed and mailed for you">Mailing: {s.label}</Badge>
      {test && <> <Badge kind="warn" title="A test order: not printed, mailed or charged">test order</Badge></>}
    </>
  )
}

/** A part of the back that is printed over when the card is mailed, drawn on top of the preview (never sent to the printer). */
function MailZone({ left, top, width, height, scale, children }: { left: number; top: number; width: number; height: number; scale: number; children: string }) {
  return (
    <div
      aria-hidden="true"
      style={{
        position: 'absolute', zIndex: 1, left: left * scale, top: top * scale, width: width * scale, height: height * scale, boxSizing: 'border-box',
        border: '1px dashed #9aa3b2', background: 'repeating-linear-gradient(135deg, rgba(120,130,150,.10) 0 6px, rgba(120,130,150,.03) 6px 12px)',
        display: 'flex', alignItems: 'center', justifyContent: 'center', textAlign: 'center', padding: 4, color: '#5b6474', fontSize: Math.max(9, 12 * scale), lineHeight: 1.25,
      }}
    >
      {children}
    </div>
  )
}

/**
 * One side of the card, drawn from the same HTML the printer receives, scaled to fit. With mailZones (the back), the
 * areas the mail house prints over are shaded: the guest's address and postage on the right, and a barcode strip.
 */
export function Side({ html, label, scale = 0.56, mailZones = false }: { html: string; label: string; scale?: number; mailZones?: boolean }) {
  // The card is 6.25 x 4.25 inches, 600 x 408 pixels at full size.
  return (
    <figure style={{ margin: 0 }}>
      <div style={{ position: 'relative', width: 600 * scale, height: 408 * scale, overflow: 'hidden', border: '1px solid var(--border)', borderRadius: 4, background: '#fff' }}>
        {mailZones && (
          <>
            <MailZone left={362} top={0} width={238} height={408} scale={scale}>The guest’s address and the postage are printed here</MailZone>
            <MailZone left={144} top={338} width={218} height={70} scale={scale}>Barcode</MailZone>
          </>
        )}
        <iframe
          title={label}
          srcDoc={html}
          // No scripts run in the card. Same-origin only so the card-front picture loads like any image on the page.
          sandbox="allow-same-origin"
          style={{ width: 600, height: 408, border: 0, transform: `scale(${scale})`, transformOrigin: '0 0', pointerEvents: 'none' }}
        />
      </div>
      <figcaption className="muted small">{label}</figcaption>
    </figure>
  )
}

/**
 * Sends the chosen coupon cards to be printed (both sides) and mailed for the investment group, which the site bills
 * per card. Shows both sides first, confirms the count and the exact price, then each card's progress, with a way to
 * cancel any card that has not started printing. The company that prints them is never named here.
 */
export default function MailCardsPage() {
  const { settings } = useAuth()
  const [params] = useSearchParams()
  const nav = useNavigate()
  const ids = useMemo(() => (params.get('ids') ?? '').split(',').map(Number).filter((n) => n > 0), [params])
  const [cards, setCards] = useState<CommentSummary[] | null>(null)
  const [config, setConfig] = useState<PrintConfig | null>(null)
  const [previewId, setPreviewId] = useState<number | null>(null)
  const [preview, setPreview] = useState<MailPreview | null>(null)
  const [outcomes, setOutcomes] = useState<Record<number, MailOutcome>>({})
  const [statuses, setStatuses] = useState<Record<number, MailStatusInfo>>({})
  const [confirm, setConfirm] = useState(false)
  const [cancelId, setCancelId] = useState<number | null>(null)
  /** The card whose address is being corrected. */
  const [fixing, setFixing] = useState<CommentSummary | null>(null)
  const [busy, setBusy] = useState<'send' | 'status' | 'cancel' | 'proof' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = () =>
    Promise.all([api.get<PagedResult<CommentSummary>>(`/comments?ids=${ids.join(',')}&pageSize=200`), api.get<PrintConfig>('/comments/print-config')])
      .then(([list, cfg]) => {
        const sorted = [...list.items].sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id))
        setCards(sorted)
        setConfig(cfg)
        setPreviewId((p) => p ?? sorted.find(hasMailingAddress)?.id ?? sorted[0]?.id ?? null)
      })
      .catch((e) => {
        setError((e as Error).message)
        setCards([])
      })

  useEffect(() => {
    if (!ids.length) return setCards([])
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ids])

  useEffect(() => {
    if (!previewId) return setPreview(null)
    setPreview(null)
    api.get<MailPreview>(`/comments/${previewId}/mail/preview`).then(setPreview).catch((e) => setError((e as Error).message))
  }, [previewId])

  if (!settings || cards === null || !config) return error ? <Alert kind="error">{error}</Alert> : <Spinner label="Getting the cards ready…" />

  // Sending again (from a card's history): cards that went before go once more, and each is paid for again.
  const again = params.get('resend') === '1'
  const notPrintedYet = (c: CommentSummary) => c.mailStatus === 'ready' || c.mailStatus === 'printing'
  const beingMailed = (c: CommentSummary) => (outcomes[c.id]?.ok ? true : again ? notPrintedYet(c) : Boolean(c.mailStatus && c.mailStatus !== 'cancelled'))
  const statusOf = (c: CommentSummary): MailStatus | null => statuses[c.id]?.status ?? outcomes[c.id]?.status ?? c.mailStatus ?? null
  // Whether cards can be mailed, and whether for real or as tests, is set restaurant by restaurant.
  const rules = (c: CommentSummary) => config.mailRestaurants.find((r) => r.restaurantId === c.restaurantId)
  const blocked = (c: CommentSummary) =>
    !hasMailingAddress(c) ? 'no full mailing address'
      : c.responded && !beingMailed(c) && !again ? 'already marked as sent'
        : rules(c) && !rules(c)!.on ? 'having cards mailed is not switched on for this restaurant'
          : rules(c) && !rules(c)!.ready ? 'mailing is not set up for this restaurant yet'
            : null
  const toSend = cards.filter((c) => !beingMailed(c) && !blocked(c) && !outcomes[c.id])
  const sent = cards.filter(beingMailed)
  const failed = Object.values(outcomes).filter((o) => !o.ok)
  const tooMany = toSend.length > config.mailMaxCards
  // Test orders are a proof only: not printed, mailed or billed. Only the cards going out for real cost anything.
  const liveToSend = toSend.filter((c) => rules(c)?.live ?? !config.mailTest)
  const testOnly = toSend.length > 0 ? liveToSend.length === 0 : config.mailTest
  const mixed = liveToSend.length > 0 && liveToSend.length < toSend.length
  const total = liveToSend.length * config.mailCentsPerCard
  const overLimit = !testOnly && toSend.length > 0 && config.mailUsedCents + total > config.mailLimitCents
  // With no payment method saved, the group mails on its credit, and only as far as the credit goes.
  const creditCovers = config.mailCentsPerCard > 0 ? Math.floor(config.mailCreditCents / config.mailCentsPerCard) : 0
  const overCredit = !testOnly && config.mailOnCredit && toSend.length > 0 && total > config.mailCreditCents

  const send = async () => {
    setBusy('send')
    setError(null)
    setNotice(null)
    try {
      const res = await api.post<MailOutcome[]>('/comments/mail', { commentIds: toSend.map((c) => c.id), confirmCount: toSend.length, confirmCentsPerCard: config.mailCentsPerCard, resend: again })
      setOutcomes((o) => ({ ...o, ...Object.fromEntries(res.map((r) => [r.commentId, r])) }))
      const ok = res.filter((r) => r.ok && !r.alreadyMailed).length
      if (ok > 0)
        setNotice(
          testOnly
            ? `${ok} test order${ok === 1 ? '' : 's'} sent. Nothing is printed, mailed or charged in test mode. The cards are marked as sent.`
            : `${ok} card${ok === 1 ? ' is' : 's are'} on the way to be printed and mailed, and marked as sent.${settings.followUpDays > 0 ? ` You will be reminded to follow up after ${settings.followUpDays} day${settings.followUpDays === 1 ? '' : 's'}.` : ''}`,
        )
      followUpsChanged()
      await load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
      setConfirm(false)
    }
  }

  const refresh = async () => {
    setBusy('status')
    setError(null)
    try {
      const res = await api.post<MailStatusInfo[]>('/comments/mail/status', { commentIds: sent.map((c) => c.id) })
      setStatuses((s) => ({ ...s, ...Object.fromEntries(res.map((r) => [r.commentId, r])) }))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const proof = async (c: CommentSummary) => {
    setBusy('proof')
    setError(null)
    try {
      downloadBlob(await api.blob(`/comments/${c.id}/mail/proof`), `card-proof-${c.guestLastName || c.id}.pdf`)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const cancel = async () => {
    if (!cancelId) return
    setBusy('cancel')
    setError(null)
    try {
      const r = await api.post<MailStatusInfo>(`/comments/${cancelId}/mail/cancel`)
      setStatuses((s) => ({ ...s, [r.commentId]: r }))
      setOutcomes((o) => {
        const next = { ...o }
        delete next[cancelId]
        return next
      })
      setNotice(`${name(cards.find((c) => c.id === cancelId))}'s card was cancelled before printing and is back in the list of cards not sent yet.`)
      followUpsChanged()
      await load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
      setCancelId(null)
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>{again ? 'Mail cards again' : 'Mail cards'}</h1>
        <span className="grow" />
        <button type="button" className="btn" onClick={() => nav('/print')}>Back to Print cards</button>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}

      {cards.length === 0 ? (
        <Alert kind="warn">No cards were chosen. Go to <Link to="/print">Print cards</Link> and choose which to mail.</Alert>
      ) : !config.mailAvailable ? (
        <Alert kind="warn">Having cards printed and mailed for you is not switched on for this investment group. Print them from <Link to="/print">Print cards</Link> instead.</Alert>
      ) : (
        <>
          {testOnly && (
            <Alert kind="warn">Mailing is in test mode: cards are not printed, mailed or charged. A proof of each one is made.</Alert>
          )}
          <div className="panel">
            <h2>What is printed</h2>
            <p className="muted">
              Both sides of {previewId ? `${name(cards.find((c) => c.id === previewId))}'s` : 'the'} card. The guest’s address, the restaurant as the return
              address, and the postage are added in the blank area on the right of the back. The look of the card is set on <Link to="/card-design">Card design</Link>.
            </p>
            {preview ? (
              <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap' }}>
                <Side html={preview.frontHtml} label="Front" />
                <Side html={preview.backHtml} label="Back (the shaded areas are printed over when it is mailed)" mailZones />
              </div>
            ) : (
              <Spinner label="Drawing the card…" />
            )}
          </div>

          <div className="panel">
            <div className="page-head">
              <h2 style={{ margin: 0 }}>
                {toSend.length > 0 ? `${toSend.length} card${toSend.length === 1 ? '' : 's'} to send` : sent.length > 0 ? `${sent.length} card${sent.length === 1 ? '' : 's'} being mailed` : 'Nothing to send'}
              </h2>
              <span className="grow" />
              {sent.length > 0 && (
                <button type="button" className="btn" onClick={() => void refresh()} disabled={busy !== null}>{busy === 'status' ? 'Checking…' : 'Check where they are'}</button>
              )}
              {toSend.length > 0 && (
                <button type="button" className="btn btn-primary" onClick={() => setConfirm(true)} disabled={busy !== null || !config.canMail || tooMany || overLimit || overCredit}>
                  Mail {toSend.length} card{toSend.length === 1 ? '' : 's'}
                </button>
              )}
            </div>
            {config.mailNeedsPayMethod && toSend.length > 0 && (
              <Alert kind="warn">A payment method must be saved before cards can be mailed for this investment group. An Investment Group Administrator can add it on <Link to="/billing">Billing</Link>. Until then, print the cards from Print cards.</Alert>
            )}
            {!config.canMail && !config.mailNeedsPayMethod && toSend.length > 0 && <Alert kind="info">Having cards mailed spends money, so it needs the “buy postage” permission. Ask an owner or administrator.</Alert>}
            {config.mailOnCredit && !testOnly && toSend.length > 0 && !overCredit && (
              <Alert kind="info">
                These are paid from your investment group’s credit: {money(config.mailCreditCents)} is left, and {money(total)} of it will be used.
              </Alert>
            )}
            {mixed && (
              <Alert kind="info">
                {toSend.length - liveToSend.length} of these cards {toSend.length - liveToSend.length === 1 ? 'is from a restaurant' : 'are from restaurants'} set to test mailing: a proof is
                made and nothing is printed, mailed or billed. The other {liveToSend.length} {liveToSend.length === 1 ? 'is' : 'are'} really mailed, {money(total)} in all.
              </Alert>
            )}
            {overCredit && (
              <Alert kind="warn">
                Your investment group’s credit ({money(config.mailCreditCents)}) covers {creditCovers} card{creditCovers === 1 ? '' : 's'} at {money(config.mailCentsPerCard)} each, and {toSend.length} are
                chosen. Go back and choose fewer, or save a payment method on <Link to="/billing">Billing</Link> to mail them all.
              </Alert>
            )}
            {overLimit && (
              <Alert kind="warn">
                Mailing these {toSend.length} cards ({money(total)}) would pass this month’s limit for mailed cards: {money(config.mailUsedCents)} of {money(config.mailLimitCents)} is used.
                Go back and choose fewer, or ask the site’s administrator to raise the limit.
              </Alert>
            )}
            {again && toSend.length > 0 && (
              <Alert kind="warn">
                {toSend.length === 1 ? 'This card has been sent before' : 'These cards have been sent before'}. Sending {toSend.length === 1 ? 'it' : 'them'} again prints and mails
                {toSend.length === 1 ? ' a new card' : ' new cards'}{testOnly ? '' : `, paid for again (${money(total)})`}. Each card’s history keeps every send and the address it used.
              </Alert>
            )}
            {tooMany && <Alert kind="warn">Up to {config.mailMaxCards} cards can be mailed at a time. Go back and choose fewer.</Alert>}
            {failed.map((o) => {
              const card = cards.find((c) => c.id === o.commentId)
              return (
                <Alert key={o.commentId} kind="error">
                  Not sent: {name(card)}. {o.error}{' '}
                  {card && o.addressProblem && (
                    <button type="button" className="btn btn-sm btn-primary" onClick={() => setFixing(card)} aria-label="Correct the address of the card that was not sent">Correct the address</button>
                  )}
                </Alert>
              )
            })}
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Guest</th>
                    <th>Restaurant</th>
                    <th>Offer</th>
                    <th>Mailing address</th>
                    <th>Mailing</th>
                    <th aria-label="Actions" />
                  </tr>
                </thead>
                <tbody>
                  {cards.map((c) => {
                    const why = beingMailed(c) ? null : blocked(c)
                    const status = statusOf(c)
                    const hasProof = statuses[c.id]?.hasProof
                    return (
                      <tr key={c.id} className={previewId === c.id ? 'selected' : undefined}>
                        <td><strong>{name(c)}</strong></td>
                        <td>{c.restaurantName}</td>
                        <td>{c.couponOffer || <span className="muted">—</span>}</td>
                        <td>
                          {hasMailingAddress(c) ? `${c.streetAddress}, ${c.city}, ${c.state} ${c.zip}` : <Badge kind="warn">no full address</Badge>}{' '}
                          {c.addressStatus && c.addressStatus !== 'Verified' && c.addressStatus !== 'Corrected' && <AddressBadge status={c.addressStatus} />}
                        </td>
                        <td>
                          {why ? (
                            <Badge kind="warn">{why}</Badge>
                          ) : status && status !== 'cancelled' ? (
                            <MailStatusBadge status={status} test={c.mailTest || outcomes[c.id]?.test} />
                          ) : outcomes[c.id] && !outcomes[c.id].ok ? (
                            <Badge kind="danger">not sent</Badge>
                          ) : (
                            <span className="muted">to send</span>
                          )}
                          {statuses[c.id]?.error && <div className="muted small">{statuses[c.id]?.error}</div>}
                        </td>
                        <td className="row-actions" style={{ whiteSpace: 'nowrap' }}>
                          <button type="button" className="btn btn-sm" onClick={() => setPreviewId(c.id)} disabled={previewId === c.id}>Show card</button>
                          {!beingMailed(c) && !(c.responded && !outcomes[c.id]) && (
                            <button type="button" className={`btn btn-sm ${!hasMailingAddress(c) || outcomes[c.id]?.addressProblem ? 'btn-primary' : ''}`} onClick={() => setFixing(c)} disabled={busy !== null}
                              aria-label="Correct this card’s mailing address">
                              {hasMailingAddress(c) ? 'Correct address' : 'Add address'}
                            </button>
                          )}
                          {hasProof && <button type="button" className="btn btn-sm" onClick={() => void proof(c)} disabled={busy !== null} aria-label={`Download the printed proof of ${name(c)}'s card`}>{busy === 'proof' ? 'Getting…' : 'Printed proof'}</button>}
                          {status === 'ready' && beingMailed(c) && (
                            <button type="button" className="btn btn-sm btn-danger" onClick={() => setCancelId(c.id)} disabled={busy !== null}>Cancel mailing</button>
                          )}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}

      {confirm && (
        <Confirm
          title={`Mail ${toSend.length} card${toSend.length === 1 ? '' : 's'}?`}
          message={
            testOnly
              ? `Mailing is in test mode: nothing is printed, mailed or charged. Each card is marked as sent.`
              : `${toSend.length === 1 ? 'This card is' : `These ${toSend.length} cards are`} printed and mailed for you, and your investment group is billed ${money(config.mailCentsPerCard)} a card: ${money(total)} in all, on next month’s bill (${money(config.mailUsedCents)} of this month’s ${money(config.mailLimitCents)} limit is already used). Each card is marked as sent. A card can be cancelled here until it starts printing, usually the next business day; a cancelled card is not billed.`
          }
          confirmLabel={testOnly ? 'Send test orders' : `Mail for ${money(total)}`}
          busy={busy === 'send'}
          onConfirm={() => void send()}
          onCancel={() => setConfirm(false)}
        />
      )}
      {fixing && (
        <CorrectAddressModal
          card={fixing}
          problem={outcomes[fixing.id]?.addressProblem ? outcomes[fixing.id]?.error : null}
          onClose={() => setFixing(null)}
          onSaved={(saved) => {
            // The corrected card takes its place in the list, and is among the cards to send again.
            setCards((list) => list && list.map((c) => (c.id === saved.id ? { ...c, ...saved } : c)))
            setOutcomes((o) => {
              const next = { ...o }
              delete next[saved.id]
              return next
            })
            setNotice(`${name(saved)}’s address was saved. The card is among the cards to send.`)
            setFixing(null)
          }}
        />
      )}
      {cancelId && (
        <Confirm
          title="Cancel this mailing?"
          message={`${name(cards.find((c) => c.id === cancelId))}'s card will not be printed or mailed, it will not be billed, and it goes back to the cards not sent yet. This only works until it starts printing.`}
          confirmLabel="Cancel the mailing"
          danger
          busy={busy === 'cancel'}
          onConfirm={() => void cancel()}
          onCancel={() => setCancelId(null)}
        />
      )}
    </>
  )
}
