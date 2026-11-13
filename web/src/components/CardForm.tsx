import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { api, ApiError } from '../api/client'
import { COMMENT_SOURCES, PHONE_SOURCE, type CommentRequest, type CommentSummary, type DuplicateCheckResult } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import AddressCheckNote from './AddressCheckNote'
import { Alert, Field, addMonths, fmtDate, today } from './ui'

export type CardFormValues = Omit<CommentRequest, 'force'>

export function emptyCard(restaurantId: number, offer = ''): CardFormValues {
  const t = today()
  return {
    restaurantId,
    source: '',
    guestFirstName: '',
    guestLastName: '',
    streetAddress: '',
    city: '',
    state: '',
    zip: '',
    phone: '',
    couponOffer: offer,
    dateIssued: t,
    authorizedBy: '',
    visitDate: t,
    reason: '',
    reasonDetails: '',
    notes: '',
  }
}

export function cardFromSummary(c: CommentSummary): CardFormValues {
  return {
    restaurantId: c.restaurantId,
    source: c.source ?? '',
    guestFirstName: c.guestFirstName,
    guestLastName: c.guestLastName,
    streetAddress: c.streetAddress ?? '',
    city: c.city ?? '',
    state: c.state ?? '',
    zip: c.zip ?? '',
    phone: c.phone ?? '',
    couponOffer: c.couponOffer ?? '',
    dateIssued: c.dateIssued,
    authorizedBy: c.authorizedBy ?? '',
    visitDate: c.visitDate,
    reason: c.reason,
    reasonDetails: c.reasonDetails ?? '',
    notes: c.notes ?? '',
  }
}

export function DuplicateAlert({ check, showDetails, onDismiss }: { check: DuplicateCheckResult; showDetails: boolean; onDismiss?: () => void }) {
  if (!check.exactDuplicate && !check.likelyDuplicate && !check.repeatGuest) return null
  const kind = check.exactDuplicate ? 'error' : 'warn'
  const title = check.exactDuplicate
    ? 'This card looks like it was already entered'
    : check.likelyDuplicate
      ? 'Possible duplicate: another card for this guest around the same visit'
      : `Repeat guest: ${check.priorCount} previous card${check.priorCount === 1 ? '' : 's'} on file`
  return (
    <Alert kind={kind} onClose={onDismiss}>
      <strong>{title}</strong>
      {check.message && <div>{check.message}</div>}
      {check.repeatGuest && !check.exactDuplicate && check.priorCount > 0 && (
        <div>
          This guest has received {check.priorCount + 1} card{check.priorCount + 1 === 1 ? '' : 's'} including this one (alert threshold is {check.threshold}).
        </div>
      )}
      {showDetails && check.priorComments.length > 0 && (
        <ul className="dup-prior">
          {check.priorComments.slice(0, 6).map((p) => (
            <li key={p.id}>
              {fmtDate(p.visitDate)} · {p.restaurantName} · {p.reason}
              {p.couponOffer ? ` · sent ${p.couponOffer}` : ''} · entered by {p.createdByName}
              {p.responded ? ' · responded' : ' · not responded'}
            </li>
          ))}
          {check.priorComments.length > 6 && <li>…and {check.priorComments.length - 6} more</li>}
        </ul>
      )}
    </Alert>
  )
}

interface Props {
  initial: CardFormValues
  editingId?: number
  onSaved: (saved: CommentSummary) => void
  onCancel?: () => void
  submitLabel?: string
}

/**
 * The postcard entry form, shared by "Enter card" and "Edit comment".
 * Runs /comments/check as the guest fields are filled in, and again on submit.
 */
