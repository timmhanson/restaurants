import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { GeocodioSite, ServiceTestResult } from '../api/types'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/**
 * Geocodio on Connected Systems: where the System Administrator enters the site's Geocodio API key, which checks
 * guest addresses. The key is typed in once; after that only its last four characters are shown. Entering it for
 * the first time also makes Geocodio the service that checks addresses (changed in the panel above; `chosen` is
 * that choice as it stands).
 */
export default function GeocodioPanel({ onChanged, chosen }: { onChanged?: () => void; chosen?: string | null }) {
  const [site, setSite] = useState<GeocodioSite | null>(null)
  const [key, setKey] = useState('')
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [test, setTest] = useState<ServiceTestResult | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)

  useEffect(() => {
    api.get<GeocodioSite>('/system/geocodio').then(setSite).catch((e) => setError((e as Error).message))
  }, [])

  const runTest = async () => {
    setBusy('test')
    setTest(null)
    setError(null)
    try {
      setTest(await api.post<ServiceTestResult>('/system/geocodio/test'))
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
      const after = await api.put<GeocodioSite>('/system/geocodio', { apiKey: key })
      setSite(after)
      setKey('')
      setEntering(false)
      setNotice(after.inUse ? 'Saved. Geocodio now checks guest addresses. Testing the connection…' : 'Saved. Testing the connection…')
      onChanged?.()
      setBusy(null)
      await runTest()
      setNotice(after.inUse ? 'Saved. Geocodio now checks guest addresses.' : 'Saved.')
    } catch (err) {
      setError((err as Error).message)
      setBusy(null)
    }
  }

  const remove = async () => {
    setBusy('remove')
    setError(null)
    setTest(null)
    setNotice(null)
    try {
      const after = await api.delete<GeocodioSite>('/system/geocodio')
      setSite(after)
      setNotice(after.configured ? 'Removed. The key in the web app’s settings is being used instead.' : 'Removed. Geocodio no longer checks guest addresses.')
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
      <h2>Geocodio</h2>
      <p className="muted">
        Geocodio checks each guest address as a card is entered: that it exists at that house number, how it is properly written, and, from the post
        office’s ZIP+4 data, that mail is delivered there and whether an apartment or suite number is missing. Create an API key at dash.geocod.io
        (API Keys) and enter it here. Each check uses two lookups (the address and the ZIP+4 data); Geocodio gives 2,500 lookups a day free and charges
        the account for more. Answers are remembered for a day, so the same address is not looked up twice.
      </p>
      <p className="muted">
        Geocodio does not offer addresses as they are typed. That stays with PostGrid’s Address Verification key, below.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {test && <Alert kind={test.ok ? 'success' : 'error'} onClose={() => setTest(null)}>{test.message}</Alert>}
      {!site ? (
        !error && <Spinner />
      ) : !site.canEnter ? (
        <Alert kind="info">This copy of the site uses a stand-in address service, so no key is needed here.</Alert>
      ) : (
        <>
          {site.configured ? (
            <dl className="kv">
              <dt>Key</dt>
              <dd>
                <Badge kind="accent">saved</Badge> ending in <code>{site.keyEnding}</code>{' '}
                {(chosen ? chosen === 'geocodio' : site.inUse) ? <Badge kind="ok">checks guest addresses</Badge> : <Badge kind="neutral" title="Choose Geocodio under Checking guest addresses, above">not the chosen service</Badge>}
              </dd>
              <dt>Entered</dt>
              <dd>
                {site.source === 'Saved in the site'
                  ? `${site.savedAt ? fmtDateTime(site.savedAt) : ''}${site.savedBy ? ` by ${site.savedBy}` : ''}`
                  : 'In the web app’s settings in Azure. Entering a key here takes over from it.'}
              </dd>
            </dl>
          ) : (
            <p><Badge kind="neutral">not set up</Badge> Enter the key to have Geocodio check guest addresses.</p>
          )}

          {entering || !site.configured ? (
            <form onSubmit={save} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
              <Field label="Geocodio API key" hint="Stored encrypted and never shown again.">
                <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} required minLength={12} />
              </Field>
              <div className="row-actions">
                {entering && <button type="button" className="btn" onClick={() => { setEntering(false); setKey('') }} disabled={busy !== null}>Cancel</button>}
                <button type="submit" className="btn btn-primary" disabled={busy !== null || key.trim().length < 12}>{busy === 'save' ? 'Saving…' : 'Save and test'}</button>
              </div>
            </form>
          ) : (
            <div className="row-actions" style={{ marginTop: '0.75rem' }}>
              <button type="button" className="btn btn-primary" onClick={() => void runTest()} disabled={busy !== null} title="Looks up one known address: two lookups">
                {busy === 'test' ? 'Testing…' : 'Test connection'}
              </button>
              <button type="button" className="btn" onClick={() => setEntering(true)} disabled={busy !== null}>Replace key</button>
              {site.source === 'Saved in the site' && (
                <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove</button>
              )}
            </div>
          )}
        </>
      )}
      {removing && (
        <Confirm
          title="Remove the Geocodio key?"
          message="Geocodio will no longer check guest addresses. Another service still does if one is set up; otherwise addresses are saved as typed. Nothing is changed at Geocodio."
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
