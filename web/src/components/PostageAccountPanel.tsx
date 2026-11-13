import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { PostageAccount } from '../api/types'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/**
 * The investment group's own EasyPost account, so the group pays for the postage on its own cards. The key is
 * typed in once; after that only its last four characters are ever shown, and replacing it means typing a new one.
 */
export default function PostageAccountPanel() {
  const [account, setAccount] = useState<PostageAccount | null>(null)
  const [key, setKey] = useState('')
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get<PostageAccount>('/settings/postage-account').then(setAccount).catch((e) => setError((e as Error).message))
  }, [])

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const saved = await api.put<PostageAccount>('/settings/postage-account', { apiKey: key })
      setAccount(saved)
      setKey('')
      setEntering(false)
      setNotice(
        saved.looksLikeTestKey
          ? 'Saved. This is a test key: it makes sample labels at no cost that are not valid postage. Switch to your production key when you are ready to mail.'
          : 'Saved. Postage for this group’s cards is now bought from this account.',
      )
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    setBusy(true)
    setError(null)
    try {
      setAccount(await api.delete<PostageAccount>('/settings/postage-account'))
      setNotice('The account was removed from this site. Nothing was changed at EasyPost.')
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
      setRemoving(false)
    }
  }

  return (
    <div className="panel">
      <h2>Postage account (EasyPost)</h2>
      <p className="muted">
        To buy USPS postage when printing cards, this investment group uses its own EasyPost account, so the postage is charged to you and not to another
        group. Stamps by hand need no account.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {!account ? (
        !error && <Spinner />
      ) : !account.canAddAccount ? (
        <Alert kind="info">Postage accounts cannot be added on this site yet.</Alert>
      ) : (
        <>
          {account.hasOwnAccount ? (
            <dl className="kv">
              <dt>Account</dt>
              <dd>
                <Badge kind="ok">connected</Badge> key ending in <code>{account.keyEnding}</code>{' '}
                {account.looksLikeTestKey && <Badge kind="warn" title="Sample labels only, not valid postage">test key</Badge>}
              </dd>
              <dt>Added</dt>
              <dd>{account.addedAt ? fmtDateTime(account.addedAt) : '—'}{account.addedBy ? ` by ${account.addedBy}` : ''}</dd>
            </dl>
          ) : (
            <p>
              <Badge kind="neutral">no account yet</Badge>{' '}
              {account.siteAccountAvailable
                ? 'Until you add one, postage is bought from the site’s shared account.'
                : 'Until you add one, cards print for a stamp by hand.'}
            </p>
          )}

          {entering || !account.hasOwnAccount ? (
            <form onSubmit={save} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
              <Field
                label={account.hasOwnAccount ? 'New EasyPost API key' : 'EasyPost API key'}
                hint="In EasyPost: Account Settings, then API Keys. Start with the Test key to try it at no cost. The key is stored encrypted and is never shown again."
              >
                <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} required minLength={20} />
              </Field>
              <div className="row-actions">
                {entering && <button type="button" className="btn" onClick={() => { setEntering(false); setKey('') }} disabled={busy}>Cancel</button>}
                <button type="submit" className="btn btn-primary" disabled={busy || key.trim().length < 20}>{busy ? 'Saving…' : 'Save account key'}</button>
              </div>
            </form>
          ) : (
            <div className="row-actions" style={{ marginTop: '0.75rem' }}>
              <button type="button" className="btn" onClick={() => setEntering(true)}>Replace key</button>
              <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)}>Remove account</button>
            </div>
          )}
          <p className="legend-note" style={{ marginTop: '0.75rem' }}>
            Only people with the “buy postage” permission can buy, each purchase is confirmed first, and every purchase is in the audit log. You add money
            and see charges in your EasyPost account, not here.
          </p>
        </>
      )}
      {removing && (
        <Confirm
          title="Remove this postage account?"
          message={
            account?.siteAccountAvailable
              ? 'Postage for this group’s cards will be bought from the site’s shared account instead. Your EasyPost account itself is not changed.'
              : 'People will no longer be able to buy postage when printing; cards will print for a stamp by hand. Your EasyPost account itself is not changed.'
          }
          confirmLabel="Remove account"
          danger
          busy={busy}
          onConfirm={() => void remove()}
          onCancel={() => setRemoving(false)}
        />
      )}
    </div>
  )
}
