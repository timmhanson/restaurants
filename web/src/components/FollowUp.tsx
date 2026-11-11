import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { ActivityDto, ActivityKind, CommentSummary, FollowUpRequest, FollowUpResponse } from '../api/types'
import { useAuth } from '../auth/AuthContext'
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

const KIND_LABEL: Record<ActivityKind, string> = { Note: 'Note', Attempt: 'Tried to reach guest', Replied: 'Reply confirmed', Reopened: 'Reopened' }
const KIND_BADGE: Record<ActivityKind, 'neutral' | 'warn' | 'ok' | 'danger'> = { Note: 'neutral', Attempt: 'warn', Replied: 'ok', Reopened: 'danger' }

type Mode = 'reply' | 'attempt' | 'reopen' | null

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
    setOn(today())
    setError(null)
  }

  const send = async (req: FollowUpRequest) => {
    setBusy(true)
    setError(null)
    try {
      const res = await api.post<FollowUpResponse>(`/comments/${comment.id}/followup`, req)
      setActivity(res.activity)
      onChanged(res.comment)
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
          {comment.responded && <button type="button" className="btn btn-sm" onClick={() => open('reopen')}>Reopen</button>}
        </div>
      )}

      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}

      {mode && (
        <form className="followup-form" onSubmit={submitAction}>
          <strong>{mode === 'reply' ? 'How did you reply to the guest?' : mode === 'attempt' ? 'How did you try to reach them?' : 'Why does this need another reply?'}</strong>
          {mode !== 'reopen' && (
            <div className="form-row">
              <Field label="Method">
                <select value={method} onChange={(e) => setMethod(e.target.value)}>
                  {methods.map((m) => <option key={m}>{m}</option>)}
                  <option>Other</option>
                </select>
              </Field>
              <Field label={mode === 'reply' ? 'Date sent or made' : 'Date tried'}>
                <input type="date" value={on} max={today()} min={comment.visitDate} onChange={(e) => setOn(e.target.value)} required />
              </Field>
            </div>
          )}
          <Field
            label={mode === 'reply' ? 'What you sent or said (optional)' : mode === 'attempt' ? 'What happened (optional)' : 'Reason'}
            hint={mode === 'attempt' ? 'e.g. Left a voicemail, number disconnected' : mode === 'reply' ? 'e.g. Mailed a Free Value Basket card, apologised for the wait' : 'e.g. Card came back undeliverable'}
          >
            <textarea rows={2} value={text} onChange={(e) => setText(e.target.value)} required={mode === 'reopen'} maxLength={2000} />
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
              {busy ? 'Saving…' : mode === 'reply' ? 'Confirm reply' : mode === 'attempt' ? 'Log attempt' : 'Reopen'}
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
