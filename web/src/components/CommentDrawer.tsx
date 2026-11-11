import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { CommentSummary } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Badge, Spinner, fmtDate, fmtDateTime } from './ui'

interface Props {
  comment: CommentSummary
  onClose: () => void
  onEdit?: (c: CommentSummary) => void
  onToggleResponded?: (c: CommentSummary) => void
  onDelete?: (c: CommentSummary) => void
}

export default function CommentDrawer({ comment, onClose, onEdit, onToggleResponded, onDelete }: Props) {
  const { has } = useAuth()
  const [history, setHistory] = useState<CommentSummary[] | null>(null)

  useEffect(() => {
    setHistory(null)
    api
      .get<CommentSummary[]>(`/comments/guest/${encodeURIComponent(comment.guestKey)}`)
      .then(setHistory)
      .catch(() => setHistory([]))
  }, [comment.guestKey, comment.id, comment.responded])

  const address = [comment.streetAddress, [comment.city, comment.state].filter(Boolean).join(', '), comment.zip].filter(Boolean).join(' · ')

  return (
    <>
      <div className="drawer-backdrop" onClick={onClose} />
      <aside className="drawer" aria-label="Comment details">
        <div className="page-head">
          <h2 style={{ margin: 0 }}>
            {comment.guestFirstName} {comment.guestLastName}
          </h2>
          {comment.guestCommentCount > 1 && <Badge kind="warn">{comment.guestCommentCount} cards on file</Badge>}
          <span className="grow" />
          <button type="button" className="btn-icon" aria-label="Close" onClick={onClose}>
            ×
          </button>
        </div>
        <div className="row-actions" style={{ marginBottom: '1rem' }}>
          {onToggleResponded && has('MarkResponded') && (
            <button type="button" className={`btn btn-sm ${comment.responded ? '' : 'btn-primary'}`} onClick={() => onToggleResponded(comment)}>
              {comment.responded ? 'Mark not responded' : 'Mark responded'}
            </button>
          )}
          {onEdit && has('EditComments') && (
            <button type="button" className="btn btn-sm" onClick={() => onEdit(comment)}>
              Edit
            </button>
          )}
          {onDelete && has('DeleteComments') && (
            <button type="button" className="btn btn-sm btn-danger" onClick={() => onDelete(comment)}>
              Remove
            </button>
          )}
        </div>
        <dl className="kv">
          <dt>Restaurant</dt>
          <dd>{comment.restaurantName}</dd>
          <dt>Visit</dt>
          <dd>
            {fmtDate(comment.visitDate)} ({comment.dayOfWeek})
          </dd>
          <dt>Reason</dt>
          <dd>{comment.reason}</dd>
          <dt>Details</dt>
          <dd>{comment.reasonDetails || <span className="muted">—</span>}</dd>
          <dt>Coupon</dt>
          <dd>
            {comment.couponOffer || <span className="muted">none recorded</span>}
            <div className="muted">
              Issued {fmtDate(comment.dateIssued)} · good until {fmtDate(comment.expiresOn)}
              {comment.authorizedBy ? ` · signed by ${comment.authorizedBy}` : ''}
            </div>
          </dd>
          <dt>Phone</dt>
          <dd>{comment.phone || <span className="muted">—</span>}</dd>
          <dt>Address</dt>
          <dd>{address || <span className="muted">—</span>}</dd>
          <dt>Responded</dt>
          <dd>
            {comment.responded ? (
              <>
                <Badge kind="ok">Yes</Badge> {comment.respondedByName} · {fmtDateTime(comment.respondedAt)}
              </>
            ) : (
              <Badge kind="danger">Not yet</Badge>
            )}
          </dd>
          <dt>Notes</dt>
          <dd>{comment.notes || <span className="muted">—</span>}</dd>
          <dt>Entered</dt>
          <dd>
            {comment.createdByName} · {fmtDateTime(comment.createdAt)}
            {comment.updatedAt && <div className="muted">updated {fmtDateTime(comment.updatedAt)}</div>}
          </dd>
        </dl>

        <h3 style={{ marginTop: '1.25rem' }}>Guest history</h3>
        {history === null ? (
          <Spinner />
        ) : (
          <ul className="history">
            {history.map((h) => (
              <li key={h.id} className={h.id === comment.id ? 'current' : ''}>
                <strong>{fmtDate(h.visitDate)}</strong> · {h.restaurantName} · {h.reason}
                {h.couponOffer ? ` · ${h.couponOffer}` : ''} {h.responded ? <Badge kind="ok">responded</Badge> : <Badge kind="danger">open</Badge>}
                {h.reasonDetails && <div className="muted">{h.reasonDetails}</div>}
              </li>
            ))}
          </ul>
        )}
      </aside>
    </>
  )
}
