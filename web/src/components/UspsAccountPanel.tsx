import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { ServiceTestResult, UspsAccount } from '../api/types'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/**
 * Where the System Administrator enters the site's USPS developer account, which switches on address checking
 * for every investment group. The Consumer Key and Secret are typed in once; after that only the key's last four
 * characters are shown, and changing them means typing both again.
 */
export default function UspsAccountPanel({ onChanged }: { onChanged?: () => void }) {
  const [account, setAccount] = useState<UspsAccount | null>(null)
  const [key, setKey] = useState('')
  const [secret, setSecret] = useState('')
  const [testEnvironment, setTestEnvironment] = useState(false)
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [test, setTest] = useState<ServiceTestResult | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)

  useEffect(() => {
    api.get<UspsAccount>('/system/usps-account').then(setAccount).catch((e) => setError((e as Error).message))
  }, [])

  const runTest = async () => {
    setBusy('test')
    setTest(null)
    setError(null)
    try {
      setTest(await api.post<ServiceTestResult>('/system/usps-account/test'))
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
    setNotice(null)
    setTest(null)
    try {
      setAccount(await api.put<UspsAccount>('/system/usps-account', { consumerKey: key, consumerSecret: secret, testEnvironment }))
      setKey('')
      setSecret('')
      setEntering(false)
      setNotice('Saved. Testing the connection to USPS…')
      onChanged?.()
      setBusy(null)
      await runTest()
      setNotice(null)
    } catch (err) {
      setError((err as Error).message)
      setBusy(null)
    }
  }

  const remove = async () => {
    setBusy('remove')
    setError(null)
    setTest(null)
    try {
      const after = await api.delete<UspsAccount>('/system/usps-account')
      setAccount(after)
      setNotice(after.configured ? 'Removed. The account in the web app’s settings is being used instead.' : 'Removed. Guest addresses are no longer checked.')
      onChanged?.()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(null)
      setRemoving(false)
    }
  }

  return (
    <div className="panel">
      <h2>USPS account for address checking</h2>
      <p className="muted">
        With a USPS account, every guest address is checked as a card is entered, for every investment group. It is free: create a developer account at
        developers.usps.com, add an app, and USPS gives you a Consumer Key and a Consumer Secret to enter here.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {test && <Alert kind={test.ok ? 'success' : 'error'} onClose={() => setTest(null)}>{test.message}</Alert>}
      {!account ? (
        !error && <Spinner />
      ) : !account.canEnter ? (
        <Alert kind="info">This copy of the site uses a stand-in for USPS, so no account is needed here.</Alert>
      ) : (
        <>
          {account.configured ? (
            <dl className="kv">
              <dt>Account</dt>
              <dd>
                <Badge kind="accent">details saved</Badge> Consumer Key ending in <code>{account.keyEnding}</code>{' '}
                {account.testEnvironment && <Badge kind="warn" title="USPS's practice system">testing environment</Badge>}
              </dd>
              <dt>Entered</dt>
              <dd>
                {account.source === 'Saved in the site'
                  ? `${account.savedAt ? fmtDateTime(account.savedAt) : ''}${account.savedBy ? ` by ${account.savedBy}` : ''}`
                  : 'In the web app’s settings in Azure. Entering details here takes over from those.'}
              </dd>
            </dl>
          ) : (
            <p><Badge kind="neutral">not set up</Badge> Guest addresses are saved as typed, with no check.</p>
          )}

          {entering || !account.configured ? (
            <form onSubmit={save} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
              <Field label="Consumer Key">
                <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} required minLength={8} />
              </Field>
              <Field label="Consumer Secret" hint="Both are stored encrypted and are never shown again.">
                <input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="off" spellCheck={false} required minLength={8} />
              </Field>
              <label className="check">
                <input type="checkbox" checked={testEnvironment} onChange={(e) => setTestEnvironment(e.target.checked)} />
                <span>Use USPS’s testing environment <span className="muted">(leave unticked for real address checks)</span></span>
              </label>
              <div className="row-actions">
                {entering && (
                  <button type="button" className="btn" onClick={() => { setEntering(false); setKey(''); setSecret('') }} disabled={busy !== null}>
                    Cancel
                  </button>
                )}
                <button type="submit" className="btn btn-primary" disabled={busy !== null || key.trim().length < 8 || secret.trim().length < 8}>
                  {busy === 'save' ? 'Saving…' : 'Save and test'}
                </button>
              </div>
            </form>
          ) : (
            <div className="row-actions" style={{ marginTop: '0.75rem' }}>
              <button type="button" className="btn btn-primary" onClick={() => void runTest()} disabled={busy !== null}>
                {busy === 'test' ? 'Testing…' : 'Test connection'}
              </button>
              <button type="button" className="btn" onClick={() => { setEntering(true); setTestEnvironment(account.testEnvironment) }} disabled={busy !== null}>
                Replace details
              </button>
              {account.source === 'Saved in the site' && (
                <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove</button>
              )}
            </div>
          )}
        </>
      )}
      {removing && (
        <Confirm
          title="Remove the USPS account?"
          message="Guest addresses will no longer be checked as cards are entered. Nothing is changed at USPS."
          confirmLabel="Remove"
          danger
          busy={busy === 'remove'}
          onConfirm={() => void remove()}
          onCancel={() => setRemoving(false)}
        />
      )}
    </div>
  )
}
