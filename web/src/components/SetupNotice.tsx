import { useCallback, useEffect, useState } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { Onboarding, SystemOnboarding } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Field, Spinner, fmtDateTime } from './ui'

/** Tells the menu and the reminder that set-up has moved on (a step confirmed, left for later, finished). */
export const onboardingChanged = () => window.dispatchEvent(new Event('gf:onboarding'))

const SENT = 'gf:welcomed'

/**
 * Where the investment group's set-up stands (for its administrators), and whether this person has had their own
 * welcome. At their first visit, a new group's administrator is taken to Getting Started, and an owner or manager
 * (or an administrator of a group already set up) to the tour of what they can do: once per browser session, and
 * never again once they have seen it. The shared team login gets neither, and a System Administrator viewing a
 * group is never sent there, though they can open it. Returns the set-up state for administrators only.
 */
export function useOnboarding() {
  const { isGroupWide, isTeamMember, overseeing, viewingAs, user } = useAuth()
  const nav = useNavigate()
  const location = useLocation()
  const [state, setState] = useState<Onboarding | null>(null)
  const applies = Boolean(user) && !overseeing && !isTeamMember

  const load = useCallback(() => {
    if (!applies) return setState(null)
    api.get<Onboarding>('/onboarding').then(setState).catch(() => setState(null))
  }, [applies])
  useEffect(load, [load, user?.id])
  useEffect(() => {
    window.addEventListener('gf:onboarding', load)
    return () => window.removeEventListener('gf:onboarding', load)
  }, [load])

  useEffect(() => {
    const due = state && ((state.canRun && state.status === 'new') || state.tour)
    if (!due || viewingAs || location.pathname !== '/enter') return
    try {
      if (window.sessionStorage.getItem(SENT)) return
      window.sessionStorage.setItem(SENT, '1')
    } catch {
      return // without session storage there is no telling whether they have been sent already, so they are not sent
    }
    nav('/welcome', { replace: true })
  }, [state, viewingAs, location.pathname, nav])

  return applies && isGroupWide && state?.canRun ? state : null
}

/** Shown at the top of every page until set-up is finished: how far it has got, and the way back to it. */
export default function SetupNotice({ state }: { state: Onboarding | null }) {
  const location = useLocation()
  if (!state || state.status === 'done' || location.pathname === '/welcome') return null
  const done = state.steps.filter((s) => s.confirmed).length
  return (
    <div className="follow-up-notice setup-notice" role="status">
      <span>
        <strong>{state.minimumMet ? 'Finish setting up.' : 'Add your first restaurant to get going.'}</strong>{' '}
        {state.status === 'new' ? 'A few short steps check what has been set up for you.' : `${done} of ${state.steps.length} steps confirmed.`}
      </span>
      <span className="row-actions">
        <Link className="btn btn-sm btn-primary" to="/welcome">{state.status === 'new' ? 'Get started' : 'Continue'}</Link>
      </span>
    </div>
  )
}

/**
 * In the System Administrator's Edit group dialog: a welcome note the group's administrator reads on their first
 * screen, where the group's set-up stands and which steps they have confirmed. Saves as it is changed.
 */
export function OnboardingEditor({ groupId }: { groupId: number }) {
  const [o, setO] = useState<SystemOnboarding | null>(null)
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get<SystemOnboarding>(`/system/groups/${groupId}/onboarding`).then((d) => { setO(d); setNote(d.welcomeNote ?? '') }).catch((e) => setError((e as Error).message))
  }, [groupId])

  const save = async (change: Record<string, unknown>) => {
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      const next = await api.put<SystemOnboarding>(`/system/groups/${groupId}/onboarding`, change)
      setO(next)
      setNote(next.welcomeNote ?? '')
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (!o) return error ? <Alert kind="error">{error}</Alert> : <Spinner />
  return (
    <div>
      <p className="muted">
        At their first sign-in, the group’s administrator is welcomed and walked through a few steps that show what you have set up, for them to confirm or change.
        They can look around first and come back to it. This part saves as you change it.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <p style={{ margin: '0 0 0.5rem' }}>
        {o.status === 'done' ? <><Badge kind="ok">set up</Badge> {o.completedBy === 'Already in use' ? 'This group was already in use when Getting Started was added.' : `Finished ${o.completedAt ? fmtDateTime(o.completedAt) : ''}${o.completedBy ? ` by ${o.completedBy}` : ''}.`}</>
          : o.status === 'new' ? <><Badge kind="neutral">not started</Badge> Their administrator has not opened the welcome page yet.</>
            : <><Badge kind="warn">started</Badge> {o.startedAt ? `Began ${fmtDateTime(o.startedAt)}. ` : ''}{o.laterAt ? 'They chose to finish later.' : ''}</>}
      </p>
      {o.steps.some((s) => s.confirmed) && (
        <p className="muted small" style={{ margin: '0 0 0.5rem' }}>
          Confirmed: {o.steps.filter((s) => s.confirmed).map((s) => `${s.title}${s.confirmedBy ? ` (${s.confirmedBy})` : ''}`).join(', ')}.
          {o.steps.some((s) => !s.confirmed) ? ` Not yet: ${o.steps.filter((s) => !s.confirmed).map((s) => s.title).join(', ')}.` : ''}
        </p>
      )}
      <Field label="Welcome note (optional)" hint="Shown on their first screen, with your name under it. Saves when you leave the box.">
        <textarea rows={3} value={note} maxLength={2000} disabled={busy} onChange={(e) => setNote(e.target.value)} onBlur={() => { if (note.trim() !== (o.welcomeNote ?? '')) void save({ welcomeNote: note }) }}
          placeholder="e.g. Good talking with you on Tuesday. I have added your three restaurants and switched on mailing in test mode so you can try it. Check the addresses, then add your managers." />
      </Field>
      <div className="row-actions" style={{ marginTop: '0.5rem' }}>
        {o.status !== 'done' && <button type="button" className="btn btn-sm" disabled={busy} onClick={() => void save({ markDone: true })} title="For a group you set up completely yourself">Mark as set up</button>}
        {o.status !== 'new' && <button type="button" className="btn btn-sm" disabled={busy} onClick={() => void save({ restart: true })} title="Their administrator is welcomed again; nothing they set up is changed">Welcome them again</button>}
        {saved && <span className="muted small">Saved.</span>}
      </div>
    </div>
  )
}
