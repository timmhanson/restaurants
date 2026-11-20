import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { PermissionRules } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import PermissionRulesEditor, { type PermissionChange } from '../components/PermissionRulesEditor'
import { Alert, Spinner } from '../components/ui'

/**
 * The investment group's administrators decide what its owners and managers may do, within what the people who
 * run the site have left to them. Each change is saved as it is made.
 */
export default function PermissionsPage() {
  const { groupId, refreshContext } = useAuth()
  const [rules, setRules] = useState<PermissionRules | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved'>('idle')

  useEffect(() => {
    setRules(null)
    api.get<PermissionRules>('/permission-rules').then(setRules).catch((e) => setError((e as Error).message))
  }, [groupId])

  const change = async (c: PermissionChange) => {
    setStatus('saving')
    setError(null)
    try {
      setRules(await api.put<PermissionRules>('/permission-rules', { rows: [c] }))
      setStatus('saved')
      void refreshContext()
    } catch (e) {
      setError(`Not saved: ${(e as Error).message}`)
      setStatus('idle')
    }
  }

  return (
    <>
      <div className="page-head"><h1>Permissions</h1></div>
      <p className="muted">
        Who may do what in your investment group, handed down level by level. Investment Group Administrators can do everything. Here you decide what
        Restaurant Owners may do and what Managers may do: for everyone at once, or, for managers, one manager at a time. Nobody can be given more than
        the level above holds, so a permission owners do not hold is one no manager holds either. Team logins only enter cards.
      </p>
      <p className="muted">
        “Set for each manager” permissions are ticked on <Link to="/users">All Staff</Link> (Edit a manager) by you, and by owners when
        “Choose what each manager may do” is allowed for owners below.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!rules ? (error ? null : <Spinner />) : (
        <>
          <p className="muted small" role="status" style={{ marginTop: 0 }}>
            Changes are saved as you make them. {status === 'saving' ? 'Saving…' : status === 'saved' ? <strong>Saved.</strong> : ''}
          </p>
          <PermissionRulesEditor value={rules} onChange={(c) => void change(c)} disabled={status === 'saving'} />
        </>
      )}
    </>
  )
}
