import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { PostGridSite, ServiceTestResult } from '../api/types'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/**
 * PostGrid on Connected Systems: the site's address verification key (checks guest addresses before or after USPS),
 * and a summary of the investment groups that mail cards through their own PostGrid accounts.
 */
export default function PostGridSitePanel({ onChanged }: { onChanged?: () => void }) {
  const [site, setSite] = useState<PostGridSite | null>(null)
  const [key, setKey] = useState('')
  const [useFirst, setUseFirst] = useState(false)
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [test, setTest] = useState<ServiceTestResult | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | 'order' | null>(null)

  useEffect(() => {
    api.get<PostGridSite>('/system/postgrid').then(setSite).catch((e) => setError((e as Error).message))
  }, [])

  const runTest = async () => {
    setBusy('test')
    setTest(null)
    setError(null)
    try {
      setTest(await api.post<ServiceTestResult>('/system/postgrid/test'))
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
      setSite(await api.put<PostGridSite>('/system/postgrid', { apiKey: key, useFirst }))
      setKey('')
      setEntering(false)
      onChanged?.()
      setBusy(null)
      await runTest()
    } catch (err) {
      setError((err as Error).message)
      setBusy(null)
    }
  }

  const setOrder = async (first: boolean) => {
    setBusy('order')
    setError(null)
    try {
      setSite(await api.put<PostGridSite>('/system/postgrid/order', { useFirst: first }))
      onChanged?.()
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
      setSite(await api.delete<PostGridSite>('/system/postgrid'))
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
      <h2>PostGrid</h2>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!site ? (
        !error && <Spinner />
      ) : (
        <>
          <h3 style={{ marginTop: 0 }}>Printing and mailing cards</h3>
          <p className="muted">
            Each investment group adds its own PostGrid account in its Settings, so PostGrid bills each group for its own cards. To add one for a group,
            open the group with <strong>View as admin</strong> on <Link to="/groups">Investment groups</Link>, then go to Settings.
          </p>
          <p>
            {site.groupsWithMailAccounts > 0 ? (
              <Badge kind="ok">{site.groupsWithMailAccounts} group account{site.groupsWithMailAccounts === 1 ? '' : 's'}</Badge>
            ) : (
              <Badge kind="neutral">no group accounts yet</Badge>
            )}{' '}
            {site.cardsMailed} card{site.cardsMailed === 1 ? '' : 's'} mailed by PostGrid
            {site.testCardsMailed > 0 ? `, ${site.testCardsMailed} test order${site.testCardsMailed === 1 ? '' : 's'} (not mailed)` : ''}.
          </p>

          <h3>Checking guest addresses</h3>
          <p className="muted">
            PostGrid can also check each guest address as a card is entered, against USPS data, as a backup for USPS or instead of it. It uses a separate
            Address Verification key (in PostGrid: Address Verification, Developers). New accounts get some free lookups; after that PostGrid charges per
            lookup, billed to the account whose key is entered here.
          </p>
          {test && <Alert kind={test.ok ? 'success' : 'error'} onClose={() => setTest(null)}>{test.message}</Alert>}
          {!site.canEnter ? (
            <Alert kind="info">This copy of the site uses a stand-in address service, so no key is needed here.</Alert>
          ) : (
            <>
              {site.configured ? (
                <dl className="kv">
                  <dt>Key</dt>
                  <dd><Badge kind="accent">saved</Badge> ending in <code>{site.keyEnding}</code></dd>
                  <dt>Entered</dt>
                  <dd>
                    {site.source === 'Saved in the site'
                      ? `${site.savedAt ? fmtDateTime(site.savedAt) : ''}${site.savedBy ? ` by ${site.savedBy}` : ''}`
                      : 'In the web app’s settings in Azure. Entering a key here takes over from it.'}
                  </dd>
                  <dt>Order</dt>
                  <dd>
                    <div className="source-choice two" role="radiogroup" aria-label="Which service checks addresses first">
                      <label className={!site.useFirst ? 'selected' : ''}>
                        <input type="radio" name="pg-order" checked={!site.useFirst} disabled={busy !== null || site.source !== 'Saved in the site'} onChange={() => void setOrder(false)} />
                        <span className="source-label">USPS first</span>
                        <span className="source-hint">PostGrid is asked only when USPS is not set up or cannot answer.</span>
                      </label>
                      <label className={site.useFirst ? 'selected' : ''}>
                        <input type="radio" name="pg-order" checked={site.useFirst} disabled={busy !== null || site.source !== 'Saved in the site'} onChange={() => void setOrder(true)} />
                        <span className="source-label">PostGrid first</span>
                        <span className="source-hint">USPS is asked only when PostGrid cannot answer.</span>
                      </label>
                    </div>
                  </dd>
                </dl>
              ) : (
                <p><Badge kind="neutral">not set up</Badge> Only USPS checks addresses, when its account is set up.</p>
              )}

              {entering || !site.configured ? (
                <form onSubmit={save} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
                  <Field label="PostGrid Address Verification API key" hint="Stored encrypted and never shown again.">
                    <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} required minLength={12} />
                  </Field>
                  <label className="check">
                    <input type="checkbox" checked={useFirst} onChange={(e) => setUseFirst(e.target.checked)} />
                    <span>Ask PostGrid before USPS <span className="muted">(leave unticked to use PostGrid only when USPS cannot answer)</span></span>
                  </label>
                  <div className="row-actions">
                    {entering && <button type="button" className="btn" onClick={() => { setEntering(false); setKey('') }} disabled={busy !== null}>Cancel</button>}
                    <button type="submit" className="btn btn-primary" disabled={busy !== null || key.trim().length < 12}>{busy === 'save' ? 'Saving…' : 'Save and test'}</button>
                  </div>
                </form>
              ) : (
                <div className="row-actions" style={{ marginTop: '0.75rem' }}>
                  <button type="button" className="btn btn-primary" onClick={() => void runTest()} disabled={busy !== null}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
                  <button type="button" className="btn" onClick={() => { setEntering(true); setUseFirst(site.useFirst) }} disabled={busy !== null}>Replace key</button>
                  {site.source === 'Saved in the site' && (
                    <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove</button>
                  )}
                </div>
              )}
            </>
          )}
        </>
      )}
      {removing && (
        <Confirm
          title="Remove the PostGrid address key?"
          message="PostGrid will no longer check guest addresses; USPS still does if its account is set up. Group mailing accounts are not affected. Nothing is changed at PostGrid."
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
