import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { ServiceTestResult, StripeAccount } from '../api/types'
import SiteKeyBlock from './SiteKeyBlock'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/** What the site's restricted key must be allowed to do in Stripe, and nothing more. */
const PERMISSIONS: [string, string, string][] = [
  ['Customers', 'Write', 'one customer per investment group'],
  ['Checkout Sessions', 'Write', 'the page where a group saves its bank account or card'],
  ['SetupIntents', 'Read', 'reading what was saved on that page'],
  ['PaymentMethods', 'Write', 'removing a saved bank account or card'],
  ['PaymentIntents', 'Write', 'charging a bill and checking how a payment ended'],
]

/**
 * Stripe on Connected Systems: the site's own Stripe account, which takes payment for the monthly mailing bills.
 * Investment groups save a bank account or card on Stripe's page; nothing they type there reaches this site. Two
 * things are entered here: a restricted API key (so the site can ask Stripe to do things) and the signing secret of
 * a webhook (so Stripe can tell the site when a bank payment arrives).
 */
export default function StripePanel({ onChanged }: { onChanged?: () => void }) {
  const [account, setAccount] = useState<StripeAccount | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [secret, setSecret] = useState('')
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    api.get<StripeAccount>('/system/stripe').then(setAccount).catch((e) => setError((e as Error).message))
  }, [])

  const webhookUrl = `${window.location.origin}/webhooks/stripe`

  const saveSecret = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      setAccount(await api.put<StripeAccount>('/system/stripe/webhook', { secret }))
      setSecret('')
      setEntering(false)
      setNotice('Signing secret saved. Send a test notification from Stripe’s webhook page; “last notification” below shows when one arrives.')
      onChanged?.()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const removeSecret = async () => {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      setAccount(await api.delete<StripeAccount>('/system/stripe/webhook'))
      onChanged?.()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
      setRemoving(false)
    }
  }

  return (
    <div className="panel">
      <h2>Stripe (payments for mailing bills)</h2>
      <p className="muted">
        With a Stripe account, investment groups pay their monthly mailing bills on the site, from a bank account or a card, and the money goes to your
        Stripe account. Fees, bill dates and the credit card and bank account fees are set on <Link to="/system-billing">Billing</Link>.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {!account ? (
        !error && <Spinner />
      ) : (
        <>
          <h3 style={{ marginTop: 0 }}>1. API key</h3>
          <p className="muted">
            In Stripe, open Developers, API keys and create a <strong>restricted key</strong>. A restricted key can only do what you allow, so if it ever
            leaked it could do far less than your secret key. Give it these permissions and leave everything else on “None”:
          </p>
          <div className="table-wrap" style={{ maxWidth: 640 }}>
            <table>
              <thead><tr><th>Permission</th><th>Access</th><th>Used for</th></tr></thead>
              <tbody>
                {PERMISSIONS.map(([name, access, why]) => (
                  <tr key={name}><td><strong>{name}</strong></td><td>{access}</td><td className="muted">{why}</td></tr>
                ))}
              </tbody>
            </table>
          </div>
          <SiteKeyBlock
            info={account}
            label="Stripe restricted key"
            hint="Begins rk_test_ or rk_live_. Start with a test key to try payments with Stripe’s test cards. Stored encrypted and never shown again."
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
          {account.configured && !account.restricted && (
            <Alert kind="warn">
              This is a full secret key (sk_…), which can do anything in your Stripe account. It works, but Stripe recommends a restricted key with only the
              permissions above. Replace it when convenient.
            </Alert>
          )}
          <p className="muted small">
            If a later step says the key “is not allowed to do this”, add the permission it names to the key in Stripe. Requests use Stripe API version{' '}
            <code>{account.apiVersion || 'your account’s default'}</code>. Replacing the key with one from a different Stripe account or mode clears the
            payment methods groups have saved, because they belong to the old one.
          </p>

          <h3>2. Notifications from Stripe (webhook)</h3>
          <p className="muted">
            A payment from a bank account takes a few days. Stripe tells the site the moment it arrives or fails by calling the address below. In Stripe, open
            Developers, Webhooks, add a destination with this address, choose the events <code>payment_intent.succeeded</code>,{' '}
            <code>payment_intent.payment_failed</code> and <code>payment_intent.canceled</code>, then copy its <strong>signing secret</strong> here. Use the
            webhook from the same mode (test or live) as the key above.
          </p>
          <dl className="kv">
            <dt>Address for Stripe</dt>
            <dd><code>{webhookUrl}</code></dd>
            <dt>Signing secret</dt>
            <dd>
              {account.webhookConfigured
                ? <><Badge kind="ok">saved</Badge> ending in <code>{account.webhookEnding}</code></>
                : <><Badge kind="warn">not set up</Badge> Bank payments are only noticed by the hourly check, up to an hour late.</>}
            </dd>
            {account.webhookConfigured && (
              <>
                <dt>Last notification</dt>
                <dd>{account.lastNotificationAt ? fmtDateTime(account.lastNotificationAt) : 'None since the site last restarted.'}</dd>
              </>
            )}
          </dl>
          {entering || !account.webhookConfigured ? (
            <form onSubmit={saveSecret} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
              <Field label="Webhook signing secret" hint="Begins whsec_. Stored encrypted and never shown again. Every notification is checked against it; anything else is refused.">
                <input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="off" spellCheck={false} required minLength={20} />
              </Field>
              <div className="row-actions">
                {entering && <button type="button" className="btn" onClick={() => { setEntering(false); setSecret('') }} disabled={busy}>Cancel</button>}
                <button type="submit" className="btn btn-primary" disabled={busy || secret.trim().length < 20}>{busy ? 'Saving…' : 'Save signing secret'}</button>
              </div>
            </form>
          ) : (
            <div className="row-actions" style={{ marginTop: '0.75rem' }}>
              <button type="button" className="btn" onClick={() => setEntering(true)} disabled={busy}>Replace signing secret</button>
              <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy}>Remove signing secret</button>
            </div>
          )}

          <p style={{ marginBottom: 0, marginTop: '1rem' }}>
            {account.groupsWithMethod > 0
              ? <Badge kind="ok">{account.groupsWithMethod} group{account.groupsWithMethod === 1 ? '' : 's'} with a payment method</Badge>
              : <Badge kind="neutral">no group has saved a payment method yet</Badge>}
          </p>
        </>
      )}
      {removing && (
        <Confirm
          title="Remove the webhook signing secret?"
          message="Stripe’s notifications will be refused. Bank payments are still noticed by the hourly check, up to an hour late. Nothing is changed at Stripe."
          confirmLabel="Remove"
          danger
          busy={busy}
          onConfirm={() => void removeSecret()}
          onCancel={() => setRemoving(false)}
        />
      )}
    </div>
  )
}
