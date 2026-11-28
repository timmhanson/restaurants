import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { SystemStatus } from '../api/types'
import AddressVerificationPanel from '../components/AddressVerificationPanel'
import PostGridSitePanel from '../components/PostGridSitePanel'
import StripePanel from '../components/StripePanel'
import { Alert, Badge } from '../components/ui'

/**
 * The outside services the site connects to, for the System Administrator, grouped by what they are for: mailing
 * and postage (PostGrid, EasyPost), payment (Stripe), address verification (one service chosen from Geocodio,
 * USPS and PostGrid) and email. The accounts are entered here or, for the ones managed elsewhere, it says where.
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

      <h2 className="section-head">Mailing and Postage Services</h2>
      <PostGridSitePanel onChanged={load} />
      <div className="panel">
        <h2>EasyPost (postage)</h2>
        <p className="muted">
          Buys USPS postage when coupon cards are printed. Each investment group adds its own EasyPost account in its own Settings, so each pays for its
          own postage. To add or change one for a group, open the group with <strong>Impersonate</strong> on <Link to="/groups">Investment groups</Link>,
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

      <h2 className="section-head">Payment</h2>
      <StripePanel onChanged={load} />

      <h2 className="section-head">Address Verification</h2>
      <AddressVerificationPanel onChanged={load} />

      <h2 className="section-head">Email</h2>
      <div className="panel">
        <h2>Email service</h2>
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
