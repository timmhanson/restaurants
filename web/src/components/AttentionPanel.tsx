import { useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { AttentionItem } from '../api/types'
import { Alert, fmtDateTime } from './ui'

const SEVERITY = { high: 'Needs action', medium: 'Worth a look', info: 'For your information' } as const

type Show = 'all' | 'system' | 'restaurants'
const SHOWS: { id: Show; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'system', label: 'System Only' },
  { id: 'restaurants', label: 'Restaurants Only' },
]
const STORED = 'gf:attention-show'

/** The choice of All / System Only / Restaurants Only is this browser's own convenience, so it is kept here and nowhere else. */
function remembered(): Show {
  try {
    const v = window.localStorage.getItem(STORED)
    return v === 'system' || v === 'restaurants' ? v : 'all'
  } catch {
    return 'all'
  }
}

/**
 * "Needs your attention" on the System Administrator's Overview. Each item can be dismissed; it comes back by
 * itself when it changes (a new count, a new detail) or goes away and returns. A switch narrows the list to the
 * site itself (backups, errors, keys, security) or to the investment groups (their restaurants, people, cards and
 * bills). Dismissed items can be shown and brought back. Dismissing is each System Administrator's own.
 */
export default function AttentionPanel({ items, onChange }: { items: AttentionItem[]; onChange: (items: AttentionItem[]) => void }) {
  const [show, setShow] = useState<Show>(remembered)
  const [dismissedOpen, setDismissedOpen] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const choose = (next: Show) => {
    setShow(next)
    try {
      window.localStorage.setItem(STORED, next)
    } catch {
      /* the choice just is not remembered */
    }
  }

  const act = async (item: AttentionItem, dismiss: boolean) => {
    setBusy(item.key)
    setError(null)
    try {
      if (dismiss) await api.post('/system/attention/dismiss', { key: item.key, fingerprint: item.fingerprint, title: item.title })
      else await api.post('/system/attention/restore', { key: item.key })
      onChange(items.map((a) => (a.key === item.key ? { ...a, dismissed: dismiss, dismissedAt: dismiss ? new Date().toISOString() : null } : a)))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const inScope = items.filter((a) => show === 'all' || a.scope === show)
  const current = inScope.filter((a) => !a.dismissed)
  const dismissed = inScope.filter((a) => a.dismissed)
  const count = (s: Show) => items.filter((a) => !a.dismissed && (s === 'all' || a.scope === s)).length
  const about = show === 'system' ? ' about the system' : show === 'restaurants' ? ' about investment groups and restaurants' : ''

  const row = (a: AttentionItem) => (
    <li key={a.key} className={`attention-${a.severity}${a.dismissed ? ' attention-dismissed' : ''}`}>
      <span className="attention-mark" title={SEVERITY[a.severity]} aria-label={SEVERITY[a.severity]} />
      <div className="attention-text">
        <strong>{a.title}</strong>
        <div className="muted small">{a.detail}</div>
        {a.dismissed && a.dismissedAt && <div className="muted small">Dismissed {fmtDateTime(a.dismissedAt)}. It comes back by itself if it changes.</div>}
      </div>
      {a.link && <Link className="btn btn-sm" to={a.link}>Open</Link>}
      {a.dismissed ? (
        <button type="button" className="btn btn-sm" disabled={busy !== null} onClick={() => void act(a, false)} aria-label={`Bring back: ${a.title}`}>Bring back</button>
      ) : (
        <button type="button" className="btn btn-sm" disabled={busy !== null} onClick={() => void act(a, true)} aria-label={`Dismiss: ${a.title}`}
          title="Hide this until it changes">
          Dismiss
        </button>
      )}
    </li>
  )

  return (
    <div className="panel">
      <div className="page-head" style={{ marginBottom: '0.6rem' }}>
        <h2 style={{ margin: 0 }}>Needs your attention</h2>
        <span className="grow" />
        <div className="toggle" role="group" aria-label="Which items to show">
          {SHOWS.map((s) => (
            <button key={s.id} type="button" className={show === s.id ? 'on' : ''} aria-pressed={show === s.id} onClick={() => choose(s.id)}>
              {s.label}{count(s.id) > 0 ? ` (${count(s.id)})` : ''}
            </button>
          ))}
        </div>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {current.length === 0 ? (
        <p className="muted" style={{ margin: 0 }}>
          {items.length === 0
            ? 'Nothing right now. Every group has an administrator, nothing is failing and no cards are overdue.'
            : dismissed.length > 0 ? `Nothing new${about}. You have dismissed the rest.` : `Nothing${about} right now.`}
        </p>
      ) : (
        <ul className="attention">{current.map(row)}</ul>
      )}
      {dismissed.length > 0 && (
        <div style={{ marginTop: '0.75rem' }}>
          <button type="button" className="btn-link" onClick={() => setDismissedOpen((o) => !o)} aria-expanded={dismissedOpen}>
            {dismissedOpen ? 'Hide dismissed' : `Show dismissed (${dismissed.length})`}
          </button>
          {dismissedOpen && <ul className="attention" style={{ marginTop: '0.5rem' }}>{dismissed.map(row)}</ul>}
        </div>
      )}
    </div>
  )
}
