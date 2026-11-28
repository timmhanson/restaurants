import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { AddressChecking } from '../api/types'
import { Alert, Badge, Spinner } from './ui'

const HINT: Record<string, string> = {
  geocodio: 'Finds the address and how it is written, with the post office’s ZIP+4 delivery data. Two lookups a check.',
  usps: 'The post office’s own address check. Free, with a developer account.',
  postgrid: 'Checks against post office data with the Address Verification key. Charged per lookup after the free ones.',
}

/**
 * Which service checks guest addresses, for the System Administrator: Geocodio, USPS or PostGrid. Only one whose
 * key has been entered (in its own panel below) can be chosen. The choice saves as it is made. The others that are
 * set up are asked only when the chosen one cannot answer, so one outage never switches address checking off.
 * `version` goes up when a key is saved or removed below, so the panel shows what can be chosen now; `onChosen` tells
 * the page which service is chosen, for the panels below.
 */
export default function AddressCheckingPanel({ version, onChosen }: { version: number; onChosen?: (first: string) => void }) {
  const [data, setData] = useState<AddressChecking | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get<AddressChecking>('/system/address-checking').then(setData).catch((e) => setError((e as Error).message))
  }, [version])
  // The panels below say whether theirs is the chosen service.
  useEffect(() => {
    if (data) onChosen?.(data.first)
  }, [data, onChosen])

  const choose = async (first: string) => {
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      setData(await api.put<AddressChecking>('/system/address-checking', { first }))
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const ready = data?.services.filter((s) => s.configured) ?? []
  const chosen = data?.services.find((s) => s.id === data.first)
  const backups = ready.filter((s) => s.id !== data?.first)
  return (
    <div className="panel">
      <h2>Checking guest addresses</h2>
      <p className="muted">
        Every guest address is checked as a card is entered, for every investment group, by the service you choose here. Enter a service’s key in its own
        panel below, then choose it. Groups never see which service it is.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!data ? (
        !error && <Spinner />
      ) : !data.canChoose ? (
        <Alert kind="info">This copy of the site uses a stand-in address service, so there is nothing to choose here.</Alert>
      ) : (
        <>
          <div className="source-choice" role="radiogroup" aria-label="Which service checks guest addresses">
            {data.services.map((s) => (
              <label key={s.id} className={s.id === data.first && s.configured ? 'selected' : ''} style={s.configured ? undefined : { opacity: 0.6 }}>
                <input type="radio" name="address-service" checked={s.id === data.first && s.configured} disabled={busy || !s.configured} onChange={() => void choose(s.id)} />
                <span className="source-label">
                  {s.name} {s.configured ? <Badge kind="ok">set up</Badge> : <Badge kind="neutral">not set up</Badge>}
                </span>
                <span className="source-hint">{HINT[s.id]}{s.configured ? '' : ' Enter its key below to choose it.'}</span>
              </label>
            ))}
          </div>
          {saved && <p className="muted small" style={{ margin: '0.25rem 0 0' }}>Saved.</p>}
          <p style={{ marginBottom: 0 }}>
            {ready.length === 0 ? (
              <><Badge kind="danger">not checked</Badge> No service is set up, so guest addresses are saved as typed.</>
            ) : chosen?.configured ? (
              <>
                <strong>{chosen.name}</strong> checks addresses.{' '}
                {backups.length > 0
                  ? `When it cannot answer (busy, down or out of lookups), ${backups.map((b) => b.name).join(', then ')} ${backups.length === 1 ? 'is' : 'are'} asked instead. Remove a service’s key below if you never want it asked.`
                  : 'No other service is set up, so when it cannot answer, the address is saved without a check.'}
              </>
            ) : (
              <>
                <strong>{ready[0].name}</strong> checks addresses, because {chosen?.name ?? 'the chosen service'} is not set up. Choose a service above.
              </>
            )}
          </p>
        </>
      )}
    </div>
  )
}
