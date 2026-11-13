import { useCallback, useEffect, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { api } from '../api/client'
import type { FollowUpRestaurant, FollowUpSummary } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Modal, fmtDate } from './ui'

/** Tells the notice to recount, e.g. right after a follow-up is recorded. */
export const followUpsChanged = () => window.dispatchEvent(new Event('gf:follow-ups'))

/**
 * Shown at the top of every page to managers and owners when guests are due a follow-up: their coupon card was
 * sent the restaurant's number of days ago (5 unless changed) and nobody has checked that it arrived.
 */
export default function FollowUpNotice() {
  const { has, isTeamMember, overseeing } = useAuth()
  const location = useLocation()
  const [summary, setSummary] = useState<FollowUpSummary | null>(null)
  const [timing, setTiming] = useState(false)
  const applies = !isTeamMember && !overseeing && has('ViewComments') && has('MarkResponded')

  const load = useCallback(() => {
    if (!applies) return
    api.get<FollowUpSummary>('/follow-ups/summary').then(setSummary).catch(() => undefined)
  }, [applies])

  // Recount on every page change and whenever a follow-up is recorded.
  useEffect(load, [load, location.pathname])
  useEffect(() => {
    window.addEventListener('gf:follow-ups', load)
    return () => window.removeEventListener('gf:follow-ups', load)
  }, [load])

  if (!applies || !summary) return null
  const days = [...new Set(summary.restaurants.map((r) => r.effectiveDays).filter((d) => d > 0))]
  return (
    <>
      {summary.due > 0 && (
        <div className="follow-up-notice" role="status">
          <span>
            <strong>
              {summary.due} guest{summary.due === 1 ? ' is' : 's are'} due a follow-up.
            </strong>{' '}
            {summary.due === 1 ? 'Their coupon card was' : 'Their coupon cards were'} sent {days.length === 1 ? `${days[0]} or more days ago` : 'a while ago'} and nobody
            has checked {summary.due === 1 ? 'it' : 'they'} arrived{summary.oldestDueOn ? ` (oldest due ${fmtDate(summary.oldestDueOn)})` : ''}.
          </span>
          <span className="row-actions">
            <Link className="btn btn-sm btn-primary" to="/comments?status=follow-up-due">Review and call</Link>
            {summary.restaurants.some((r) => r.canSet) && (
              <button type="button" className="btn btn-sm" onClick={() => setTiming(true)}>Change timing</button>
            )}
          </span>
        </div>
      )}
      {timing && <FollowUpTiming summary={summary} onClose={() => setTiming(false)} onSaved={load} />}
    </>
  )
}

/** How many days after a card is sent each restaurant follows up. Owners set it; managers only with the permission. */
export function FollowUpTiming({ summary, onClose, onSaved }: { summary: FollowUpSummary; onClose: () => void; onSaved: () => void }) {
  const [rows, setRows] = useState<FollowUpRestaurant[]>(summary.restaurants)
  const [draft, setDraft] = useState<Record<number, string>>(Object.fromEntries(summary.restaurants.map((r) => [r.id, r.days == null ? '' : String(r.days)])))
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const save = async (r: FollowUpRestaurant) => {
    const text = (draft[r.id] ?? '').trim()
    const days = text === '' ? null : Number(text)
    if (days !== null && (!Number.isInteger(days) || days < 0 || days > 90)) return setError('Enter a whole number of days from 0 to 90, or leave it empty to use the default.')
    setBusy(true)
    setError(null)
    setSaved(null)
    try {
      const updated = await api.put<FollowUpRestaurant>(`/follow-ups/restaurants/${r.id}`, { days })
      setRows((list) => list.map((x) => (x.id === r.id ? updated : x)))
      setSaved(updated.effectiveDays === 0 ? `Follow-up reminders are off at ${r.name}.` : `${r.name}: follow up ${updated.effectiveDays} day${updated.effectiveDays === 1 ? '' : 's'} after a card is sent.`)
      onSaved()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Follow-up timing" onClose={onClose}>
      <p className="muted">
        After a coupon card is sent, the guest is due a follow-up to check it arrived. Choose how many days to wait at each restaurant. Leave it empty
        to use the usual {summary.defaultDays} days; 0 turns the reminders off.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {saved && <Alert kind="success" onClose={() => setSaved(null)}>{saved}</Alert>}
      <table className="compact">
        <tbody>
          {rows.map((r) => (
            <tr key={r.id}>
              <td>
                <strong>{r.name}</strong>
                <div className="muted small">{r.effectiveDays === 0 ? 'Reminders off' : `Now: ${r.effectiveDays} day${r.effectiveDays === 1 ? '' : 's'}${r.days == null ? ' (the default)' : ''}`}</div>
              </td>
              <td style={{ width: '11rem' }}>
                {r.canSet ? (
                  <div style={{ display: 'flex', gap: '0.4rem', alignItems: 'center' }}>
                    <input
                      type="number"
                      min={0}
                      max={90}
                      aria-label={`Days before following up at ${r.name}`}
                      placeholder={String(summary.defaultDays)}
                      value={draft[r.id] ?? ''}
                      onChange={(e) => setDraft((d) => ({ ...d, [r.id]: e.target.value }))}
                    />
                    <button type="button" className="btn btn-sm" disabled={busy} onClick={() => void save(r)}>Save</button>
                  </div>
                ) : (
                  <span className="muted small">Set by this restaurant's owners</span>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="actions">
        <button type="button" className="btn" onClick={onClose}>Done</button>
      </div>
    </Modal>
  )
}

/** Opens the timing dialog from anywhere; hidden from people who may not change any restaurant's timing. */
export function FollowUpTimingButton() {
  const [summary, setSummary] = useState<FollowUpSummary | null>(null)
  const [open, setOpen] = useState(false)
  const load = useCallback(() => {
    api.get<FollowUpSummary>('/follow-ups/summary').then(setSummary).catch(() => undefined)
  }, [])
  useEffect(load, [load])
  if (!summary?.restaurants.some((r) => r.canSet)) return null
  return (
    <>
      <button type="button" className="btn" onClick={() => setOpen(true)}>Follow-up timing</button>
      {open && (
        <FollowUpTiming
          summary={summary}
          onClose={() => {
            setOpen(false)
            load()
          }}
          onSaved={followUpsChanged}
        />
      )}
    </>
  )
}
