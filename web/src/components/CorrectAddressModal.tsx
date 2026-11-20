import { useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { CommentSummary } from '../api/types'
import AddressCheckNote from './AddressCheckNote'
import AddressSuggestInput from './AddressSuggestInput'
import { Alert, Field, Modal } from './ui'

/**
 * Puts a guest's mailing address right from the mailing screen, without opening the whole card: when the mailing
 * service did not take the address, or the card has none in full. The street box offers addresses as it is typed
 * (where the site can), and the address is checked as on Enter Card, with its corrected form one click away.
 * Nothing else on the card changes, and the card goes back among the ones to send.
 */
export default function CorrectAddressModal({ card, problem, onClose, onSaved }: {
  card: CommentSummary
  /** What the mailing service said about the address, when it refused the card. */
  problem?: string | null
  onClose: () => void
  onSaved: (card: CommentSummary) => void
}) {
  const [a, setA] = useState({ streetAddress: card.streetAddress ?? '', city: card.city ?? '', state: card.state ?? '', zip: card.zip ?? '' })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const guest = `${card.guestFirstName} ${card.guestLastName}`.trim()
  const complete = a.streetAddress.trim() && a.city.trim() && a.state.trim() && a.zip.trim()

  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onSaved(await api.put<CommentSummary>(`/comments/${card.id}/address`, a))
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  return (
    <Modal title={`Correct the address · ${guest}`} onClose={onClose}>
      <form onSubmit={save} className="grid" data-no-track>
        {problem && <Alert kind="warn">The card was not sent: {problem}</Alert>}
        {error && <Alert kind="error">{error}</Alert>}
        <p className="muted" style={{ margin: 0 }}>
          Check the address against what the guest wrote, then save. Common causes: a missing apartment or unit number, a misspelt street, or “Hwy” where the post
          office writes “Route”. Saving changes only the address; the card then goes back among the cards to send.
        </p>
        <Field label="Street address">
          <AddressSuggestInput
            value={a.streetAddress}
            city={a.city}
            state={a.state}
            zip={a.zip}
            restaurantId={card.restaurantId}
            onChange={(streetAddress) => setA((s) => ({ ...s, streetAddress }))}
            onPick={(p) => setA((s) => ({ streetAddress: p.streetAddress, city: p.city ?? s.city, state: p.state ?? s.state, zip: p.zip ?? s.zip }))}
          />
        </Field>
        <div className="row">
          <Field label="City"><input value={a.city} onChange={(e) => setA((s) => ({ ...s, city: e.target.value }))} autoComplete="off" /></Field>
          <Field label="State"><input value={a.state} onChange={(e) => setA((s) => ({ ...s, state: e.target.value }))} maxLength={20} autoComplete="off" /></Field>
          <Field label="Zip"><input value={a.zip} onChange={(e) => setA((s) => ({ ...s, zip: e.target.value }))} inputMode="numeric" autoComplete="off" /></Field>
        </div>
        <AddressCheckNote street={a.streetAddress} city={a.city} state={a.state} zip={a.zip} onUse={(s) => setA({ streetAddress: s.streetAddress, city: s.city, state: s.state, zip: s.zip })} onKeep={() => undefined} />
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy || !complete}>{busy ? 'Saving…' : 'Save the address'}</button>
        </div>
      </form>
    </Modal>
  )
}
