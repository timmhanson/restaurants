import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { PostGridSite, ServiceTestResult } from '../api/types'
import SiteKeyBlock from './SiteKeyBlock'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/**
 * PostGrid on Connected Systems: the site's Print & Mail key, which prints and mails every investment group's cards
 * on the site's own PostGrid account, and its address verification key (checks guest addresses before or after USPS).
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
            Every investment group’s cards are printed and mailed on this one PostGrid account, so PostGrid bills you. You bill each group on{' '}
            <Link to="/system-billing">Billing</Link>, where you set your fee per card and each group’s monthly limit. Groups see “Mailed for you”; they never
            see PostGrid’s name. PostGrid gives you two <strong>Print &amp; Mail API keys</strong> (Settings, API Keys): a test key and a live key. Enter
            both. Whether cards go out for real or as tests is then a setting: for the whole site on <Link to="/system-billing">Billing</Link>, for one
            investment group under <Link to="/groups">Investment groups</Link> (Edit), or for one restaurant.
          </p>
          <h4 style={{ margin: '0.75rem 0 0.25rem' }}>Test key</h4>
          <SiteKeyBlock
            info={site.mailTestKey}
            label="PostGrid test key (begins test_)"
            hint="Used for anything set to test mailing: nothing is printed, mailed or charged. Stored encrypted and never shown again."
            testNote="Test key: cards sent with it are not printed, mailed or charged, and nothing is billed to groups. PostGrid makes a proof of each one."
            liveNote="This is a live key. Enter it under Live key instead."
            onSave={async (k) => {
              if (!/^test_/i.test(k.trim())) throw new Error('That is not a test key: PostGrid’s test keys begin test_. Enter a live key under Live key below.')
              setSite(await api.put<PostGridSite>('/system/postgrid/mail', { apiKey: k }))
              onChanged?.()
            }}
            onTest={() => api.post<ServiceTestResult>('/system/postgrid/mail/test?mode=test')}
            onRemove={async () => { setSite(await api.delete<PostGridSite>('/system/postgrid/mail?mode=test')); onChanged?.() }}
            removeTitle="Remove the PostGrid test key?"
            removeMessage="Restaurants set to test mailing will not be able to send test orders until a test key is entered again. Real mailing is not affected."
            onError={setError}
          />
          <h4 style={{ margin: '1rem 0 0.25rem' }}>Live key</h4>
          <SiteKeyBlock
            info={site.mailLiveKey}
            label="PostGrid live key (begins live_)"
            hint="Used for anything set to real mailing: cards are printed, mailed and charged to your PostGrid account. Stored encrypted and never shown again."
            testNote="This is a test key. Enter it under Test key instead."
            liveNote="Live key: cards sent with it are printed, mailed and charged to this PostGrid account, then billed to each group."
            onSave={async (k) => {
              if (/^test_/i.test(k.trim())) throw new Error('That is a test key. Enter it under Test key above; the live key begins live_.')
              setSite(await api.put<PostGridSite>('/system/postgrid/mail', { apiKey: k }))
              onChanged?.()
            }}
            onTest={() => api.post<ServiceTestResult>('/system/postgrid/mail/test?mode=live')}
            onRemove={async () => { setSite(await api.delete<PostGridSite>('/system/postgrid/mail?mode=live')); onChanged?.() }}
            removeTitle="Remove the PostGrid live key?"
            removeMessage="No group will be able to have cards mailed until a key is entered again; they can still print their own. Cards already sent are not affected, but their status can no longer be checked. Nothing is changed at PostGrid."
            onError={setError}
          />
          <p>
            {site.cardsMailed} card{site.cardsMailed === 1 ? '' : 's'} mailed ({site.cardsThisMonth} this month)
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
          message="PostGrid will no longer check guest addresses; USPS still does if its account is set up. Mailing cards is not affected. Nothing is changed at PostGrid."
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
