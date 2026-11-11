import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { LoginResponse } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Field } from '../components/ui'

interface CodeRequestResponse { emailMasked: string; expiresAt: string; resendAfterSeconds?: number }

const COOLDOWN_SECONDS = 60

/**
 * Two-step password change: (1) email a 6-digit code to the address on file,
 * (2) submit the code with the current and new password.
 * Used both for the regular "Change my password" page and the forced change after an admin reset.
 */
export default function ChangePasswordPage({ forced = false }: { forced?: boolean }) {
  const { applyLogin, logout, user } = useAuth()
  const nav = useNavigate()
  const [sent, setSent] = useState<CodeRequestResponse | null>(null)
  const [cooldown, setCooldown] = useState(0)
  const [code, setCode] = useState('')
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (cooldown <= 0) return
    const t = window.setTimeout(() => setCooldown((c) => c - 1), 1000)
    return () => window.clearTimeout(t)
  }, [cooldown])

  const requestCode = async () => {
    setBusy(true)
    setError(null)
    setDone(false)
    try {
      const res = await api.post<CodeRequestResponse>('/auth/change-password/request')
      setSent(res)
      setCooldown(res.resendAfterSeconds ?? COOLDOWN_SECONDS)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (!/^\d{6}$/.test(code)) return setError('Enter the 6-digit code from the email.')
    if (next.length < 8) return setError('The new password must be at least 8 characters.')
    if (next !== confirm) return setError('The new passwords do not match.')
    setBusy(true)
    try {
      const res = await api.post<LoginResponse>('/auth/change-password', { currentPassword: current, newPassword: next, code })
      applyLogin(res)
      setDone(true)
      setSent(null)
      setCode('')
      setCurrent('')
      setNext('')
      setConfirm('')
      if (forced) nav('/', { replace: true })
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const body = (
    <div className="grid" style={{ maxWidth: 460 }}>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {done && <Alert kind="success">Your password has been changed.</Alert>}

      <div className="panel" style={{ marginBottom: 0 }}>
        <h3>Step 1 · Email me a code</h3>
        {sent ? (
          <p className="muted" style={{ margin: 0 }}>
            We sent a code to <strong>{sent.emailMasked}</strong>. It expires in 10 minutes.{' '}
            {cooldown > 0 ? (
              <span>Resend available in {cooldown}s.</span>
            ) : (
              <button type="button" className="btn-link" onClick={() => void requestCode()} disabled={busy}>
                Resend code
              </button>
            )}
          </p>
        ) : (
          <>
            <p className="muted">A 6-digit code will be sent to the email address on your account{user?.email ? ` (${user.email})` : ''}.</p>
            <button type="button" className="btn btn-primary" onClick={() => void requestCode()} disabled={busy}>
              {busy ? 'Sending…' : 'Email me a code'}
            </button>
          </>
        )}
      </div>

      <form onSubmit={submit} className="panel grid" style={{ marginBottom: 0, opacity: sent ? 1 : 0.6 }}>
        <h3 style={{ margin: 0 }}>Step 2 · Set the new password</h3>
        <Field label="6-digit code">
          <input value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, '').slice(0, 6))} inputMode="numeric" pattern="\d{6}" maxLength={6} autoComplete="one-time-code" placeholder="123456" required disabled={!sent} />
        </Field>
        <Field label="Current password">
          <input type="password" value={current} onChange={(e) => setCurrent(e.target.value)} autoComplete="current-password" required disabled={!sent} />
        </Field>
        <Field label="New password" hint="At least 8 characters.">
          <input type="password" value={next} onChange={(e) => setNext(e.target.value)} autoComplete="new-password" required minLength={8} disabled={!sent} />
        </Field>
        <Field label="Confirm new password">
          <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" required disabled={!sent} />
        </Field>
        <div className="actions" style={{ justifyContent: 'flex-start', marginTop: 0 }}>
          <button className="btn btn-primary" type="submit" disabled={busy || !sent}>
            {busy ? 'Saving…' : 'Change password'}
          </button>
          {forced && (
            <button className="btn" type="button" onClick={logout}>
              Sign out
            </button>
          )}
        </div>
      </form>
    </div>
  )

  if (forced) {
    return (
      <div className="login-wrap">
        <div className="login-card" style={{ maxWidth: 520 }}>
          <h1>Choose a new password</h1>
          <p className="muted">Hi {user?.displayName}. Your password was set by an administrator, so you need to pick a new one before continuing.</p>
          {body}
        </div>
      </div>
    )
  }
  return (
    <>
      <div className="page-head">
        <h1>Change my password</h1>
      </div>
      {body}
    </>
  )
}
