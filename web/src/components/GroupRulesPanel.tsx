import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { GroupRules } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Field } from './ui'

const BY_SITE = 'Decided for your investment group by the people who run the site, so it cannot be changed here.'

/**
 * The settings an investment group's administrators make for all their restaurants, or leave to each restaurant.
 * A setting the people who run the site have decided for the group is shown, but cannot be changed. Each change is
 * saved as it is made.
 */
export default function GroupRulesPanel() {
  const { groupId, refreshContext } = useAuth()
  const [r, setR] = useState<GroupRules | null>(null)
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved'>('idle')

  useEffect(() => {
    setR(null)
    api.get<GroupRules>('/group-rules').then((x) => { setR(x); setName(x.signatureForAll ?? '') }).catch(() => setR(null))
  }, [groupId])

  if (!r) return null
  const mine = (key: string) => r.groupDecides.includes(key)
  const siteFixesMode = r.siteMailMode !== 'each'
  const oneSignature = r.signatureForAll != null
  const save = async (body: Record<string, string | null>) => {
    setStatus('saving')
    setError(null)
    try {
      const next = await api.put<GroupRules>('/group-rules', body)
      setR(next)
      setName(next.signatureForAll ?? '')
      setStatus('saved')
      void refreshContext()
    } catch (e) {
      setError(`Not saved: ${(e as Error).message}`)
      setStatus('idle')
    }
  }
  const busy = status === 'saving'
  const fixed = (text: string, hint = BY_SITE) => <><input readOnly value={text} /><span className="muted small" style={{ display: 'block' }}>{hint}</span></>

  return (
    <div className="panel">
      <h2>For all restaurants, or each restaurant decides</h2>
      <p className="muted">
        What you set for all restaurants takes precedence over a restaurant’s own choice. Where each restaurant decides, its owners choose on Restaurants &amp; Staff
        Assignments (Edit). {status === 'saving' ? 'Saving…' : status === 'saved' ? <strong>Saved.</strong> : ''}
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="grid grid-2">
        <Field label="Having cards printed and mailed" hint={mine('mail') ? 'Until this is on for a restaurant, its people see “Mailed for you” greyed out on the print screens.' : undefined}>
          {mine('mail') ? (
            <select value={r.mail} disabled={busy} onChange={(e) => void save({ mail: e.target.value })}>
              <option value="on">On for all restaurants</option>
              <option value="off">Off for all restaurants</option>
              <option value="each">Each restaurant decides</option>
            </select>
          ) : fixed(r.mail === 'on' ? 'On for all restaurants' : r.mail === 'off' ? 'Off for all restaurants' : 'Each restaurant decides')}
        </Field>
        <Field label="Real or test mailing" hint={!siteFixesMode && mine('mode') ? 'Real: cards are printed, mailed and billed. Test: a proof of each card is made, and nothing is printed, mailed or billed.' : undefined}>
          {siteFixesMode ? fixed(r.siteMailMode === 'live' ? 'Real mailing' : 'Test mailing', 'Decided for every investment group by the people who run the site, so it cannot be changed here.')
            : mine('mode') ? (
              <select value={r.mailMode} disabled={busy} onChange={(e) => void save({ mailMode: e.target.value })}>
                <option value="live">Real mailing for all restaurants</option>
                <option value="test">Test mailing for all restaurants</option>
                <option value="each">Each restaurant decides</option>
              </select>
            ) : fixed(r.mailMode === 'live' ? 'Real mailing for all restaurants' : r.mailMode === 'test' ? 'Test mailing for all restaurants' : 'Each restaurant decides')}
        </Field>
        <Field label="How cards usually go out" hint={mine('usual') ? 'What the print screens start on. Whoever is printing can still pick another way.' : undefined}>
          {mine('usual') ? (
            <select value={r.usualWay} disabled={busy} onChange={(e) => void save({ usualWay: e.target.value })}>
              <option value="mail">Mailed for you, for all restaurants</option>
              <option value="stamp">Stamps by hand, for all restaurants</option>
              <option value="each">Each restaurant decides</option>
            </select>
          ) : fixed(r.usualWay === 'mail' ? 'Mailed for you, for all restaurants' : r.usualWay === 'stamp' ? 'Stamps by hand, for all restaurants' : 'Each restaurant decides')}
        </Field>
        <Field label="Name that signs cards entered with the team login" hint={mine('signature') ? 'Managers, owners and administrators sign with their own name. This is for the shared team login.' : undefined}>
          {mine('signature') ? (
            <>
              <select value={oneSignature ? 'all' : 'each'} disabled={busy}
                onChange={(e) => (e.target.value === 'each' ? void save({ signature: 'each' }) : setR({ ...r, signatureForAll: name }))}>
                <option value="all">One name for all restaurants</option>
                <option value="each">Each restaurant decides</option>
              </select>
              {oneSignature && (
                <input value={name} maxLength={60} disabled={busy} aria-label="The name that signs, for all restaurants" placeholder="For example: Michael Smith" style={{ marginTop: '0.4rem' }}
                  onChange={(e) => setName(e.target.value)}
                  onBlur={() => { if (name.trim() && name.trim() !== (r.signatureForAll ?? '')) void save({ signature: 'all', signatureForAll: name.trim() }) }} />
              )}
            </>
          ) : fixed(oneSignature ? `${r.signatureForAll}, for all restaurants` : 'Each restaurant decides')}
        </Field>
      </div>
    </div>
  )
}