export default function CardForm({ initial, editingId, onSaved, onCancel, submitLabel }: Props) {
  const { restaurants, settings, has, isTeamMember } = useAuth()
  const [v, setV] = useState<CardFormValues>(initial)
  const [check, setCheck] = useState<DuplicateCheckResult | null>(null)
  const [checking, setChecking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [needsForce, setNeedsForce] = useState(false)
  // Whether the person took USPS's version of the address or kept their own, for the audit log. It only counts
  // while the address is still the one they chose about.
  const [addressChoice, setAddressChoice] = useState<{ choice: 'UsedUsps' | 'KeptOwn'; address: string; typed?: string } | null>(null)
  const addressLine = (x: Pick<CardFormValues, 'streetAddress' | 'city' | 'state' | 'zip'>) => [x.streetAddress, x.city, x.state, x.zip].map((p) => (p ?? '').trim()).join(', ')
  const timer = useRef<number | null>(null)
  const canSeeDetails = has('ViewComments')

  useEffect(() => setV(initial), [initial])

  const set = <K extends keyof CardFormValues>(k: K, val: CardFormValues[K]) => {
    setV((s) => ({ ...s, [k]: val }))
    setNeedsForce(false)
  }

  const validity = settings?.couponValidityMonths ?? 3
  const expires = useMemo(() => (v.dateIssued ? addMonths(v.dateIssued, validity) : ''), [v.dateIssued, validity])
  const reasons = settings?.reasonCategories ?? []
  const offers = settings?.couponOffers ?? []
  const byPhone = v.source === PHONE_SOURCE
  // Cards entered before "how it came in" was asked may be edited without choosing one.
  const hadSource = Boolean(initial.source)

  const canCheck = v.guestFirstName.trim() && v.guestLastName.trim() && (v.phone?.trim() || v.streetAddress?.trim()) && v.visitDate && v.restaurantId

  const runCheck = useCallback(async (values: CardFormValues) => {
    setChecking(true)
    try {
      const path = editingId ? `/comments/check?excludeId=${editingId}` : '/comments/check'
      const res = await api.post<DuplicateCheckResult>(path, { ...values, reason: values.reason || '-', force: false })
      setCheck(res)
      return res
    } catch {
      setCheck(null)
      return null
    } finally {
      setChecking(false)
    }
  }, [editingId])

  // Debounced live check once the identifying fields are present.
  useEffect(() => {
    if (!canCheck) {
      setCheck(null)
      return
    }
    if (timer.current) window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => void runCheck(v), 500)
    return () => {
      if (timer.current) window.clearTimeout(timer.current)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v.guestFirstName, v.guestLastName, v.phone, v.streetAddress, v.zip, v.visitDate, v.reason, v.restaurantId, canCheck])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (!v.restaurantId) return setError('Pick a restaurant.')
    if (!v.source && (!editingId || hadSource)) return setError('Choose how the comment came in: SMG survey, Culvers.com or a phone call.')
    if (byPhone && !v.phone?.trim()) return setError("The guest called, so enter their phone number.")
    if (!v.reason) return setError('Pick a reason.')
    if (settings?.requirePhoneOrAddress && !v.phone?.trim() && !v.streetAddress?.trim())
      return setError("Enter the guest's phone number or street address so repeat guests can be recognised.")
    setBusy(true)
    try {
      const chose = addressChoice && addressChoice.address === addressLine(v) ? addressChoice : null
      const body: CommentRequest = { ...v, source: v.source || null, force: needsForce, addressChoice: chose?.choice ?? null, addressAsTyped: chose?.typed ?? null }
      const saved = editingId ? await api.put<CommentSummary>(`/comments/${editingId}`, body) : await api.post<CommentSummary>('/comments', body)
      onSaved(saved)
    } catch (err) {
      const dup = err instanceof ApiError ? err.duplicate : null
      if (dup) {
        setCheck(dup)
        setNeedsForce(true)
        setError(null)
      } else {
        setError((err as Error).message)
      }
    } finally {
      setBusy(false)
    }
  }

  const blocking = check && (check.exactDuplicate || check.likelyDuplicate)
  const phoneInput = (autoFocus: boolean) => (
    <input
      value={v.phone ?? ''}
      onChange={(e) => set('phone', e.target.value)}
      inputMode="tel"
      autoComplete="off"
      placeholder="(815) 555-1234"
      required={byPhone}
      autoFocus={autoFocus}
    />
  )

  return (
    <form onSubmit={submit}>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {check && <DuplicateAlert check={check} showDetails={canSeeDetails} />}

      <fieldset className="panel source-panel">
        <legend>How did this comment come in?</legend>
        <div className="source-choice" role="radiogroup">
          {COMMENT_SOURCES.map((s) => (
            <label key={s.value} className={v.source === s.value ? 'selected' : ''}>
              <input type="radio" name="source" value={s.value} checked={v.source === s.value} onChange={() => set('source', s.value)} />
              <span className="source-label">{s.label}</span>
              <span className="source-hint">{s.hint}</span>
            </label>
          ))}
        </div>
        {byPhone && (
          <Field label="Guest's phone number" hint="Required for phone calls so a manager can call back.">
            {phoneInput(!editingId)}
          </Field>
        )}
      </fieldset>

      <div className="postcard">
        <div className="postcard-left">
          <h2>Coupon card</h2>
          <div className="enjoy">
            Please enjoy
            <select value={v.couponOffer ?? ''} onChange={(e) => set('couponOffer', e.target.value)}>
              <option value="">— choose an offer —</option>
              {offers.map((o) => (
                <option key={o} value={o}>
                  {o}
                </option>
              ))}
              {v.couponOffer && !offers.includes(v.couponOffer) && <option value={v.couponOffer}>{v.couponOffer}</option>}
            </select>
            the next time you visit us.
          </div>
          <div className="form-row">
            <Field label="Date issued">
              <input type="date" value={v.dateIssued ?? ''} onChange={(e) => set('dateIssued', e.target.value)} required />
            </Field>
            <Field label="Good until" hint={`${validity} month${validity === 1 ? '' : 's'} from date of issue`}>
              <input value={expires ? fmtDate(expires) : ''} readOnly tabIndex={-1} />
            </Field>
          </div>
          <Field label="Authorized signature (who signed the card)">
            <input value={v.authorizedBy ?? ''} onChange={(e) => set('authorizedBy', e.target.value)} placeholder="Manager name" />
          </Field>
          <div className="postcard-legal">
            Please present this card prior to ordering. One order per card. Cannot be exchanged for gift certificates or cash in whole or part and has no
            cash value. Not valid with other promotion or discounts. Valid only with authorized signature.
          </div>
        </div>
        <div className="postcard-right">
          <h2>Guest</h2>
          <div className="row" style={{ gridTemplateColumns: '1fr 1fr' }}>
            <Field label="First name">
              <input value={v.guestFirstName} onChange={(e) => set('guestFirstName', e.target.value)} required autoComplete="off" />
            </Field>
            <Field label="Last name">
              <input value={v.guestLastName} onChange={(e) => set('guestLastName', e.target.value)} required autoComplete="off" />
            </Field>
          </div>
          <Field label="Street address">
            <input value={v.streetAddress ?? ''} onChange={(e) => set('streetAddress', e.target.value)} autoComplete="off" />
          </Field>
          <div className="row">
            <Field label="City">
              <input value={v.city ?? ''} onChange={(e) => set('city', e.target.value)} autoComplete="off" />
            </Field>
            <Field label="State">
              <input value={v.state ?? ''} onChange={(e) => set('state', e.target.value)} maxLength={20} autoComplete="off" />
            </Field>
            <Field label="Zip">
              <input value={v.zip ?? ''} onChange={(e) => set('zip', e.target.value)} inputMode="numeric" autoComplete="off" />
            </Field>
          </div>
          <AddressCheckNote
            street={v.streetAddress ?? ''}
            city={v.city ?? ''}
            state={v.state ?? ''}
            zip={v.zip ?? ''}
            onUse={(a) => {
              setAddressChoice({ choice: 'UsedUsps', address: addressLine(a), typed: addressLine(v) })
              setV((s) => ({ ...s, streetAddress: a.streetAddress, city: a.city, state: a.state, zip: a.zip }))
            }}
            onKeep={() => setAddressChoice({ choice: 'KeptOwn', address: addressLine(v) })}
          />
          {!byPhone && (
            <Field label="Guest's phone number" hint={settings?.requirePhoneOrAddress ? 'Phone or street address is required to spot repeat guests.' : undefined}>
              {phoneInput(false)}
            </Field>
          )}
        </div>
      </div>

      <div className="panel" style={{ marginTop: '1rem' }}>
        <h2>What happened</h2>
        <div className="visit-panel">
          <Field label="Restaurant">
            <select value={v.restaurantId || ''} onChange={(e) => set('restaurantId', Number(e.target.value))} required disabled={restaurants.length === 1}>
              <option value="">— choose —</option>
              {restaurants.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.name}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Visit date" hint={v.visitDate ? new Date(v.visitDate + 'T12:00:00').toLocaleDateString(undefined, { weekday: 'long' }) : undefined}>
            <input type="date" value={v.visitDate} max={today()} onChange={(e) => set('visitDate', e.target.value)} required />
          </Field>
          <Field label="Reason for the card">
            <select value={v.reason} onChange={(e) => set('reason', e.target.value)} required>
              <option value="">— choose —</option>
              {reasons.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
              {v.reason && !reasons.includes(v.reason) && <option value={v.reason}>{v.reason}</option>}
            </select>
          </Field>
          <Field label="Details of the mistake / comment" className="span2">
            <textarea value={v.reasonDetails ?? ''} onChange={(e) => set('reasonDetails', e.target.value)} placeholder="What went wrong, what the guest said…" />
          </Field>
          {!isTeamMember && (
            <Field label="Internal notes" className="span2">
              <textarea value={v.notes ?? ''} onChange={(e) => set('notes', e.target.value)} placeholder="Not shown on the card; for managers and owners." />
            </Field>
          )}
        </div>
        <div className="actions">
          {checking && <span className="muted">Checking for duplicates…</span>}
          {onCancel && (
            <button type="button" className="btn" onClick={onCancel} disabled={busy}>
              Cancel
            </button>
          )}
          {blocking && needsForce ? (
            <button type="submit" className="btn btn-warn" disabled={busy}>
              {busy ? 'Saving…' : 'Submit anyway'}
            </button>
          ) : (
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? 'Saving…' : submitLabel ?? (editingId ? 'Save changes' : 'Save card')}
            </button>
          )}
        </div>
      </div>
    </form>
  )
}
