import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { ServiceTestResult, StripeAccount } from '../api/types'
import SiteKeyBlock from './SiteKeyBlock'
import { Alert, Badge, Spinner } from './ui'

/**
 * Stripe on Connected Systems: the site's own Stripe account, which takes payment for the monthly mailing bills.
 * Investment groups save a bank account or card on Stripe's page; nothing they type there reaches this site.
 */
export default function StripePanel({ onChanged }: { onChanged?: () => void }) {
  const [account, setAccount] = useState<StripeAccount | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<StripeAccount>('/system/stripe').then(setAccount).catch((e) => setError((e as Error).message))
  }, [])

  return (
    <div className="panel">
      <h2>Stripe (payments for mailing bills)</h2>
      <p className="muted">
        With a Stripe account, investment groups pay their monthly mailing bills on the site, from a bank account or a card, and the money goes to your
        Stripe account. Create the account at stripe.com, then copy the <strong>secret key</strong> from Developers, API keys. Fees, bill dates and the
        credit card and bank account fees are set on <Link to="/system-billing">Billing</Link>.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!account ? (
        !error && <Spinner />
      ) : (
        <>
          <SiteKeyBlock
            info={account}
            label="Stripe secret key"
            hint="Begins sk_test_ or sk_live_. Start with the test key to try payments with Stripe’s test cards. Stored encrypted and never shown again."
            minLength={20}
            testNote="Test mode: payments use Stripe’s test cards and bank accounts, and no money moves."
            liveNote="Live: payments move real money into this Stripe account."
            onSave={async (key) => { setAccount(await api.put<StripeAccount>('/system/stripe', { secretKey: key })); onChanged?.() }}
            onTest={() => api.post<ServiceTestResult>('/system/stripe/test')}
            onRemove={async () => { setAccount(await api.delete<StripeAccount>('/system/stripe')); onChanged?.() }}
            removeTitle="Remove the Stripe key?"
            removeMessage="Groups will no longer be able to pay on the site, and automatic payments stop. Bills can still be marked as paid by hand. Nothing is changed at Stripe."
            onError={setError}
          />
          <p style={{ marginBottom: 0 }}>
            {account.groupsWithMethod > 0
              ? <Badge kind="ok">{account.groupsWithMethod} group{account.groupsWithMethod === 1 ? '' : 's'} with a payment method</Badge>
              : <Badge kind="neutral">no group has saved a payment method yet</Badge>}{' '}
            <span className="muted small">Replacing the key with one from a different Stripe account or mode clears the payment methods groups have saved, because they belong to the old one.</span>
          </p>
        </>
      )}
    </div>
  )
}
