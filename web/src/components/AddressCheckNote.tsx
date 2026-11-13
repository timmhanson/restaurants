import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { AddressCheckResponse, AddressStatus, AddressSuggestion } from '../api/types'
import { Badge } from './ui'

export const ADDRESS_STATUS_LABEL: Record<AddressStatus, string> = {
  Verified: 'USPS confirmed',
  Corrected: 'USPS confirmed (written differently)',
  NeedsUnit: 'Needs an apartment or unit number',
  UnitNotConfirmed: 'Apartment or unit not recognised',
  NotFound: 'USPS does not recognise it',
}

/** The verdict saved on a card, as a small badge. Nothing is shown for cards that were never checked. */
export function AddressBadge({ status }: { status?: AddressStatus | null }) {
  if (!status) return null
  const kind = status === 'Verified' || status === 'Corrected' ? 'ok' : status === 'NotFound' ? 'danger' : 'warn'
  return <Badge kind={kind} title="Checked with USPS when the card was saved">{ADDRESS_STATUS_LABEL[status]}</Badge>
}

/**
 * Checks the address with USPS a moment after typing stops and says what it found: confirmed, a corrected
 * version to accept in one click, or a warning. It only advises; the card can always be saved. Shows nothing
 * when address checking is not set up or USPS cannot be reached.
 */
export default function AddressCheckNote({
  street,
  city,
  state,
  zip,
  onUse,
  onKeep,
}: {
  street: string
  city: string
  state: string
  zip: string
  onUse: (a: AddressSuggestion) => void
  /** The person chose to keep their own wording over USPS's. */
  onKeep?: () => void
}) {
  const [result, setResult] = useState<AddressCheckResponse | null>(null)
  const [checking, setChecking] = useState(false)
  const [kept, setKept] = useState<string | null>(null)
  const off = useRef(false)
  const key = [street, city, state, zip].map((s) => s.trim().toLowerCase()).join('|')
  const ready = Boolean(street.trim() && state.trim() && (city.trim() || zip.trim().length >= 5))

  useEffect(() => {
    setResult(null)
    if (!ready || off.current) return
    let cancelled = false
    const timer = window.setTimeout(() => {
      setChecking(true)
      api
        .post<AddressCheckResponse>('/addresses/check', { streetAddress: street, city, state, zip })
        .then((r) => {
          if (!r.available) off.current = true // not set up on this site: stop asking
          if (!cancelled) setResult(r)
        })
        .catch(() => undefined)
        .finally(() => {
          if (!cancelled) setChecking(false)
        })
    }, 900)
    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, ready])

  if (checking && !result) return <div className="address-check muted small">Checking the address with USPS…</div>
  if (!result?.available || result.status === 'Unavailable' || result.status === 'Incomplete') return null
  const s = result.suggested
  if (result.status === 'Verified')
    return (
      <div className="address-check ok small" role="status">
        ✓ USPS delivers to this address.
        {s && s.zip.length > 5 && zip.trim().length <= 5 && (
          <>
            {' '}
            <button type="button" className="link-btn" onClick={() => onUse(s)}>Add the full zip ({s.zip})</button>
          </>
        )}
      </div>
    )
  if (result.status === 'Corrected' && s) {
    if (kept === key) return null
    return (
      <div className="address-check suggest" role="status">
        <div>
          USPS writes this address as <strong>{s.streetAddress}, {s.city}, {s.state} {s.zip}</strong>.
        </div>
        <div className="row-actions">
          <button type="button" className="btn btn-sm btn-primary" onClick={() => onUse(s)}>Use this address</button>
          <button
            type="button"
            className="btn btn-sm"
            onClick={() => {
              setKept(key)
              onKeep?.()
            }}
          >
            Keep what I typed
          </button>
        </div>
      </div>
    )
  }
  return (
    <div className={`address-check ${result.status === 'NotFound' ? 'bad' : 'warn'}`} role="status">
      {result.message} <span className="muted">You can still save the card.</span>
    </div>
  )
}
