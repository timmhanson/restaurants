import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { ActivityDto, ActivityKind, CommentSummary, FollowUpRequest, FollowUpResponse } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { followUpsChanged } from './FollowUpNotice'
import { Alert, Badge, Field, Spinner, fmtDate, fmtDateTime, today } from './ui'

const DEFAULT_METHODS = ['Mailed coupon card', 'Phone call', 'Email', 'Text message', 'In person']

/** Days since the guest's visit, for "waiting" labels. */
export const waitingDays = (c: CommentSummary) => Math.max(0, Math.floor((Date.now() - new Date(c.visitDate + 'T12:00:00').getTime()) / 86_400_000))

/** Compact follow-up state for tables: replied, tried, or needs a reply (with how long it has waited). */
export function FollowUpBadge({ c }: { c: CommentSummary }) {
  if (c.responded)
    return (
      <span title={`${c.replyMethod ?? 'Reply'} by ${c.respondedByName ?? 'someone'}`}>
        <Badge kind="ok">Replied</Badge>
        <div className="muted small">{[c.replyMethod, fmtDate(c.repliedOn)].filter(Boolean).join(' · ')}</div>
      </span>
    )
  const days = waitingDays(c)
  return (
    <span>
      {c.attemptCount > 0 ? <Badge kind="warn">Tried ×{c.attemptCount}</Badge> : <Badge kind="danger">Needs reply</Badge>}
      <div className={`small ${days > 7 ? 'text-danger' : 'muted'}`}>{days === 0 ? 'visited today' : `${days} day${days === 1 ? '' : 's'} since visit`}</div>
    </span>
  )
}

const KIND_LABEL: Record<ActivityKind, string> = {
  Note: 'Note', Attempt: 'Tried to reach guest', Replied: 'Reply confirmed', Reopened: 'Reopened', Printed: 'Card printed', PostageBought: 'Postage',
  Redeemed: 'Coupon redeemed', RedemptionUndone: 'Redemption taken back', FollowedUp: 'Followed up with guest', FollowUpAttempt: 'Tried to follow up',
}
const KIND_BADGE: Record<ActivityKind, 'neutral' | 'warn' | 'ok' | 'danger' | 'accent'> = {
  Note: 'neutral', Attempt: 'warn', Replied: 'ok', Reopened: 'danger', Printed: 'neutral', PostageBought: 'neutral', Redeemed: 'accent',
  RedemptionUndone: 'danger', FollowedUp: 'ok', FollowUpAttempt: 'warn',
}

const TITLES: Record<Exclude<Mode, null>, string> = {
  reply: 'How did you reply to the guest?',
  attempt: 'How did you try to reach them?',
  reopen: 'Why does this need another reply?',
  call: 'Follow-up: did the coupon card arrive?',
  redeem: 'The guest used the coupon',
  unredeem: 'Why is the redemption being taken back?',
}
const TEXT_LABEL = (mode: Mode, reached: boolean) =>
  mode === 'reply' ? 'What you sent or said (optional)'
  : mode === 'attempt' ? 'What happened (optional)'
  : mode === 'call' ? (reached ? 'Notes about the conversation' : 'What happened (optional)')
  : mode === 'redeem' ? 'Note (optional)'
  : 'Reason'
const TEXT_HINT = (mode: Mode, reached: boolean) =>
  mode === 'attempt' ? 'e.g. Left a voicemail, number disconnected'
  : mode === 'reply' ? 'e.g. Mailed a Free Value Basket card, apologised for the wait'
  : mode === 'call' ? (reached ? 'Did the card arrive? What did the guest say? The other managers can read this.' : 'e.g. Left a voicemail, will try again Thursday')
  : mode === 'redeem' ? 'e.g. Used at the drive-thru, which team member took it'
  : mode === 'unredeem' ? 'e.g. Marked on the wrong guest'
  : 'e.g. Card came back undeliverable'
const SUBMIT = (mode: Mode, reached: boolean) =>
  mode === 'reply' ? 'Confirm reply'
  : mode === 'attempt' ? 'Log attempt'
  : mode === 'call' ? (reached ? 'Save follow-up' : 'Log attempt')
  : mode === 'redeem' ? 'Mark redeemed'
  : mode === 'unredeem' ? 'Undo redeemed'
  : 'Reopen'

type Mode = 'reply' | 'attempt' | 'reopen' | 'call' | 'redeem' | 'unredeem' | null

/** True once the guest's follow-up day (did the card arrive?) has come and nobody has done it. */
export const followUpDue = (c: CommentSummary) => Boolean(c.followUpDueOn && c.followUpDueOn <= today())

/** After the card is sent: follow-up due, followed up, and whether the coupon has been redeemed. */
export function AfterSendBadges({ c }: { c: CommentSummary }) {
  return (
    <>
      {c.redeemedOn && <Badge kind="accent" title={`Redeemed ${fmtDate(c.redeemedOn)} (recorded by ${c.redeemedByName ?? 'someone'})`}>Redeemed</Badge>}
      {c.followedUpOn && <Badge kind="ok" title={`Followed up ${fmtDate(c.followedUpOn)} by ${c.followedUpByName ?? 'someone'}`}>Followed up</Badge>}
      {followUpDue(c) && <Badge kind="warn" title={`Due since ${fmtDate(c.followUpDueOn)}`}>Follow-up due</Badge>}
    </>
  )
}

