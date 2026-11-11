import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, downloadBlob, qs } from '../api/client'
import type { CommentSummary, PagedResult } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import CardForm, { cardFromSummary } from '../components/CardForm'
import CommentDrawer from '../components/CommentDrawer'
import { FollowUpBadge } from '../components/FollowUp'
import { Alert, Badge, Confirm, Empty, Field, Modal, Spinner, daysAgo, fmtDate } from '../components/ui'

type Sort = 'visitDate' | 'createdAt' | 'guest' | 'reason' | 'lastActivity'
type Status = '' | 'waiting' | 'needs-reply' | 'attempted' | 'replied'

export default function CommentsPage() {
  const { restaurants, settings, has } = useAuth()
  const [restaurantId, setRestaurantId] = useState<number | ''>('')
  const [from, setFrom] = useState(daysAgo(90))
  const [to, setTo] = useState('')
  const [status, setStatus] = useState<Status>('')
  const [reason, setReason] = useState('')
  const [search, setSearch] = useState('')
  const [repeatOnly, setRepeatOnly] = useState(false)
  const [sort, setSort] = useState<Sort>('visitDate')
  const [dir, setDir] = useState<'asc' | 'desc'>('desc')
  const [page, setPage] = useState(1)
  const pageSize = 50
  const [data, setData] = useState<PagedResult<CommentSummary> | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<CommentSummary | null>(null)
  const [replyFirst, setReplyFirst] = useState(false)
  const [editing, setEditing] = useState<CommentSummary | null>(null)
  const [deleting, setDeleting] = useState<CommentSummary | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const res = await api.get<PagedResult<CommentSummary>>(
        `/comments${qs({ restaurantId, from, to, status, reason, search, repeatOnly, sort, dir, page, pageSize })}`,
      )
      setData(res)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }, [restaurantId, from, to, status, reason, search, repeatOnly, sort, dir, page])

  useEffect(() => {
    const t = window.setTimeout(() => void load(), search ? 350 : 0)
    return () => window.clearTimeout(t)
  }, [load, search])

  const toggleSort = (s: Sort) => {
    if (sort === s) setDir(dir === 'asc' ? 'desc' : 'asc')
    else {
      setSort(s)
      setDir(s === 'guest' || s === 'reason' ? 'asc' : 'desc')
    }
  }
  const arrow = (s: Sort) => (sort === s ? (dir === 'asc' ? ' ↑' : ' ↓') : '')

  const replaceRow = (c: CommentSummary) => {
    setData((d) => (d ? { ...d, items: d.items.map((x) => (x.id === c.id ? c : x)) } : d))
    setSelected((s) => (s && s.id === c.id ? c : s))
  }

  const openComment = (c: CommentSummary, reply = false) => {
    setReplyFirst(reply)
    setSelected(c)
  }

  /** The reply queue: everything still waiting, longest wait (earliest visit) first. */
  const showQueue = () => {
    setStatus('waiting')
    setSort('visitDate')
    setDir('asc')
    setFrom('')
    setPage(1)
  }

  const doDelete = async () => {
    if (!deleting) return
    setBusy(true)
    try {
      await api.delete(`/comments/${deleting.id}`)
      setDeleting(null)
      setSelected(null)
      await load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const exportCsv = async () => {
    try {
      const responded = status === 'replied' ? 'true' : status ? 'false' : ''
      const blob = await api.blob(`/comments/export${qs({ restaurantId, from, to, responded })}`)
      downloadBlob(blob, `guest-comments-${new Date().toISOString().slice(0, 10)}.csv`)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.total / pageSize)) : 1
  const openCount = useMemo(() => data?.items.filter((c) => !c.responded).length ?? 0, [data])

  return (
    <>
      <div className="page-head">
        <h1>Guest comments</h1>
        <span className="grow" />
        {has('MarkResponded') && (
          <button type="button" className="btn" onClick={showQueue}>Reply queue</button>
        )}
        {has('ExportComments') && (
          <button type="button" className="btn" onClick={exportCsv}>
            ⬇ Download CSV
          </button>
        )}
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="filters">
        <Field label="Restaurant">
          <select value={restaurantId} onChange={(e) => { setRestaurantId(e.target.value ? Number(e.target.value) : ''); setPage(1) }}>
            <option value="">All my restaurants</option>
            {restaurants.map((r) => (
              <option key={r.id} value={r.id}>{r.name}</option>
            ))}
          </select>
        </Field>
        <Field label="Visits from">
          <input type="date" value={from} onChange={(e) => { setFrom(e.target.value); setPage(1) }} />
        </Field>
        <Field label="to">
          <input type="date" value={to} onChange={(e) => { setTo(e.target.value); setPage(1) }} />
        </Field>
        <Field label="Follow-up">
          <select value={status} onChange={(e) => { setStatus(e.target.value as Status); setPage(1) }}>
            <option value="">Any</option>
            <option value="waiting">Waiting for a reply</option>
            <option value="needs-reply">Not tried yet</option>
            <option value="attempted">Tried, not reached</option>
            <option value="replied">Replied</option>
          </select>
        </Field>
        <Field label="Reason">
          <select value={reason} onChange={(e) => { setReason(e.target.value); setPage(1) }}>
            <option value="">Any</option>
            {(settings?.reasonCategories ?? []).map((r) => (
              <option key={r} value={r}>{r}</option>
            ))}
          </select>
        </Field>
        <Field label="Search" className="wide">
          <input value={search} onChange={(e) => { setSearch(e.target.value); setPage(1) }} placeholder="Name, phone, address, details…" />
        </Field>
        <label className="check" style={{ paddingBottom: '0.6rem' }}>
          <input type="checkbox" checked={repeatOnly} onChange={(e) => { setRepeatOnly(e.target.checked); setPage(1) }} /> Repeat guests only
        </label>
      </div>

      {loading && !data ? (
        <Spinner />
      ) : !data || data.items.length === 0 ? (
        <Empty>No comments match these filters.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th className="sortable" onClick={() => toggleSort('visitDate')}>Visit{arrow('visitDate')}</th>
                <th>Day</th>
                {!restaurantId && restaurants.length > 1 && <th>Restaurant</th>}
                <th className="sortable" onClick={() => toggleSort('guest')}>Guest{arrow('guest')}</th>
                <th className="sortable" onClick={() => toggleSort('reason')}>Reason{arrow('reason')}</th>
                <th>Sent</th>
                <th className="sortable" onClick={() => toggleSort('lastActivity')}>Follow-up{arrow('lastActivity')}</th>
                <th className="sortable" onClick={() => toggleSort('createdAt')}>Entered{arrow('createdAt')}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((c) => (
                <tr key={c.id} className="clickable" onClick={() => openComment(c)}>
                  <td>{fmtDate(c.visitDate)}</td>
                  <td>{c.dayOfWeek.slice(0, 3)}</td>
                  {!restaurantId && restaurants.length > 1 && <td>{c.restaurantName}</td>}
                  <td>
                    {c.guestFirstName} {c.guestLastName}{' '}
                    {c.guestCommentCount > 1 && (
                      <Badge kind="warn" title="Number of cards on file for this guest across the group">
                        ×{c.guestCommentCount}
                      </Badge>
                    )}
                    <div className="muted" style={{ fontSize: '0.8rem' }}>{c.phone || c.streetAddress}</div>
                  </td>
                  <td>
                    {c.reason}
                    {c.source && <div className="small muted">via {c.source}</div>}
                    {c.reasonDetails && <div className="muted" style={{ fontSize: '0.8rem', maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{c.reasonDetails}</div>}
                  </td>
                  <td>{c.couponOffer || <span className="muted">—</span>}</td>
                  <td>
                    <FollowUpBadge c={c} />
                    {c.noteCount > 0 && (
                      <div className="small muted" title={c.latestNote ? `${c.latestNote.createdByName}: ${c.latestNote.text}` : undefined}>
                        💬 {c.noteCount} note{c.noteCount === 1 ? '' : 's'}
                      </div>
                    )}
                  </td>
                  <td>
                    <div>{c.createdByName}</div>
                    <div className="muted" style={{ fontSize: '0.8rem' }}>{fmtDate(c.createdAt)}</div>
                  </td>
                  <td onClick={(e) => e.stopPropagation()}>
                    <div className="row-actions">
                      {has('MarkResponded') && !c.responded && (
                        <button type="button" className="btn btn-sm btn-primary" onClick={() => openComment(c, true)}>Reply</button>
                      )}
                      {has('EditComments') && (
                        <button type="button" className="btn btn-sm" onClick={() => setEditing(c)}>Edit</button>
                      )}
                      {has('DeleteComments') && (
                        <button type="button" className="btn btn-sm btn-danger" onClick={() => setDeleting(c)}>Remove</button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="table-foot">
            <span>
              {data.total} comment{data.total === 1 ? '' : 's'} · {openCount} on this page waiting for a reply
            </span>
            <span className="grow" />
            <button type="button" className="btn btn-sm" disabled={page <= 1} onClick={() => setPage(page - 1)}>‹ Prev</button>
            <span>Page {page} of {totalPages}</span>
            <button type="button" className="btn btn-sm" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>Next ›</button>
          </div>
        </div>
      )}

      {selected && (
        <CommentDrawer
          comment={selected}
          onClose={() => setSelected(null)}
          onEdit={(c) => setEditing(c)}
          onChanged={replaceRow}
          onDelete={(c) => setDeleting(c)}
          startReply={replyFirst}
        />
      )}
      {editing && (
        <Modal title={`Edit card · ${editing.guestFirstName} ${editing.guestLastName}`} onClose={() => setEditing(null)} wide>
          <CardForm
            initial={cardFromSummary(editing)}
            editingId={editing.id}
            onCancel={() => setEditing(null)}
            onSaved={(c) => {
              replaceRow(c)
              setEditing(null)
              void load()
            }}
          />
        </Modal>
      )}
      {deleting && (
        <Confirm
          title="Remove this card?"
          danger
          busy={busy}
          confirmLabel="Remove"
          message={`Remove the card for ${deleting.guestFirstName} ${deleting.guestLastName} (${fmtDate(deleting.visitDate)}, ${deleting.reason})? It will disappear from lists and analytics; the audit log keeps a record.`}
          onCancel={() => setDeleting(null)}
          onConfirm={() => void doDelete()}
        />
      )}
    </>
  )
}
