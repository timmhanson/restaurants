import GroupRulesPanel from '../components/GroupRulesPanel'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { GroupSettings } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import PostageAccountPanel from '../components/PostageAccountPanel'
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
  const [error, setError] = useState<string | null>(null)
  // Every change is saved by itself a moment after it is made; there is no Save button.
  const [status, setStatus] = useState<'idle' | 'waiting' | 'saving' | 'saved' | 'failed'>('idle')
  const changes = useRef(0)
  const latest = useRef<GroupSettings | null>(null)
  const unsaved = useRef(false)
  const canSave = !(isSuperAdmin && !groupId)

  useEffect(() => {
    setS(null)
    setStatus('idle')
    api.get<GroupSettings>('/settings').then(setS).catch((e) => setError((e as Error).message))
  }, [groupId])

  /** A change made on the page: shown at once, and saved shortly after the last one. */
  const change = (next: GroupSettings) => {
    setS(next)
    latest.current = next
    changes.current++
    unsaved.current = true
    setStatus('waiting')
  }

  useEffect(() => {
    if (status !== 'waiting' || !s || !canSave) return
    const mine = changes.current
    const timer = window.setTimeout(async () => {
      setStatus('saving')
      try {
        const fresh = await api.put<GroupSettings>('/settings', s)
        // Something else may have been changed while this was being saved: that goes next, and is not overwritten.
        if (changes.current === mine) {
          unsaved.current = false
          setS(fresh)
          setError(null)
          setStatus('saved')
        } else setStatus('waiting')
        void refreshContext()
      } catch (err) {
        setError(`Not saved: ${(err as Error).message}`)
        setStatus(changes.current === mine ? 'failed' : 'waiting')
      }
    }, 800)
    return () => window.clearTimeout(timer)
  }, [status, s, canSave, refreshContext])

  // Leaving the page within that moment still saves the last change.
  useEffect(() => () => {
    if (unsaved.current && latest.current) void api.put('/settings', latest.current).catch(() => undefined)
  }, [])

  const groupName = isSuperAdmin ? groups.find((g) => g.id === groupId)?.name : undefined

  return (
    <>
      <div className="page-head">
        <h1>Group settings</h1>
        {groupName && <span className="muted">{groupName}</span>}
      </div>
      {isSuperAdmin && !groupId && <Alert kind="warn">Open a group with "Impersonate" on Investment Groups first.</Alert>}
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {s && (
        <p className="muted small" role="status" style={{ marginTop: 0 }}>
          Changes are saved as you make them.{' '}
          {status === 'waiting' || status === 'saving' ? 'Saving…'
            : status === 'saved' ? <strong>Saved.</strong>
              : status === 'failed' ? <strong style={{ color: 'var(--danger)' }}>The last change was not saved; see the message above and change it again.</strong>
                : s.updatedAt ? `Last saved ${fmtDateTime(s.updatedAt)} by ${s.updatedBy}.` : ''}
        </p>
      )}
      {!s ? (
        <Spinner />
      ) : (
        <form onSubmit={(e) => e.preventDefault()}>
          <GroupRulesPanel />
          <div className="grid grid-2">
            <div className="panel">
              <h2>The card</h2>
              <ListEditor label="Coupon offers" items={s.couponOffers} onChange={(v) => change({ ...s, couponOffers: v })} placeholder="Free Value Basket" hint='Choices for the "Please enjoy ___" line.' />
              <br />
              <Field label="Follow up with the guest after (days)" hint="Days after a coupon card is sent before managers and owners are reminded to check it arrived. Each restaurant's owners can choose their own; 0 turns reminders off.">
                <input type="number" min={0} max={90} value={s.followUpDays} onChange={(e) => change({ ...s, followUpDays: Number(e.target.value) })} />
              </Field>
              <br />
              <Field label="Coupon good for (months)" hint="Printed on the card as “good for 3 months from date of issue”.">
                <input type="number" min={1} max={24} value={s.couponValidityMonths} onChange={(e) => change({ ...s, couponValidityMonths: Number(e.target.value) })} />
              </Field>
            </div>
            <div className="panel">
              <h2>Reasons</h2>
              <ListEditor label="Reason categories" items={s.reasonCategories} onChange={(v) => change({ ...s, reasonCategories: v })} placeholder="Order mistake" hint="Why the guest received a card. Drives the dropdown and the analytics charts." />
              <br />
              <ListEditor label="Reply methods" items={s.replyMethods ?? []} onChange={(v) => change({ ...s, replyMethods: v })} placeholder="Phone call" hint="How managers can reply to a guest. They pick one when they confirm a reply or log an attempt." />
            </div>
            <div className="panel">
              <h2>Duplicate &amp; repeat guest detection</h2>
              <div className="grid">
                <Field label="Duplicate window (days)" hint="A second card for the same guest within this many days of the visit is flagged as a likely duplicate entry. 0 only flags the exact same visit.">
                  <input type="number" min={0} max={365} value={s.duplicateWindowDays} onChange={(e) => change({ ...s, duplicateWindowDays: Number(e.target.value) })} />
                </Field>
                <Field label="Repeat guest alert at (cards)" hint="Show the repeat-guest alert once a guest has this many cards on file, including the new one.">
                  <input type="number" min={2} max={100} value={s.repeatGuestAlertThreshold} onChange={(e) => change({ ...s, repeatGuestAlertThreshold: Number(e.target.value) })} />
                </Field>
                <label className="check">
                  <input type="checkbox" checked={s.requirePhoneOrAddress} onChange={(e) => change({ ...s, requirePhoneOrAddress: e.target.checked })} />
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
                    <input type="color" value={s.accentColor} onChange={(e) => change({ ...s, accentColor: e.target.value })} style={{ width: 56, padding: 2 }} />
                    <input value={s.accentColor} onChange={(e) => change({ ...s, accentColor: e.target.value })} pattern="#[0-9a-fA-F]{6}" />
                  </div>
                </Field>
              </div>
            </div>
          </div>
          <PostcardSettingsEditor
            settings={s}
            dirty={status === 'waiting' || status === 'saving'}
            onChange={(postcard) => change({ ...s, postcard })}
          />
        </form>
      )}
      {s && (
        <>
          <PostageAccountPanel />
        </>
      )}
    </>
  )
}
