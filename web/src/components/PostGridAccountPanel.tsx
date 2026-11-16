import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { PostageAccount, ServiceTestResult } from '../api/types'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/**
 * The investment group's own PostGrid account. PostGrid prints both sides of each coupon card and mails it, and bills
 * this group directly. The key is checked with PostGrid and typed in once; after that only its last four characters
 * are shown, and replacing it means typing a new one.
 */
export default function PostGridAccountPanel() {
  const [account, setAccount] = useState<PostageAccount | null>(null)
  const [key, setKey] = useState('')
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [test, setTest] = useState<ServiceTestResult | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)

  useEffect(() => {
    api.get<PostageAccount>('/settings/postgrid-account').then(setAccount).catch((e) => setError((e as Error).message))
  }, [])

  const runTest = async () => {
    setBusy('test')
    setTest(null)
    setError(null)
    try {
      setTest(await api.post<ServiceTestResult>('/settings/postgrid-account/test'))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy('save')
    setError(null)
    setTest(null)
    try {
      const saved = await api.put<PostageAccount>('/settings/postgrid-account', { apiKey: key })
      setAccount(saved)
      setKey('')
      setEntering(false)
      setTest({
        ok: true,
        message: saved.looksLikeTestKey
          ? 'Saved. PostGrid accepted the test key: cards sent with it are not printed, mailed or charged, and PostGrid shows a preview of each. Switch to the live key when you are ready to mail.'
          : 'Saved. PostGrid accepted the live key: cards sent with it are printed, mailed and charged to this PostGrid account.',
      })
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const remove = async () => {
    setBusy('remove')
    setError(null)
    setTest(null)
    try {
      setAccount(await api.delete<PostageAccount>('/settings/postgrid-account'))
      setTest({ ok: true, message: 'The account was removed from this site. Cards already with PostGrid still go out; nothing was changed at PostGrid.' })
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(null)
      setRemoving(false)
    }
  }

  return (
    <div className="panel">
      <h2>PostGrid account (printed and mailed for you)</h2>
      <p className="muted">
        With a PostGrid account, a manager can send coupon cards to PostGrid from Print cards: PostGrid prints both sides on card stock, adds the postage and
        mails them, usually the next business day. About $0.91 a card on PostGrid’s free Starter plan, less on its paid plans, billed by PostGrid to this
        investment group. Create the account at postgrid.com, then copy the Print &amp; Mail API key from Settings, API Keys.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {test && <Alert kind={test.ok ? 'success' : 'error'} onClose={() => setTest(null)}>{test.message}</Alert>}
      {!account ? (
        !error && <Spinner />
      ) : (
        <>
          {account.hasOwnAccount ? (
            <dl className="kv">
              <dt>Account</dt>
              <dd>
                <Badge kind="ok">connected</Badge> key ending in <code>{account.keyEnding}</code>{' '}
                {account.looksLikeTestKey && <Badge kind="warn" title="Nothing is printed, mailed or charged">test key</Badge>}
              </dd>
              <dt>Added</dt>
              <dd>{account.addedAt ? fmtDateTime(account.addedAt) : '—'}{account.addedBy ? ` by ${account.addedBy}` : ''}</dd>
            </dl>
          ) : (
            <p><Badge kind="neutral">no account yet</Badge> Cards are printed at the restaurant until one is added.</p>
          )}

          {entering || !account.hasOwnAccount ? (
            <form onSubmit={save} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
              <Field
                label={account.hasOwnAccount ? 'New PostGrid API key' : 'PostGrid Print & Mail API key'}
                hint="Start with the test key (it begins test_) to try it at no cost. The key is checked with PostGrid, stored encrypted and never shown again."
              >
                <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} required minLength={12} />
              </Field>
              <div className="row-actions">
                {entering && <button type="button" className="btn" onClick={() => { setEntering(false); setKey('') }} disabled={busy !== null}>Cancel</button>}
                <button type="submit" className="btn btn-primary" disabled={busy !== null || key.trim().length < 12}>{busy === 'save' ? 'Checking with PostGrid…' : 'Save and test'}</button>
              </div>
            </form>
          ) : (
            <div className="row-actions" style={{ marginTop: '0.75rem' }}>
              <button type="button" className="btn btn-primary" onClick={() => void runTest()} disabled={busy !== null}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
              <button type="button" className="btn" onClick={() => setEntering(true)} disabled={busy !== null}>Replace key</button>
              <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove account</button>
            </div>
          )}
          <p className="legend-note" style={{ marginTop: '0.75rem' }}>
            Only people with the “buy postage” permission can send cards to PostGrid, each mailing is confirmed first with its cost, a card is never sent twice,
            and every card sent is in the audit log. A card can be cancelled until PostGrid starts printing it. You add a payment method and see charges in
            your PostGrid account, not here.
          </p>
        </>
      )}
      {removing && (
        <Confirm
          title="Remove the PostGrid account?"
          message="Nobody will be able to send cards to PostGrid until an account is added again; cards can still be printed at the restaurant. Cards already sent still go out. Your PostGrid account itself is not changed."
          confirmLabel="Remove account"
          danger
          busy={busy === 'remove'}
          onConfirm={() => void remove()}
          onCancel={() => setRemoving(false)}
        />
      )}
    </div>
  )
}
