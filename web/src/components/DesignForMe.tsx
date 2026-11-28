import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { CardDesignFields, CardDesignHelp, CardDesignSuggestion, CardDesignSuggestions, MailPreview } from '../api/types'
import { Side } from '../pages/MailCardsPage'
import { Alert, Badge, Field, Spinner } from './ui'

const TONES = ['Warm and personal', 'Simple and classic', 'Bold and modern', 'Friendly and playful', 'Like the pictures I uploaded']
const MOST = 3
const MAX_BYTES = 5 * 1024 * 1024

/**
 * "Design it for me" on Card Design, shown to whoever may change the card, where the site offers it. The person
 * says how the card should feel, can upload up to three pictures to model it after (their present card, a menu,
 * the storefront, anything with the look they want), and gets three designs drawn as they would print. Choosing
 * one loads it into the designer below, where it is checked, adjusted and saved like any other change: nothing is
 * saved from here. It is free, up to the group's number of tries; a try that fails is not counted. The pictures
 * uploaded to model after are sent to the assistant and not kept.
 */
export default function DesignForMe({ scope, hasLogo, hasPhoto, onUse }: { scope: string; hasLogo: boolean; hasPhoto: boolean; onUse: (fields: CardDesignFields, name: string) => void }) {
  const [help, setHelp] = useState<CardDesignHelp | null>(null)
  const [tone, setTone] = useState(TONES[0])
  const [wishes, setWishes] = useState('')
  const [files, setFiles] = useState<File[]>([])
  const [options, setOptions] = useState<CardDesignSuggestion[] | null>(null)
  const [previews, setPreviews] = useState<Record<number, MailPreview>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const picker = useRef<HTMLInputElement>(null)

  useEffect(() => {
    setHelp(null)
    setOptions(null)
    setPreviews({})
    api.get<CardDesignHelp>(`/card-designs/${scope}/suggest`).then(setHelp).catch(() => setHelp(null))
  }, [scope])

  // Hidden where it is not offered (or while that is not known yet).
  if (!help?.available) return null
  const left = Math.max(0, help.allowed - help.used)

  const pick = (list: FileList | null) => {
    const chosen = [...(list ?? [])]
    const wrong = chosen.find((f) => !/^image\/(jpeg|png|webp|gif)$/.test(f.type) || f.size > MAX_BYTES)
    if (wrong) return setError(`“${wrong.name}” cannot be used: pictures must be JPEG, PNG, WebP or GIF, up to 5 MB each.`)
    setError(null)
    setFiles((now) => [...now, ...chosen].slice(0, MOST))
    if (picker.current) picker.current.value = ''
  }

  const design = async () => {
    setBusy(true)
    setError(null)
    try {
      const form = new FormData()
      // A key of its own for each press: if the same press is sent twice it is answered once and uses one try.
      form.append('requestKey', crypto.randomUUID())
      form.append('tone', tone)
      form.append('wishes', wishes)
      files.forEach((f) => form.append('pictures', f))
      const answer = await api.post<CardDesignSuggestions>(`/card-designs/${scope}/suggest`, form)
      setOptions(answer.options)
      setHelp({ available: true, used: answer.used, allowed: answer.allowed })
      setPreviews({})
      // Each design is drawn exactly as it would print, by the same preview the designer uses.
      answer.options.forEach((o, i) => {
        api.post<MailPreview>(`/card-designs/${scope}/preview`, o.fields).then((p) => setPreviews((all) => ({ ...all, [i]: p }))).catch(() => undefined)
      })
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel ai-panel">
      <h2>Design it for me <Badge kind="ok">free</Badge></h2>
      <p className="muted">
        Say how the card should feel and the assistant offers three designs, using {hasLogo ? 'your logo' : 'your restaurant’s name'}{hasPhoto ? ' and your photo' : ''}. It chooses a
        layout, colours and words; it does not make pictures. Pick one and it is loaded into the designer below for you to check, change and save.
        {!hasLogo && ' Upload your logo below first if you have one: the designs take their colours from it.'}
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="form-row">
        <Field label="How should it feel?">
          <select value={tone} onChange={(e) => setTone(e.target.value)} disabled={busy}>
            {TONES.map((t) => <option key={t}>{t}</option>)}
          </select>
        </Field>
        <Field label="Anything else? (optional)" hint="e.g. Mention we are family-owned. Keep it short.">
          <input value={wishes} maxLength={600} onChange={(e) => setWishes(e.target.value)} disabled={busy} data-no-track />
        </Field>
      </div>
      <Field label={`Pictures to model it after (optional, up to ${MOST})`} hint="Your present card, a menu, your storefront, or anything with the look you want. They are shown to the assistant and not kept.">
        <div className="row-actions">
          <input ref={picker} type="file" accept="image/jpeg,image/png,image/webp,image/gif" multiple hidden onChange={(e) => pick(e.target.files)} />
          <button type="button" className="btn btn-sm" onClick={() => picker.current?.click()} disabled={busy || files.length >= MOST}>Choose pictures…</button>
          {files.map((f, i) => (
            <span key={`${f.name}-${i}`} className="chip">
              {f.name.length > 28 ? `${f.name.slice(0, 26)}…` : f.name}
              <button type="button" className="btn-icon" onClick={() => setFiles((now) => now.filter((_, j) => j !== i))} disabled={busy} aria-label="Remove this picture">×</button>
            </span>
          ))}
        </div>
      </Field>
      <div className="row-actions" style={{ marginTop: '0.5rem' }}>
        <button type="button" className="btn btn-primary" onClick={() => void design()} disabled={busy || left === 0} aria-label="Design the card for me, free">
          {busy ? 'Designing… this takes about half a minute' : options ? 'Design three more' : 'Design it for me'}
        </button>
        <span className="muted small">
          {left === 0 ? `Your investment group has used its ${help.allowed} free designs. Ask the site’s administrator for more.` : `Free. ${left} of ${help.allowed} tries left for your investment group.`}
        </span>
      </div>

      {options && (
        <div className="design-options">
          {options.map((o, i) => (
            <div key={`${o.name}-${i}`} className="design-option">
              <strong>{i + 1}. {o.name}</strong>
              <p className="muted small" style={{ margin: '0.15rem 0 0.5rem' }}>{o.why}</p>
              {previews[i] ? (
                <div style={{ display: 'grid', gap: '0.4rem' }}>
                  <Side html={previews[i].frontHtml} label="Front" scale={0.46} />
                  <Side html={previews[i].backHtml} label="Back" scale={0.46} />
                </div>
              ) : <Spinner label="Drawing…" />}
              <button type="button" className="btn btn-primary btn-sm" style={{ marginTop: '0.5rem' }} onClick={() => onUse(o.fields, o.name)} aria-label={`Use design ${i + 1}, ${o.name}`}>
                Use this design
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
