import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { GroupSettings } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import PostcardSettingsEditor from '../components/PostcardSettingsEditor'
import { Alert, Field, Spinner, fmtDateTime } from '../components/ui'

function ListEditor({ label, items, onChange, placeholder, hint }: { label: string; items: string[]; onChange: (v: string[]) => void; placeholder: string; hint?: string }) {
  const [draft, setDraft] = useState('')
  const add = () => {
    const t = draft.trim()
    if (!t || items.some((i) => i.toLowerCase() === t.toLowerCase())) return
    onChange([...items, t])
    setDraft('')
  }
  return (
    <Field label={label} hint={hint}>
      <div className="chip-list">
        {items.map((i) => (
          <span key={i} className="chip">
            {i}
            <button type="button" aria-label={`Remove ${i}`} onClick={() => onChange(items.filter((x) => x !== i))}>×</button>
          </span>
        ))}
        {items.length === 0 && <span className="muted">none yet</span>}
      </div>
      <div className="chip-add">
        <input value={draft} placeholder={placeholder} onChange={(e) => setDraft(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add() } }} />
        <button type="button" className="btn btn-sm" onClick={add}>Add</button>
      </div>
    </Field>
  )
}

export default function SettingsPage() {
  const { refreshContext, isSuperAdmin, groupId, groups } = useAuth()
  const [s, setS] = useState<GroupSettings | null>(null)
  const [store, setStore] = useState<string>('')
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const [dirty, setDirty] = useState(false)

  useEffect(() => {
    setS(null)
    api.get<GroupSettings>('/settings').then(setS).catch((e) => setError((e as Error).message))
    api.get<{ store: string }>('/settings/storage').then((r) => setStore(r.store)).catch(() => setStore(''))
  }, [groupId])

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (!s) return
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      setS(await api.put<GroupSettings>('/settings', s))
      setSaved(true)
      setDirty(false)
      await refreshContext()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const groupName = isSuperAdmin ? groups.find((g) => g.id === groupId)?.name : undefined

  return (
    <>
      <div className="page-head">
        <h1>Group settings</h1>
        {groupName && <span className="muted">{groupName}</span>}
      </div>
      {isSuperAdmin && !groupId && <Alert kind="warn">Open a group with "View as admin" on Investment groups first.</Alert>}
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {saved && <Alert kind="success" onClose={() => setSaved(false)}>Settings saved.</Alert>}
      {!s ? (
        <Spinner />
      ) : (
        <form onSubmit={submit}>
          <div className="grid grid-2">
            <div className="panel">
              <h2>The card</h2>
              <ListEditor label="Coupon offers" items={s.couponOffers} onChange={(v) => setS({ ...s, couponOffers: v })} placeholder="Free Value Basket" hint='Choices for the "Please enjoy ___" line.' />
              <br />
              <Field label="Follow up with the guest after (days)" hint="Days after a coupon card is sent before managers and owners are reminded to check it arrived. Each restaurant's owners can choose their own; 0 turns reminders off.">
                <input type="number" min={0} max={90} value={s.followUpDays} onChange={(e) => setS({ ...s, followUpDays: Number(e.target.value) })} />
              </Field>
              <br />
              <Field label="Coupon good for (months)" hint="Printed on the card as “good for 3 months from date of issue”.">
                <input type="number" min={1} max={24} value={s.couponValidityMonths} onChange={(e) => setS({ ...s, couponValidityMonths: Number(e.target.value) })} />
              </Field>
            </div>
            <div className="panel">
              <h2>Reasons</h2>
              <ListEditor label="Reason categories" items={s.reasonCategories} onChange={(v) => setS({ ...s, reasonCategories: v })} placeholder="Order mistake" hint="Why the guest received a card. Drives the dropdown and the analytics charts." />
              <br />
              <ListEditor label="Reply methods" items={s.replyMethods ?? []} onChange={(v) => setS({ ...s, replyMethods: v })} placeholder="Phone call" hint="How managers can reply to a guest. They pick one when they confirm a reply or log an attempt." />
            </div>
            <div className="panel">
              <h2>Duplicate &amp; repeat guest detection</h2>
              <div className="grid">
                <Field label="Duplicate window (days)" hint="A second card for the same guest within this many days of the visit is flagged as a likely duplicate entry. 0 only flags the exact same visit.">
                  <input type="number" min={0} max={365} value={s.duplicateWindowDays} onChange={(e) => setS({ ...s, duplicateWindowDays: Number(e.target.value) })} />
                </Field>
                <Field label="Repeat guest alert at (cards)" hint="Show the repeat-guest alert once a guest has this many cards on file, including the new one.">
                  <input type="number" min={2} max={100} value={s.repeatGuestAlertThreshold} onChange={(e) => setS({ ...s, repeatGuestAlertThreshold: Number(e.target.value) })} />
                </Field>
                <label className="check">
                  <input type="checkbox" checked={s.requirePhoneOrAddress} onChange={(e) => setS({ ...s, requirePhoneOrAddress: e.target.checked })} />
                  <span>Require a phone number or street address on every card <span className="muted">(needed to recognise repeat guests)</span></span>
                </label>
              </div>
            </div>
            <div className="panel">
              <h2>Appearance</h2>
              <div className="grid">
                <Field label="Investment group name" hint="Renamed by a System Administrator on Investment groups."><input value={s.groupDisplayName} readOnly disabled /></Field>
                <Field label="Accent colour">
                  <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
                    <input type="color" value={s.accentColor} onChange={(e) => setS({ ...s, accentColor: e.target.value })} style={{ width: 56, padding: 2 }} />
                    <input value={s.accentColor} onChange={(e) => setS({ ...s, accentColor: e.target.value })} pattern="#[0-9a-fA-F]{6}" />
                  </div>
                </Field>
              </div>
            </div>
          </div>
          <PostcardSettingsEditor
            settings={s}
            dirty={dirty}
            onChange={(postcard) => {
              setS({ ...s, postcard })
              setDirty(true)
            }}
          />
          <div className="panel">
            <div className="muted" style={{ fontSize: '0.85rem' }}>
              Stored in: <strong>{store || 'unknown'}</strong> — one settings document per investment group.
              {s.updatedAt && <> Last saved {fmtDateTime(s.updatedAt)} by {s.updatedBy}.</>}
            </div>
            <div className="actions">
              <button type="submit" className="btn btn-primary" disabled={busy || (isSuperAdmin && !groupId)}>{busy ? 'Saving…' : 'Save settings'}</button>
            </div>
          </div>
        </form>
      )}
    </>
  )
}
