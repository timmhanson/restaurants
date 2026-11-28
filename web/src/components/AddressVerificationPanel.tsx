import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { AddressChecking, AddressService, ServiceTestResult } from '../api/types'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

type Id = 'geocodio' | 'usps' | 'postgrid'

/** Where each service's key is saved, tested and removed. */
const PATH: Record<Id, string> = { geocodio: '/system/geocodio', usps: '/system/usps-account', postgrid: '/system/postgrid' }

const ABOUT: Record<Id, string> = {
  geocodio:
    'Geocodio checks that the address exists at that house number and how it is properly written, and, from the post office’s ZIP+4 data, that mail is delivered there and whether an apartment or suite number is missing. Create an API key at dash.geocod.io (API Keys). Each check uses two lookups (the address and the ZIP+4 data); Geocodio gives 2,500 lookups a day free and charges the account for more.',
  usps:
    'USPS is the post office’s own address check, and it is free. Create a developer account at developers.usps.com and add an app: USPS gives you a Consumer Key and a Consumer Secret.',
  postgrid:
    'PostGrid checks each address against post office data. It uses its Address Verification key (in PostGrid: Address Verification, Developers), which is a different key from the Print & Mail keys. New accounts get some free lookups; after that PostGrid charges the account per lookup.',
}

/**
 * Address verification on Connected Systems, in one place: the System Administrator picks the service in the
 * drop-down (Geocodio, USPS or PostGrid), sees that service's key, and saves. Saving makes the service picked the
 * one that checks every guest address; the others are then not asked, even with their keys saved. Until Save is
 * pressed nothing changes. A key is typed in once and after that only its last four characters are shown.
 *
 * PostGrid's key has a second use whichever service checks addresses: it offers addresses as they are typed. So its
 * key can also be saved on its own, without making PostGrid the service that checks.
 */
