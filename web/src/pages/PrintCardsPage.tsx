import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { mailedForYouHint } from './PrintCenterPage'
import { api } from '../api/client'
import type { CardQr, CommentSummary, PagedResult, PostageOutcome, PrintConfig } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { AddressBadge } from '../components/AddressCheckNote'
import PostcardFace, { PostageLabel, hasMailingAddress } from '../components/PostcardFace'
import { Alert, Badge, Confirm, Spinner } from '../components/ui'

const money = (cents: number) => `$${(cents / 100).toFixed(2)}`

/**
 * Prints the chosen coupon cards onto postcards, one card per page at the card's real size.
 * Postage is either bought here and printed as the card's address side, or left off for a stamp by hand.
 * After the print dialog closes, one confirmation marks every card as sent to its guest.
 */
export default function PrintCardsPage() {
  const { settings, restaurants, isGroupWide } = useAuth()
  const [params] = useSearchParams()
  const nav = useNavigate()
  const ids = useMemo(() => (params.get('ids') ?? '').split(',').map(Number).filter((n) => n > 0), [params])
  const [cards, setCards] = useState<CommentSummary[] | null>(null)
  const [config, setConfig] = useState<PrintConfig | null>(null)
  const [confirmFee, setConfirmFee] = useState(false)
  // The Print cards tab passes the choices made there; both can still be changed here.
  const [postage, setPostage] = useState<'stamp' | 'bought'>(params.get('postage') === 'bought' ? 'bought' : 'stamp')
  const [outcomes, setOutcomes] = useState<Record<number, PostageOutcome>>({})
  const [markSent, setMarkSent] = useState(params.get('markSent') !== '0')
  const [confirmBuy, setConfirmBuy] = useState(false)
  const [askPrinted, setAskPrinted] = useState(false)
  const [done, setDone] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const printing = useRef(false)
  // Each card's QR code, which leads staff back to the card's record after signing in.
  const [qrs, setQrs] = useState<Record<number, string>>({})
  useEffect(() => {
    if (!ids.length) return
    api.post<CardQr[]>('/comments/qr', { commentIds: ids })
      .then((list) => setQrs(Object.fromEntries(list.map((q) => [q.commentId, q.image]))))
      .catch((e) => setError((e as Error).message))
  }, [ids])

  useEffect(() => {
    if (!ids.length) return setCards([])
    Promise.all([api.get<PagedResult<CommentSummary>>(`/comments?ids=${ids.join(',')}&pageSize=200`), api.get<PrintConfig>('/comments/print-config')])
      .then(([list, cfg]) => {
        // Keep the order the cards were picked in.
        setCards([...list.items].sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id)))
        setConfig(cfg)
        if (!cfg.canBuyPostage) setPostage('stamp')
      })
      .catch((e) => {
        setError((e as Error).message)
        setCards([])
      })
  }, [ids])

  // The browser tells us when its print dialog closes, but not whether anything printed, so ask.
  useEffect(() => {
    const after = () => {
      if (printing.current) {
        printing.current = false
        setAskPrinted(true)
      }
    }
    window.addEventListener('afterprint', after)
    return () => window.removeEventListener('afterprint', after)
  }, [])

  if (!settings || cards === null) return <Spinner label="Getting the cards ready…" />
  const card = settings.postcard
  const labelOf = (c: CommentSummary) => (postage === 'bought' ? (outcomes[c.id]?.labelUrl ?? c.postageLabelUrl ?? null) : null)
  const mailable = cards.filter(hasMailingAddress)
  const needPostage = postage === 'bought' ? mailable.filter((c) => !labelOf(c)) : []
  const bought = cards.filter((c) => labelOf(c))
  const failed = Object.values(outcomes).filter((o) => !o.ok)
  const spent = Object.values(outcomes).filter((o) => o.ok && !o.alreadyBought).reduce((n, o) => n + (o.cents ?? 0), 0)

  const buy = async () => {
    setConfirmBuy(false)
    setBusy(true)
    setError(null)
    try {
      const res = await api.post<PostageOutcome[]>('/comments/postage', { commentIds: needPostage.map((c) => c.id) })
      setOutcomes((o) => ({ ...o, ...Object.fromEntries(res.map((r) => [r.commentId, r])) }))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const print = () => {
    printing.current = true
    window.print()
  }
  // A card printed here for a stamp by hand is billed to the group once, the first time it is printed.
  const feeEach = postage === 'stamp' ? config?.printFeeCents ?? 0 : 0
  const feeCards = feeEach > 0 ? cards.filter((c) => c.printCount === 0 && c.printFeeCents == null) : []
  const feeTotal = feeCards.length * feeEach

  const recordPrinted = async () => {
    setBusy(true)
    setError(null)
    try {
      await api.post('/comments/printed', { commentIds: cards.map((c) => c.id), markSent, postage })
      const days = settings.followUpDays
      setAskPrinted(false)
      setDone(
        markSent
          ? `${cards.length} card${cards.length === 1 ? ' is' : 's are'} marked as sent.${days > 0 ? ` You will be reminded to follow up with each guest after ${days} day${days === 1 ? '' : 's'}.` : ''}`
          : `Recorded that ${cards.length} card${cards.length === 1 ? ' was' : 's were'} printed. They are not marked as sent.`,
      )
    } catch (e) {
      setError((e as Error).message)
      setAskPrinted(false)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="print-page">
      <style>{`@page { size: ${card.widthIn}in ${card.heightIn}in; margin: 0; }`}</style>
      <div className="print-toolbar no-print">
        <div className="inner">
          <div className="page-head" style={{ marginBottom: 0 }}>
            <h1 style={{ margin: 0 }}>Print coupon cards</h1>
            <span className="grow" />
            <button type="button" className="btn" onClick={() => (done ? nav('/print') : nav(-1))}>{done ? 'Back to Print cards' : 'Cancel'}</button>
          </div>
          {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
          {done ? (
            <Alert kind="success">{done}</Alert>
          ) : cards.length === 0 ? (
            <Alert kind="warn">No cards were chosen. Go to <Link to="/print">Print cards</Link> and choose which to print.</Alert>
          ) : (
            <>
              <div className="muted">
                {cards.length} card{cards.length === 1 ? '' : 's'} on {card.stock === 'Preprinted' ? 'pre-printed cards (only the blanks are printed)' : 'blank postcards (the whole card is printed)'},{' '}
                {card.widthIn} × {card.heightIn} inches. In the print dialog choose that paper size, no margins, and scale 100%.{' '}
                {isGroupWide && <Link to="/settings">Card layout settings</Link>}
              </div>
              <fieldset className="role-choice">
                <legend>Postage</legend>
                <div className="source-choice" role="radiogroup">
                  <label className={postage === 'stamp' ? 'selected' : ''}>
                    <input type="radio" name="postage" checked={postage === 'stamp'} onChange={() => setPostage('stamp')} />
                    <span className="source-label">Stamps by hand</span>
                    <span className="source-hint">
                      The card prints with the guest's address and a box for the stamp.{' '}
                      {config && config.printFeeCents > 0
                        ? `Your investment group is billed ${money(config.printFeeCents)} for each card, the first time it is printed.`
                        : 'Nothing is charged.'}
                    </span>
                  </label>
                  <label className={postage === 'bought' ? 'selected' : ''} style={config?.canBuyPostage ? undefined : { opacity: 0.6 }}>
                    <input type="radio" name="postage" checked={postage === 'bought'} disabled={!config?.canBuyPostage} onChange={() => setPostage('bought')} />
                    <span className="source-label">Buy and print postage</span>
                    <span className="source-hint">
                      {config?.canBuyPostage
                        ? `USPS postage is bought for each card through ${config.postageProvider} and printed as its address side. Charged when bought.`
                        : !config?.postageAvailable
                          ? 'Not set up on this site yet. Ask your System Administrator.'
                          : 'You need the "buy postage" permission. Ask an owner or administrator.'}
                    </span>
                  </label>
                  {/* Nothing is printed here for this one: choosing it goes to the screen that shows both sides and sends the cards. */}
                  <label style={config?.canMail ? undefined : { opacity: 0.6 }}>
                    <input type="radio" name="postage" checked={false} disabled={!config?.canMail} aria-label="Mailed for you"
                      onChange={() => nav(`/mail-cards?ids=${ids.join(',')}`)} />
                    <span className="source-label">Mailed for you</span>
                    <span className="source-hint">{mailedForYouHint(config, isGroupWide)}</span>
                  </label>
                </div>
              </fieldset>
              {cards.length > mailable.length && (
                <Alert kind="warn">
                  {cards.length - mailable.length} of these cards {cards.length - mailable.length === 1 ? 'has' : 'have'} no full mailing address (street, city, state and zip), so{' '}
                  {cards.length - mailable.length === 1 ? 'it' : 'they'} cannot be mailed as printed. Add the address on the card first, or hand the card over in person.
                </Alert>
              )}
              {failed.map((o) => (
                <Alert key={o.commentId} kind="error">
                  Postage was not bought for {name(cards.find((c) => c.id === o.commentId))}: {o.error} That card will print for a stamp by hand.
                </Alert>
              ))}
              {spent > 0 && <Alert kind="success">Postage bought: {money(spent)} for {Object.values(outcomes).filter((o) => o.ok && !o.alreadyBought).length} card(s).</Alert>}
              {bought.length > 0 && (
                <Alert kind="info">
                  Cards with bought postage print as two pages each: the coupon, then the mailing label. Print the label on the back of the card (two-sided
                  printing) or on a label the same size.
                </Alert>
              )}
              <div className="row-actions" style={{ alignItems: 'center' }}>
                {needPostage.length > 0 && failed.length === 0 ? (
                  <button type="button" className="btn btn-primary" onClick={() => setConfirmBuy(true)} disabled={busy}>
                    {busy ? 'Buying postage…' : `Buy postage for ${needPostage.length} card${needPostage.length === 1 ? '' : 's'}`}
                  </button>
                ) : (
                  <button type="button" className="btn btn-primary" onClick={() => (feeTotal > 0 ? setConfirmFee(true) : print())} disabled={busy}>
                    Print {cards.length} card{cards.length === 1 ? '' : 's'}{feeTotal > 0 ? ` (${money(feeTotal)})` : ''}
                  </button>
                )}
                <label className="check">
                  <input type="checkbox" checked={markSent} onChange={(e) => setMarkSent(e.target.checked)} /> Mark as sent once printed
                </label>
              </div>
            </>
          )}
        </div>
      </div>

      <div className="print-sheet">
        {cards.map((c) => {
          const label = labelOf(c)
          return (
            <div key={c.id} style={{ display: 'contents' }}>
              <div className="print-caption no-print">
                <strong>{name(c)}</strong>
                <span>{c.restaurantName}</span>
                {c.responded && <Badge kind="ok">already marked sent</Badge>}
                {c.printCount > 0 && <Badge kind="neutral">printed {c.printCount}× before</Badge>}
                {!hasMailingAddress(c) && <Badge kind="warn">no mailing address</Badge>}
                {c.addressStatus && c.addressStatus !== 'Verified' && c.addressStatus !== 'Corrected' && <AddressBadge status={c.addressStatus} />}
                {label && <Badge kind={c.postageTest || outcomes[c.id]?.test ? 'warn' : 'accent'}>{c.postageTest || outcomes[c.id]?.test ? 'test postage' : 'postage bought'}</Badge>}
              </div>
              <div className="print-scroll">
                <PostcardFace card={c} settings={card} restaurant={restaurants.find((r) => r.id === c.restaurantId)} validityMonths={settings.couponValidityMonths} withAddress={!label} outline qr={qrs[c.id]} />
              </div>
              {label && (
                <div className="print-scroll">
                  <PostageLabel url={label} settings={card} test={Boolean(c.postageTest || outcomes[c.id]?.test)} />
                </div>
              )}
            </div>
          )
        })}
      </div>

      {confirmFee && (
        <Confirm
          title={`Print ${cards.length} card${cards.length === 1 ? '' : 's'}?`}
          message={`Your investment group is billed ${money(feeEach)} for each card printed here for a stamp by hand: ${money(feeTotal)} for ${feeCards.length === 1 ? 'this card' : `these ${feeCards.length} cards`}, on next month’s bill.${cards.length > feeCards.length ? ` ${cards.length - feeCards.length} of the cards chosen ${cards.length - feeCards.length === 1 ? 'was' : 'were'} printed before and ${cards.length - feeCards.length === 1 ? 'is' : 'are'} not charged again.` : ''} A card is charged once, however many times it is printed.`}
          confirmLabel={`Print and bill ${money(feeTotal)}`}
          onCancel={() => setConfirmFee(false)}
          onConfirm={() => { setConfirmFee(false); print() }}
        />
      )}
      {confirmBuy && (
        <Confirm
          title={`Buy postage for ${needPostage.length} card${needPostage.length === 1 ? '' : 's'}?`}
          message={`USPS postcard postage will be bought for each card through ${config?.postageProvider} and charged straight away (no more than ${money(config?.maxCentsPerCard ?? 150)} a card). Postage that has been bought cannot be refunded here.`}
          confirmLabel="Buy postage"
          busy={busy}
          onConfirm={() => void buy()}
          onCancel={() => setConfirmBuy(false)}
        />
      )}
      {askPrinted && (
        <Confirm
          title="Did the cards print correctly?"
          message={
            markSent
              ? `Yes marks ${cards.length === 1 ? 'this card' : `all ${cards.length} cards`} as sent to the guest by mail. Choose Cancel if they did not print, then try again.`
              : `Yes records that ${cards.length === 1 ? 'this card was' : `these ${cards.length} cards were`} printed. Choose Cancel if they did not print.`
          }
          confirmLabel={markSent ? 'Yes, mark as sent' : 'Yes, they printed'}
          busy={busy}
          onConfirm={() => void recordPrinted()}
          onCancel={() => setAskPrinted(false)}
        />
      )}
    </div>
  )
}

const name = (c?: CommentSummary) => (c ? `${c.guestFirstName} ${c.guestLastName}` : 'a card')