/**
 * Follow-up for one comment: record how the guest was replied to (with a confirmation), log attempts that did
 * not reach them, reopen a reply that fell through, and leave notes that every manager of the restaurant can read.
 */
export default function FollowUpPanel({ comment, onChanged, startWith }: { comment: CommentSummary; onChanged: (c: CommentSummary) => void; startWith?: Mode }) {
  const { has, settings } = useAuth()
  const canReply = has('MarkResponded')
  const methods = settings?.replyMethods?.length ? settings.replyMethods : DEFAULT_METHODS
  const [activity, setActivity] = useState<ActivityDto[] | null>(null)
  const [mode, setMode] = useState<Mode>(startWith && canReply ? startWith : null)
  const [method, setMethod] = useState(methods[0])
  const [on, setOn] = useState(today())
  const [text, setText] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [reached, setReached] = useState(true)
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    setActivity(null)
    api.get<ActivityDto[]>(`/comments/${comment.id}/activity`).then(setActivity).catch(() => setActivity([]))
  }, [comment.id])

  const open = (m: Mode) => {
    setMode(m)
    setText('')
    setConfirmed(false)
    setReached(true)
    if (m === 'call') setMethod(methods.find((x) => /phone/i.test(x)) ?? 'Phone call')
    setOn(today())
    setError(null)
  }

  /** Redeeming and the after-sending follow-up have their own endpoints; both answer like a follow-up. */
  const post = async (path: string, body: unknown) => {
    setBusy(true)
    setError(null)
    try {
      const res = await api.post<FollowUpResponse>(`/comments/${comment.id}/${path}`, body)
      setActivity(res.activity)
      onChanged(res.comment)
      followUpsChanged()
      return true
    } catch (e) {
      setError((e as Error).message)
      return false
    } finally {
      setBusy(false)
    }
  }

  const send = async (req: FollowUpRequest) => {
    setBusy(true)
    setError(null)
    try {
      const res = await api.post<FollowUpResponse>(`/comments/${comment.id}/followup`, req)
      setActivity(res.activity)
      onChanged(res.comment)
      followUpsChanged()
      return true
    } catch (e) {
      setError((e as Error).message)
      return false
    } finally {
      setBusy(false)
    }
  }

  const submitAction = async (e: FormEvent) => {
    e.preventDefault()
    const ok =
      mode === 'reply'
        ? await send({ kind: 'Replied', method, contactedOn: on, text, confirmed })
        : mode === 'attempt'
          ? await send({ kind: 'Attempt', method, contactedOn: on, text })
          : mode === 'call'
            ? await post('followed-up', { reached, method, contactedOn: on, notes: text })
            : mode === 'redeem'
              ? await post('redeem', { redeemedOn: on, note: text })
              : mode === 'unredeem'
                ? await post('unredeem', { reason: text })
                : await send({ kind: 'Reopened', text })
    if (ok) setMode(null)
  }

  const submitNote = async (e: FormEvent) => {
    e.preventDefault()
    if (await send({ kind: 'Note', text: note })) setNote('')
  }

  return (
    <section className="followup">
      <div className="followup-head">
        <h3>Follow-up</h3>
        {comment.responded ? (
          <span>
            <Badge kind="ok">Replied</Badge>{' '}
            <span className="muted small">
              {comment.replyMethod ?? 'method not recorded'}
              {comment.repliedOn ? ` on ${fmtDate(comment.repliedOn)}` : ''} · confirmed by {comment.respondedByName}
            </span>
          </span>
        ) : (
          <FollowUpBadge c={comment} />
        )}
      </div>

      {canReply && mode === null && (
        <div className="row-actions">
          {!comment.responded && (
            <>
              <button type="button" className="btn btn-sm btn-primary" onClick={() => open('reply')}>✓ Record reply</button>
              <button type="button" className="btn btn-sm" onClick={() => open('attempt')}>Log attempt</button>
            </>
          )}
          {comment.responded && !comment.followedUpOn && (
            <button type="button" className={`btn btn-sm ${followUpDue(comment) ? 'btn-primary' : ''}`} onClick={() => open('call')}>☎ Record follow-up call</button>
          )}
          {!comment.redeemedOn ? (
            <button type="button" className="btn btn-sm" onClick={() => open('redeem')}>Coupon redeemed</button>
          ) : (
            <button type="button" className="btn btn-sm" onClick={() => open('unredeem')}>Undo redeemed</button>
          )}
          <Link className="btn btn-sm" to={`/print-cards?ids=${comment.id}`}>🖨 Print card</Link>
          {comment.responded && <button type="button" className="btn btn-sm" onClick={() => open('reopen')}>Reopen</button>}
        </div>
      )}

      {(comment.followUpDueOn || comment.followedUpOn || comment.redeemedOn || comment.printedAt) && (
        <div className="after-send small">
          {comment.printedAt && (
            <div>
              Printed {fmtDateTime(comment.printedAt)} by {comment.printedByName}
              {comment.printCount > 1 ? ` (${comment.printCount} times)` : ''}
              {comment.postageCents != null ? ` · ${comment.postageTest ? 'test postage' : `postage $${(comment.postageCents / 100).toFixed(2)}`}` : ' · stamp by hand'}
            </div>
          )}
          {comment.followedUpOn ? (
            <div>Followed up {fmtDate(comment.followedUpOn)} by {comment.followedUpByName}</div>
          ) : comment.followUpDueOn ? (
            <div className={followUpDue(comment) ? 'text-danger' : 'muted'}>
              {followUpDue(comment) ? `Follow-up with the guest has been due since ${fmtDate(comment.followUpDueOn)}: did the card arrive?` : `Follow up with the guest on or after ${fmtDate(comment.followUpDueOn)}.`}
            </div>
          ) : null}
          {comment.redeemedOn && <div>Coupon redeemed {fmtDate(comment.redeemedOn)} (recorded by {comment.redeemedByName})</div>}
        </div>
      )}

      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}

      {mode && (
        <form className="followup-form" onSubmit={submitAction}>
          <strong>{TITLES[mode]}</strong>
          {mode === 'call' && (
            <div className="source-choice two" role="radiogroup">
              <label className={reached ? 'selected' : ''}>
                <input type="radio" name="reached" checked={reached} onChange={() => setReached(true)} />
                <span className="source-label">I spoke with the guest</span>
                <span className="source-hint">Completes the follow-up.</span>
              </label>
              <label className={!reached ? 'selected' : ''}>
                <input type="radio" name="reached" checked={!reached} onChange={() => setReached(false)} />
                <span className="source-label">No answer</span>
                <span className="source-hint">Kept as an attempt; the follow-up stays due.</span>
              </label>
            </div>
          )}
          {mode === 'redeem' && (
            <Field label="Date redeemed">
              <input type="date" value={on} max={today()} min={comment.dateIssued} onChange={(e) => setOn(e.target.value)} required />
            </Field>
          )}
          {(mode === 'reply' || mode === 'attempt' || mode === 'call') && (
            <div className="form-row">
              <Field label="Method">
                <select value={method} onChange={(e) => setMethod(e.target.value)}>
                  {methods.map((m) => <option key={m}>{m}</option>)}
                  <option>Other</option>
                </select>
              </Field>
              <Field label={mode === 'reply' ? 'Date sent or made' : mode === 'call' ? 'Date of the call' : 'Date tried'}>
                <input type="date" value={on} max={today()} min={mode === 'call' ? (comment.repliedOn ?? comment.visitDate) : comment.visitDate} onChange={(e) => setOn(e.target.value)} required />
              </Field>
            </div>
          )}
          <Field label={TEXT_LABEL(mode, reached)} hint={TEXT_HINT(mode, reached)}>
            <textarea
              rows={mode === 'call' ? 4 : 2}
              value={text}
              onChange={(e) => setText(e.target.value)}
              required={mode === 'reopen' || mode === 'unredeem' || (mode === 'call' && reached)}
              maxLength={2000}
            />
          </Field>
          {mode === 'reply' && (
            <label className="check confirm-box">
              <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
              I confirm this reply was actually {method === 'Phone call' || method === 'In person' ? 'made' : 'sent'} to {comment.guestFirstName} {comment.guestLastName}.
            </label>
          )}
          <div className="row-actions">
            <button type="button" className="btn btn-sm" onClick={() => setMode(null)} disabled={busy}>Cancel</button>
            <button type="submit" className="btn btn-sm btn-primary" disabled={busy || (mode === 'reply' && !confirmed)}>
              {busy ? 'Saving…' : SUBMIT(mode, reached)}
            </button>
          </div>
        </form>
      )}

      {activity === null ? (
        <Spinner />
      ) : activity.length === 0 ? (
        <p className="muted small">No follow-up yet.</p>
      ) : (
        <ol className="timeline">
          {activity.map((a) => (
            <li key={a.id} className={`tl-${a.kind.toLowerCase()}`}>
              <div className="tl-head">
                <Badge kind={KIND_BADGE[a.kind]}>{KIND_LABEL[a.kind]}</Badge>
                {a.method && <strong>{a.method}</strong>}
                {a.contactedOn && <span className="muted">on {fmtDate(a.contactedOn)}</span>}
              </div>
              {a.text && <div className="tl-text">{a.text}</div>}
              <div className="muted small">
                {a.createdByName} ({a.createdByRole}) · {fmtDateTime(a.createdAt)}
              </div>
            </li>
          ))}
        </ol>
      )}

      {has('ViewComments') && (
        <form className="note-form" onSubmit={submitNote}>
          <textarea rows={2} value={note} onChange={(e) => setNote(e.target.value)} placeholder="Add a note for the other managers…" maxLength={2000} aria-label="Note for other managers" />
          <button type="submit" className="btn btn-sm" disabled={busy || !note.trim()}>Add note</button>
        </form>
      )}
    </section>
  )
}