export default function AddressVerificationPanel({ onChanged }: { onChanged?: () => void }) {
  const [data, setData] = useState<AddressChecking | null>(null)
  const [picked, setPicked] = useState<Id | null>(null)
  const [key, setKey] = useState('')
  const [secret, setSecret] = useState('')
  const [testEnvironment, setTestEnvironment] = useState(false)
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [test, setTest] = useState<ServiceTestResult | null>(null)
  const [busy, setBusy] = useState<'save' | 'key' | 'test' | 'remove' | null>(null)

  const load = () => api.get<AddressChecking>('/system/address-checking').then((d) => { setData(d); return d })
  useEffect(() => {
    load().catch((e) => setError((e as Error).message))
  }, [])

  const id = (picked ?? data?.first ?? 'geocodio') as Id
  const service = data?.services.find((s) => s.id === id) as AddressService | undefined
  const current = data?.services.find((s) => s.id === data.first)
  const name = service?.name ?? ''
  const showFields = entering || !service?.configured
  const min = id === 'usps' ? 8 : 12
  const typed = id === 'usps' ? key.trim().length >= min && secret.trim().length >= min : key.trim().length >= min
  const switching = Boolean(data && id !== data.first)
  // Something to save: a key that has been typed, or a different service picked that already has its key.
  const canSave = showFields ? typed : switching

  const clear = () => {
    setKey('')
    setSecret('')
    setEntering(false)
  }
  const pick = (next: Id) => {
    setPicked(next)
    clear()
    setTestEnvironment(Boolean(data?.services.find((s) => s.id === next)?.testEnvironment))
    setTest(null)
    setNotice(null)
    setError(null)
  }

  const runTest = async (which: Id) => {
    setBusy('test')
    setTest(null)
    try {
      const result = await api.post<ServiceTestResult>(`${PATH[which]}/test`)
      setTest(result)
      return result.ok
    } catch (e) {
      setError((e as Error).message)
      return false
    } finally {
      setBusy(null)
    }
  }

  /** Saves the key that was typed, if one was. `use` says whether PostGrid then becomes the service that checks. */
  const saveKey = async (use: boolean) => {
    if (!showFields) return
    if (id === 'usps') await api.put(PATH.usps, { consumerKey: key, consumerSecret: secret, testEnvironment })
    else if (id === 'postgrid') await api.put(PATH.postgrid, { apiKey: key, useFirst: use })
    else await api.put(PATH.geocodio, { apiKey: key })
  }

  /** Save: the key typed (if any), and the service picked becomes the one that checks every address. */
  const save = async (e: FormEvent) => {
    e.preventDefault()
    if (!canSave) return
    setBusy('save')
    setError(null)
    setNotice(null)
    setTest(null)
    try {
      await saveKey(true)
      setData(await api.put<AddressChecking>('/system/address-checking', { first: id }))
      clear()
      setPicked(null)
      setNotice(`Saved. ${name} now checks every guest address.`)
      onChanged?.()
      setBusy(null)
      // Saved either way; a failed test means its checks will not work until the key is put right.
      if (!(await runTest(id))) {
        setNotice(null)
        setError(`Saved: ${name} is now the service that checks guest addresses, but its test did not pass (see below). Until that is put right, addresses are saved without a check. Replace the key, or pick another service and save.`)
      }
    } catch (err) {
      setError((err as Error).message)
      await load().catch(() => undefined)
      setBusy(null)
    }
  }

  /** PostGrid only: keeps its key for offering addresses as they are typed, without making it the service that checks. */
  const saveKeyOnly = async () => {
    setBusy('key')
    setError(null)
    setNotice(null)
    setTest(null)
    try {
      await saveKey(false)
      await load()
      clear()
      setNotice(`Saved the key. ${current?.configured ? current.name : 'The service chosen before'} still checks guest addresses; PostGrid’s key is used to offer addresses as they are typed.`)
      onChanged?.()
      setBusy(null)
      await runTest(id)
    } catch (err) {
      setError((err as Error).message)
      setBusy(null)
    }
  }

  const remove = async () => {
    setBusy('remove')
    setError(null)
    setNotice(null)
    setTest(null)
    try {
      await api.delete(PATH[id])
      const after = await load()
      const still = after.services.find((s) => s.id === id)?.configured
      setNotice(still ? `Removed. ${name}’s key in the web app’s settings is being used instead.` : `Removed ${name}’s key.`)
      clear()
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
      <h2>Checking guest addresses</h2>
      <p className="muted">
        Every guest address is checked as a card is entered, for every investment group, by one service. Pick the service, enter its key, and save: from
        then on it does every check and the others are not asked. Groups never see which service it is.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!data || !service ? (
        !error && <Spinner />
      ) : !data.canChoose ? (
        <Alert kind="info">This copy of the site uses a stand-in address service, so there is nothing to choose or enter here.</Alert>
      ) : (
        <>
          <p>
            {current?.configured
              ? <><Badge kind="ok">in use</Badge> <strong>{current.name}</strong> checks every guest address.</>
              : <><Badge kind="danger">not checked</Badge> No service is checking guest addresses, so they are saved as typed. Pick one below, enter its key and save.</>}
          </p>
          <form onSubmit={save} className="grid" style={{ maxWidth: 640 }}>
            <Field label="Service that checks guest addresses">
              <select value={id} onChange={(e) => pick(e.target.value as Id)} disabled={busy !== null}>
                {data.services.map((s) => (
                  <option key={s.id} value={s.id}>
                    {s.name}{s.id === data.first && s.configured ? ' (in use)' : s.configured ? ' (key saved)' : ''}
                  </option>
                ))}
              </select>
            </Field>
            <p className="muted" style={{ margin: 0 }}>{ABOUT[id]}</p>
            {switching && (
              <Alert kind="warn">
                Not saved yet. {current?.configured ? `${current.name} still checks guest addresses` : 'Nothing checks guest addresses'} until you press Save
                {showFields ? `, once ${id === 'usps' ? 'the Consumer Key and Secret are' : 'the key is'} entered` : ''}.
              </Alert>
            )}
            {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
            {test && <Alert kind={test.ok ? 'success' : 'error'} onClose={() => setTest(null)}>{test.message}</Alert>}

            {service.configured && (
              <dl className="kv" style={{ margin: 0 }}>
                <dt>{id === 'usps' ? 'Account' : 'Key'}</dt>
                <dd>
                  <Badge kind="accent">saved</Badge> {id === 'usps' ? 'Consumer Key ending in' : 'ending in'} <code>{service.keyEnding}</code>{' '}
                  {service.testEnvironment && <Badge kind="warn" title="USPS’s practice system">testing environment</Badge>}
                </dd>
                <dt>Entered</dt>
                <dd>
                  {service.source === 'Saved in the site'
                    ? `${service.savedAt ? fmtDateTime(service.savedAt) : ''}${service.savedBy ? ` by ${service.savedBy}` : ''}`
                    : 'In the web app’s settings in Azure. Entering one here takes over from it.'}
                </dd>
              </dl>
            )}

            {showFields && (
              id === 'usps' ? (
                <>
                  <Field label="USPS Consumer Key">
                    <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} minLength={min} />
                  </Field>
                  <Field label="USPS Consumer Secret" hint="Both are stored encrypted and are never shown again.">
                    <input type="password" value={secret} onChange={(e) => setSecret(e.target.value)} autoComplete="off" spellCheck={false} minLength={min} />
                  </Field>
                  <label className="check">
                    <input type="checkbox" checked={testEnvironment} onChange={(e) => setTestEnvironment(e.target.checked)} />
                    <span>Use USPS’s testing environment <span className="muted">(leave unticked for real address checks)</span></span>
                  </label>
                </>
              ) : (
                <Field label={id === 'geocodio' ? 'Geocodio API key' : 'PostGrid Address Verification API key'} hint="Stored encrypted and never shown again.">
                  <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} minLength={min} />
                </Field>
              )
            )}

            <div className="row-actions">
              <button type="submit" className="btn btn-primary" disabled={busy !== null || !canSave} title={canSave ? `Makes ${name} the service that checks every guest address` : undefined}>
                {busy === 'save' ? 'Saving…' : 'Save'}
              </button>
              {id === 'postgrid' && showFields && data.first !== 'postgrid' && (
                <button type="button" className="btn" onClick={() => void saveKeyOnly()} disabled={busy !== null || !typed}
                  title="Keeps the key for offering addresses as they are typed, without changing which service checks addresses">
                  {busy === 'key' ? 'Saving…' : 'Save key only'}
                </button>
              )}
              {entering && <button type="button" className="btn" onClick={clear} disabled={busy !== null}>Cancel</button>}
              {service.configured && !entering && (
                <>
                  <button type="button" className="btn" onClick={() => void runTest(id)} disabled={busy !== null}
                    title={id === 'geocodio' ? 'Looks up one known address: two lookups' : 'Looks up one known address'}>
                    {busy === 'test' ? 'Testing…' : 'Test connection'}
                  </button>
                  <button type="button" className="btn" onClick={() => { setEntering(true); setTestEnvironment(Boolean(service.testEnvironment)) }} disabled={busy !== null}>
                    {id === 'usps' ? 'Replace details' : 'Replace key'}
                  </button>
                  {service.source === 'Saved in the site' && (
                    <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove {id === 'usps' ? 'account' : 'key'}</button>
                  )}
                </>
              )}
            </div>
            <p className="muted small" style={{ margin: 0 }}>
              {canSave
                ? `Save makes ${name} the service that checks every guest address${showFields ? ', and stores what you entered' : ''}.`
                : switching || showFields
                  ? `Enter ${id === 'usps' ? 'the Consumer Key and Secret' : 'the key'} to save.`
                  : `${name} is the service in use. Pick another in the list to change it.`}
              {id === 'postgrid' && showFields && data.first !== 'postgrid' ? ' “Save key only” keeps the key just for offering addresses as they are typed.' : ''}
            </p>
          </form>

          <p className="muted" style={{ marginBottom: 0, marginTop: '1rem' }}>
            <strong>Addresses offered as they are typed:</strong>{' '}
            {data.offersAddresses
              ? 'on, for restaurants that have their cards printed and mailed. That uses PostGrid’s Address Verification key and no lookups, whichever service checks addresses.'
              : 'off. Only PostGrid can do that: pick PostGrid in the list and save its Address Verification key (“Save key only” leaves the service that checks addresses as it is).'}
          </p>
        </>
      )}
      {removing && service && (
        <Confirm
          title={`Remove ${name}’s ${id === 'usps' ? 'account' : 'key'}?`}
          message={`${id === data?.first ? `${name} is the service that checks guest addresses, so they will be saved as typed, with no check, until you save another service.` : `${name} is not the service that checks addresses, so checking carries on as it is.`}${id === 'postgrid' ? ' Addresses will no longer be offered as they are typed. Mailing cards is not affected.' : ''} Nothing is changed at ${name}.`}
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
