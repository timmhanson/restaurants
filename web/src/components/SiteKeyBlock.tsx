import { useState, type FormEvent, type ReactNode } from 'react'
import type { ServiceTestResult, SiteKeyInfo } from '../api/types'
import { Alert, Badge, Confirm, Field, fmtDateTime } from './ui'

/**
 * One secret key on Connected Systems (a mailing key, a payment key): shows only its last four characters, and lets
 * the System Administrator save and test it, test it again, replace it or remove it. The key itself is never shown.
 */
export default function SiteKeyBlock({ info, label, hint, minLength = 12, testNote, liveNote, onSave, onTest, onRemove, removeTitle, removeMessage, onError }: {
  info: SiteKeyInfo
  label: string
  hint: string
  minLength?: number
  /** Shown beside a test key and a live key. */
  testNote: ReactNode
  liveNote: ReactNode
  onSave: (key: string) => Promise<void>
  onTest: () => Promise<ServiceTestResult>
  onRemove: () => Promise<void>
  removeTitle: string
  removeMessage: string
  onError: (message: string | null) => void
}) {
  const [key, setKey] = useState('')
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [test, setTest] = useState<ServiceTestResult | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)

  const run = async (what: 'save' | 'test' | 'remove', work: () => Promise<void>) => {
    setBusy(what)
    onError(null)
    try {
      await work()
    } catch (e) {
      onError((e as Error).message)
    } finally {
      setBusy(null)
      setRemoving(false)
    }
  }
  const runTest = () => run('test', async () => { setTest(null); setTest(await onTest()) })
  const save = (e: FormEvent) => {
    e.preventDefault()
    void run('save', async () => {
      setTest(null)
      await onSave(key)
      setKey('')
      setEntering(false)
      setTest(await onTest())
    })
  }

  return (
    <>
      {test && <Alert kind={test.ok ? 'success' : 'error'} onClose={() => setTest(null)}>{test.message}</Alert>}
      {info.configured ? (
        <dl className="kv">
          <dt>Key</dt>
          <dd>
            <Badge kind="ok">connected</Badge> ending in <code>{info.keyEnding}</code>{' '}
            {info.test ? <Badge kind="warn">test key</Badge> : <Badge kind="accent">live key</Badge>}
          </dd>
          <dt>Mode</dt>
          <dd>{info.test ? testNote : liveNote}</dd>
          <dt>Entered</dt>
          <dd>
            {info.source === 'Saved in the site'
              ? `${info.savedAt ? fmtDateTime(info.savedAt) : ''}${info.savedBy ? ` by ${info.savedBy}` : ''}`
              : 'In the web app’s settings in Azure. Entering a key here takes over from it.'}
          </dd>
        </dl>
      ) : (
        <p><Badge kind="neutral">not set up</Badge></p>
      )}
      {entering || !info.configured ? (
        <form onSubmit={save} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
          <Field label={label} hint={hint}>
            <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} required minLength={minLength} />
          </Field>
          <div className="row-actions">
            {entering && <button type="button" className="btn" onClick={() => { setEntering(false); setKey('') }} disabled={busy !== null}>Cancel</button>}
            <button type="submit" className="btn btn-primary" disabled={busy !== null || key.trim().length < minLength}>{busy === 'save' ? 'Checking…' : 'Save and test'}</button>
          </div>
        </form>
      ) : (
        <div className="row-actions" style={{ marginTop: '0.75rem' }}>
          <button type="button" className="btn btn-primary" onClick={() => void runTest()} disabled={busy !== null}>{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
          <button type="button" className="btn" onClick={() => setEntering(true)} disabled={busy !== null}>Replace key</button>
          {info.source === 'Saved in the site' && (
            <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove</button>
          )}
        </div>
      )}
      {removing && (
        <Confirm title={removeTitle} message={removeMessage} confirmLabel="Remove" danger busy={busy === 'remove'} onConfirm={() => void run('remove', onRemove)} onCancel={() => setRemoving(false)} />
      )}
    </>
  )
}
