import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { SystemStatus } from '../api/types'
import UspsAccountPanel from '../components/UspsAccountPanel'
import { Alert, Badge } from '../components/ui'

/**
 * The outside services the site connects to, for the System Administrator: where the accounts for them are
 * entered (USPS) and, for the ones managed elsewhere, where that is.
 */
export default function ConnectedSystemsPage() {
  const [status, setStatus] = useState<SystemStatus | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api.get<SystemStatus>('/system/status').then(setStatus).catch((e) => setError((e as Error).message))
  }, [])
  useEffect(load, [load])

  return (
    <>
      <div className="page-head">
        <h1>Connected Systems</h1>
      </div>
      <p className="muted">The outside services this site uses, and the account each one runs on.</p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}

      <UspsAccountPanel onChanged={load} />

      <div className="panel">
        <h2>EasyPost (postage)</h2>
        <p className="muted">
          Buys USPS postage when coupon cards are printed. Each investment group adds its own EasyPost account in its own Settings, so each pays for its
          own postage. To add or change one for a group, open the group with <strong>View as admin</strong> on <Link to="/groups">Investment groups</Link>,
          then go to Settings.
        </p>
        {status && (
          <p style={{ margin: 0 }}>
            {status.postageGroupAccounts > 0 ? (
              <Badge kind="ok">{status.postageGroupAccounts} group account{status.postageGroupAccounts === 1 ? '' : 's'}</Badge>
            ) : (
              <Badge kind="neutral">no group accounts yet</Badge>
            )}{' '}
            {status.postageConfigured ? 'The site also has a shared account, used for groups without their own.' : 'There is no shared site account, so groups without one add stamps by hand.'}
          </p>
        )}
      </div>

      <div className="panel">
        <h2>Email</h2>
        <p className="muted">Sends the 6-digit codes people need to change their own password. It is set up in Azure, not here.</p>
        {status && (
          <p style={{ margin: 0 }}>
            {status.emailConfigured ? <Badge kind="ok">working service</Badge> : <Badge kind="danger">not set up</Badge>} {status.email}.{' '}
            <Link to="/system">Send a test email from System status</Link>
          </p>
        )}
      </div>
    </>
  )
}
