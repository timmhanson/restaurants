import { useEffect, useMemo, useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { CommentSummary, MailOutcome, MailPreview, MailStatus, MailStatusInfo, PagedResult, PrintConfig } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { AddressBadge } from '../components/AddressCheckNote'
import { followUpsChanged } from '../components/FollowUpNotice'
import { hasMailingAddress } from '../components/PostcardFace'
import { Alert, Badge, Confirm, Spinner } from '../components/ui'

const money = (cents: number) => `$${(cents / 100).toFixed(2)}`
const name = (c?: CommentSummary) => (c ? `${c.guestFirstName} ${c.guestLastName}` : 'a card')

/** PostGrid's statuses in plain words. */
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
      <Badge kind={s.kind} title="PostGrid prints and mails this card">PostGrid: {s.label}</Badge>
      {test && <> <Badge kind="warn" title="Sent with a test key: not printed, mailed or charged">test order</Badge></>}
    </>
  )
}

/** One side of the card, drawn from the same HTML PostGrid receives, scaled to fit. */
function Side({ html, label }: { html: string; label: string }) {
  const scale = 0.56 // 6.25in (600px) shown 336px wide
  return (
    <figure style={{ margin: 0 }}>
      <div style={{ width: 600 * scale, height: 408 * scale, overflow: 'hidden', border: '1px solid var(--border)', borderRadius: 4, background: '#fff' }}>
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
 * Sends the chosen coupon cards to PostGrid, which prints both sides and mails them, billed to the investment group's
 * own PostGrid account. Shows both sides first, confirms the count and the expected cost, then each card's progress,
 * with a way to cancel any card PostGrid has not started printing.
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
  const [busy, setBusy] = useState<'send' | 'status' | 'cancel' | null>(null)
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

  const withPostGrid = (c: CommentSummary) => (outcomes[c.id]?.ok ? true : Boolean(c.mailStatus && c.mailStatus !== 'cancelled'))
  const statusOf = (c: CommentSummary): MailStatus | null => statuses[c.id]?.status ?? outcomes[c.id]?.status ?? c.mailStatus ?? null
  const blocked = (c: CommentSummary) => (!hasMailingAddress(c) ? 'no full mailing address' : c.responded && !withPostGrid(c) ? 'already marked as sent' : null)
  const toSend = cards.filter((c) => !withPostGrid(c) && !blocked(c) && !outcomes[c.id])
  const sent = cards.filter(withPostGrid)
  const failed = Object.values(outcomes).filter((o) => !o.ok)
  const tooMany = toSend.length > config.mailMaxCards
  const total = toSend.length * config.mailCentsPerCard

  const send = async () => {
    setBusy('send')
    setError(null)
    setNotice(null)
    try {
      const res = await api.post<MailOutcome[]>('/comments/mail', { commentIds: toSend.map((c) => c.id), confirmCount: toSend.length })
      setOutcomes((o) => ({ ...o, ...Object.fromEntries(res.map((r) => [r.commentId, r])) }))
      const ok = res.filter((r) => r.ok && !r.alreadyMailed).length
      if (ok > 0)
        setNotice(
          config.mailTest
            ? `${ok} test order${ok === 1 ? '' : 's'} sent to PostGrid. Nothing is printed, mailed or charged with a test key. The cards are marked as sent.`
            : `${ok} card${ok === 1 ? ' is' : 's are'} with PostGrid to print and mail, and marked as sent.${settings.followUpDays > 0 ? ` You will be reminded to follow up after ${settings.followUpDays} day${settings.followUpDays === 1 ? '' : 's'}.` : ''}`,
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
        <h1>Mail cards with PostGrid</h1>
        <span className="grow" />
        <button type="button" className="btn" onClick={() => nav('/print')}>Back to Print cards</button>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}

      {cards.length === 0 ? (
        <Alert kind="warn">No cards were chosen. Go to <Link to="/print">Print cards</Link> and choose which to mail.</Alert>
      ) : !config.mailAvailable ? (
        <Alert kind="warn">This investment group has no PostGrid account yet. An Investment Group Administrator can add it in Settings.</Alert>
      ) : (
        <>
          {config.mailTest && (
            <Alert kind="warn">This group’s PostGrid account uses a test key: cards are not printed, mailed or charged. PostGrid makes a preview of each one.</Alert>
          )}
          <div className="panel">
            <h2>What PostGrid prints</h2>
            <p className="muted">
              Both sides of {previewId ? `${name(cards.find((c) => c.id === previewId))}'s` : 'the'} card. PostGrid adds the guest’s address, the restaurant as the
              return address, and the postage in the blank area on the right of the back.{' '}
              {!config.hasCardArt && 'No front picture has been uploaded, so the front is plain. An administrator can upload one in Settings.'}
            </p>
            {preview ? (
              <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap' }}>
                <Side html={preview.frontHtml} label="Front" />
                <Side html={preview.backHtml} label="Back (address and postage added by PostGrid)" />
              </div>
            ) : (
              <Spinner label="Drawing the card…" />
            )}
          </div>

          <div className="panel">
            <div className="page-head">
              <h2 style={{ margin: 0 }}>
                {toSend.length > 0 ? `${toSend.length} card${toSend.length === 1 ? '' : 's'} to send` : sent.length > 0 ? `${sent.length} card${sent.length === 1 ? '' : 's'} with PostGrid` : 'Nothing to send'}
              </h2>
              <span className="grow" />
              {sent.length > 0 && (
                <button type="button" className="btn" onClick={() => void refresh()} disabled={busy !== null}>{busy === 'status' ? 'Checking…' : 'Check status with PostGrid'}</button>
              )}
              {toSend.length > 0 && (
                <button type="button" className="btn btn-primary" onClick={() => setConfirm(true)} disabled={busy !== null || !config.canMail || tooMany}>
                  Mail {toSend.length} card{toSend.length === 1 ? '' : 's'}
                </button>
              )}
            </div>
            {!config.canMail && toSend.length > 0 && <Alert kind="info">Sending cards to PostGrid spends money, so it needs the “buy postage” permission. Ask an owner or administrator.</Alert>}
            {tooMany && <Alert kind="warn">Up to {config.mailMaxCards} cards can be mailed at a time. Go back and choose fewer.</Alert>}
            {failed.map((o) => (
              <Alert key={o.commentId} kind="error">Not sent: {name(cards.find((c) => c.id === o.commentId))}. {o.error}</Alert>
            ))}
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Guest</th>
                    <th>Restaurant</th>
                    <th>Offer</th>
                    <th>Mailing address</th>
                    <th>PostGrid</th>
                    <th aria-label="Actions" />
                  </tr>
                </thead>
                <tbody>
                  {cards.map((c) => {
                    const why = withPostGrid(c) ? null : blocked(c)
                    const status = statusOf(c)
                    const previewUrl = statuses[c.id]?.previewUrl
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
                          {previewUrl && <a className="btn btn-sm" href={previewUrl} target="_blank" rel="noreferrer">PostGrid’s proof</a>}
                          {status === 'ready' && withPostGrid(c) && (
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
          title={`Mail ${toSend.length} card${toSend.length === 1 ? '' : 's'} with PostGrid?`}
          message={
            config.mailTest
              ? `These go to PostGrid with the group’s test key: nothing is printed, mailed or charged. Each card is marked as sent.`
              : `PostGrid prints and mails ${toSend.length === 1 ? 'this card' : `these ${toSend.length} cards`} and charges the investment group’s PostGrid account: about ${money(config.mailCentsPerCard)} a card, about ${money(total)} in all (PostGrid’s published price on its free plan; paid plans cost less). Each card is marked as sent. A card can be cancelled here until PostGrid starts printing it, usually the next business day.`
          }
          confirmLabel={config.mailTest ? 'Send test orders' : `Mail and pay about ${money(total)}`}
          busy={busy === 'send'}
          onConfirm={() => void send()}
          onCancel={() => setConfirm(false)}
        />
      )}
      {cancelId && (
        <Confirm
          title="Cancel this mailing?"
          message={`PostGrid will not print or mail ${name(cards.find((c) => c.id === cancelId))}'s card, and it goes back to the cards not sent yet. This only works until PostGrid starts printing it.`}
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
