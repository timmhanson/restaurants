import { useEffect, useId, useRef, useState } from 'react'
import { api } from '../api/client'
import { track } from '../api/tracker'
import type { AddressPreview } from '../api/types'

const MIN_TYPED = 4

/** Restaurants for which addresses are not offered, and until when the form stops asking (a minute, so a change in set-up is picked up). */
const notOffered = new Map<number, number>()
const asksAgain = (restaurantId: number) => (notOffered.get(restaurantId) ?? 0) < Date.now()

/** Told to the System Administrator only, while they use the form inside a group. */
const WHY: Record<string, string> = {
  key: 'Address suggestions are off: PostGrid’s Address Verification key has not been entered. Add it on Connected Systems, under PostGrid, “Checking guest addresses” (it is a different key from the Print & Mail keys). Only you see this note.',
  mail: 'Address suggestions are offered for restaurants that have their cards printed and mailed, and that is off for this restaurant. Only you see this note.',
}

/**
 * The street address box on a guest's card. For restaurants that have their cards printed and mailed for them, it
 * offers addresses that begin the way the street has been typed so far; picking one fills in the street, city,
 * state and zip. Typing on is always possible, and nothing is offered where the site cannot.
 *
 * Addresses are guest details: the list is marked data-no-track so the click tracker never records one. That one
 * was picked is recorded, without the address.
 */
export default function AddressSuggestInput({ value, city, state, zip, restaurantId, onChange, onPick }: {
  value: string
  city: string
  state: string
  zip: string
  restaurantId: number
  onChange: (street: string) => void
  onPick: (address: AddressPreview) => void
}) {
  const [offered, setOffered] = useState<AddressPreview[]>([])
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(-1)
  const [why, setWhy] = useState<string | null>(null)
  const asked = useRef(0)
  // The street as it was filled in by a pick, so the box does not offer the same address straight back.
  const picked = useRef<string | null>(null)
  const typing = useRef(false)
  const listId = useId()

  useEffect(() => {
    const street = value.trim()
    if (!typing.current || !restaurantId || !asksAgain(restaurantId) || street.length < MIN_TYPED || street === picked.current) {
      if (street.length < MIN_TYPED) setOffered([])
      return
    }
    const mine = ++asked.current
    const timer = window.setTimeout(() => {
      api.post<{ available: boolean; suggestions: AddressPreview[]; why?: string | null }>('/addresses/suggest', { restaurantId, streetAddress: street, city, state, zip })
        .then((r) => {
          if (!r.available) notOffered.set(restaurantId, Date.now() + 60_000)
          setWhy(r.available ? null : r.why ?? null)
          if (mine !== asked.current) return
          setOffered(r.available ? r.suggestions : [])
          setActive(-1)
          setOpen(r.available && r.suggestions.length > 0)
        })
        .catch(() => { if (mine === asked.current) setOffered([]) })
    }, 300)
    return () => window.clearTimeout(timer)
    // City, state and zip narrow what is offered, but only the street being typed asks again.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value, restaurantId])

  const pick = (a: AddressPreview) => {
    picked.current = a.streetAddress.trim()
    typing.current = false
    asked.current++
    setOpen(false)
    setOffered([])
    track('Click', 'Picked a suggested address')
    onPick(a)
  }
  const showing = open && offered.length > 0

  return (
    <span className="suggest" data-no-track>
      <input
        value={value}
        autoComplete="off"
        role="combobox"
        aria-expanded={showing}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={showing && active >= 0 ? `${listId}-${active}` : undefined}
        onChange={(e) => { typing.current = true; picked.current = null; onChange(e.target.value) }}
        onFocus={() => setOpen(true)}
        onBlur={() => setOpen(false)}
        onKeyDown={(e) => {
          if (!showing) return
          if (e.key === 'ArrowDown') { e.preventDefault(); setActive((i) => (i + 1) % offered.length) }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setActive((i) => (i <= 0 ? offered.length - 1 : i - 1)) }
          else if (e.key === 'Enter' && active >= 0) { e.preventDefault(); pick(offered[active]) }
          else if (e.key === 'Escape') setOpen(false)
        }}
      />
      {why && WHY[why] && <span className="field-hint">{WHY[why]}</span>}
      {showing && (
        <span className="suggest-list" role="listbox" id={listId} aria-label="Addresses that match what you have typed">
          {offered.map((a, i) => (
            // Chosen on mouse-down, before the box loses focus and the list closes.
            <span key={`${a.streetAddress}|${a.city}|${a.zip}`} id={`${listId}-${i}`} role="option" aria-selected={i === active} className={`suggest-item ${i === active ? 'active' : ''}`}
              onMouseDown={(e) => { e.preventDefault(); pick(a) }} onMouseEnter={() => setActive(i)}>
              <strong>{a.streetAddress}</strong>
              <span className="muted small">{[a.city, [a.state, a.zip].filter(Boolean).join(' ')].filter(Boolean).join(', ')}</span>
            </span>
          ))}
          <span className="suggest-foot muted small">Pick your guest’s address, or keep typing.</span>
        </span>
      )}
    </span>
  )
}
