import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { CardBack, CardDesign, CardDesignFields, CardDesignScope, CardFront, CardImage, CardImageKind, CardLayout, MailPreview } from '../api/types'
import CardLayoutEditor from '../components/CardLayoutEditor'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from '../components/ui'
import { Side } from './MailCardsPage'

const IDEAL_W = 1875
const IDEAL_H = 1275

const FRONTS: { value: CardFront; label: string; hint: string }[] = [
  { value: 'plain', label: 'Plain', hint: 'The restaurant’s name and a thank-you on your group’s colour. Nothing to set up.' },
  { value: 'designed', label: 'Design it here', hint: 'Pick a layout, add your logo, a photo, your own words and colours.' },
  { value: 'picture', label: 'Your own artwork', hint: 'Upload a finished picture of the whole front, made in a design program.' },
  { value: 'custom', label: 'Lay it out yourself', hint: 'Place words, pictures, lines and boxes anywhere on the card, and move or resize each one.' },
]
const BACKS: { value: CardBack; label: string; hint: string }[] = [
  { value: 'classic', label: 'Classic coupon card', hint: 'Like your printed cards: logo and address across the top, “Please enjoy”, date issued and authorized signature on lines, small print and web address at the bottom.' },
  { value: 'simple', label: 'Simple', hint: 'A plain coupon box with the restaurant’s name, the offer, the dates and the small print.' },
  { value: 'custom', label: 'Lay it out yourself', hint: 'Starts as the classic card. Move, resize, reword, add or remove anything on it, with your own pictures.' },
]
const LAYOUTS: { value: CardLayout; label: string; hint: string }[] = [
  { value: 'centered', label: 'Words in the middle', hint: 'Logo, headline and message centred on your colour.' },
  { value: 'photo', label: 'Photo with a band', hint: 'Your photo fills the card; the words sit in a band along the bottom.' },
  { value: 'split', label: 'Photo beside words', hint: 'Your photo on the left, the words on your colour on the right.' },
]

/** The picture's size in pixels, read in the browser before uploading. */
function pixels(file: File): Promise<{ w: number; h: number } | null> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file)
    const img = new Image()
    img.onload = () => { resolve({ w: img.naturalWidth, h: img.naturalHeight }); URL.revokeObjectURL(url) }
    img.onerror = () => { resolve(null); URL.revokeObjectURL(url) }
    img.src = url
  })
}

const fieldsOf = (d: CardDesign): CardDesignFields => ({
  front: d.front, layout: d.layout, headline: d.headline ?? '', message: d.message ?? '', backMessage: d.backMessage ?? '', backgroundColor: d.backgroundColor, textColor: d.textColor,
  backStyle: d.backStyle, inkColor: d.inkColor, website: d.website ?? '', legalText: d.legalText ?? '', backLayout: d.backLayout ?? null, frontLayout: d.frontLayout ?? null,
})

/**
 * Card design: how the cards that are printed and mailed for the group look. The group has one card for every
 * restaurant; a restaurant can have its own instead. Both sides are shown exactly as they will be printed, and
 * change as you type. Group administrators change any card; an owner changes their own restaurants'.
 */
export default function CardDesignPage() {
  const [scopes, setScopes] = useState<CardDesignScope[] | null>(null)
  const [scope, setScope] = useState('')
  const [design, setDesign] = useState<CardDesign | null>(null)
  const [draft, setDraft] = useState<CardDesignFields | null>(null)
  const [preview, setPreview] = useState<MailPreview | null>(null)
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [warn, setWarn] = useState<string | null>(null)
  const [removing, setRemoving] = useState(false)
  const [pendingScope, setPendingScope] = useState<string | null>(null)

  const loadScopes = (pick?: string) =>
    api.get<CardDesignScope[]>('/card-designs').then((list) => {
      setScopes(list)
      setScope((s) => pick ?? (s || (list.find((x) => x.canEdit) ?? list[0])?.scope || ''))
    }).catch((e) => setError((e as Error).message))
  useEffect(() => { void loadScopes() }, [])

  useEffect(() => {
    if (!scope) return
    setDesign(null)
    setPreview(null)
    api.get<CardDesign>(`/card-designs/${scope}`).then((d) => { setDesign(d); setDraft(fieldsOf(d)); setDirty(false) }).catch((e) => setError((e as Error).message))
  }, [scope])

  // Redraw both sides a moment after the last change, from what is on screen (saved or not).
  const imagesKey = design?.images.map((i) => i.url).join('|') ?? ''
  useEffect(() => {
    if (!draft || !scope) return
    const timer = window.setTimeout(() => {
      api.post<MailPreview>(`/card-designs/${scope}/preview`, draft).then(setPreview).catch((e) => setError((e as Error).message))
    }, 350)
    return () => window.clearTimeout(timer)
  }, [draft, scope, imagesKey])

  if (!scopes) return error ? <Alert kind="error">{error}</Alert> : <Spinner />
  const current = scopes.find((s) => s.scope === scope)
  const canEdit = design?.canEdit ?? false
  const isGroup = scope === 'group'
  const set = <K extends keyof CardDesignFields>(k: K, v: CardDesignFields[K]) => {
    setDraft((d) => (d ? { ...d, [k]: v } : d))
    setDirty(true)
    setNotice(null)
  }
  // Choosing to lay a side out yourself starts it from the classic back (or a starter front), unless one is already laid out.
  const chooseBack = (v: CardBack) => {
    if (v === 'custom' && !draft?.backLayout?.length && design) set('backLayout', design.standardBack)
    set('backStyle', v)
  }
  const chooseFront = (v: CardFront) => {
    if (v === 'custom' && !draft?.frontLayout?.length && design) set('frontLayout', design.starterFront)
    set('front', v)
  }
  // The designer shows the fields as they print: the web address, small print and note come from what is on screen now.
  const mergeFields = (design?.mergeFields ?? []).map((f) =>
    f.key === 'website' ? { ...f, sample: (draft?.website ?? '').trim() }
      : f.key === 'legal' ? { ...f, sample: (draft?.legalText ?? '').trim() || design?.defaultLegalText || '' }
        : f.key === 'note' ? { ...f, sample: (draft?.backMessage ?? '').trim() } : f)
  const image = (kind: CardImageKind) => design?.images.find((i) => i.kind === kind && !i.fromGroup)
  const inherited = (kind: CardImageKind) => design?.images.find((i) => i.kind === kind && i.fromGroup)

  const run = async (what: string, work: () => Promise<void>) => {
    setBusy(what)
    setError(null)
    setNotice(null)
    setWarn(null)
    try {
      await work()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }

  const save = () => run('save', async () => {
    if (!draft) return
    const d = await api.put<CardDesign>(`/card-designs/${scope}`, draft)
    setDesign(d)
    setDraft(fieldsOf(d))
    setDirty(false)
    setNotice(isGroup ? 'Saved. Every restaurant without its own card uses this from now on.' : `Saved. ${d.name}’s cards use this from now on.`)
    void loadScopes(scope)
  })

  const upload = (kind: CardImageKind, file: File) => run(`upload-${kind}`, async () => {
    if (!draft) return
    if (!/^image\/(png|jpeg)$/.test(file.type)) throw new Error('Choose a PNG or JPEG picture.')
    if (file.size > 5 * 1024 * 1024) throw new Error('The picture is larger than 5 MB. Save it as a JPEG, or at a smaller size, and try again.')
    const size = await pixels(file)
    if (!size) throw new Error('That file could not be read as a picture.')
    let note: string | null = null
    if (kind === 'picture') {
      const ratio = size.w / size.h
      if (Math.abs(ratio - IDEAL_W / IDEAL_H) > 0.03) note = `This picture is ${size.w} × ${size.h} pixels, which is not the card’s shape (${IDEAL_W} × ${IDEAL_H}). It is cropped to fit, so the edges may be cut off.`
      else if (size.w < IDEAL_W * 0.9) note = `This picture is ${size.w} × ${size.h} pixels. It will print, but may look soft: ${IDEAL_W} × ${IDEAL_H} prints sharply.`
    } else if (kind === 'photo' && size.w < 1200) note = `This photo is only ${size.w} pixels wide and may look soft when printed. 1800 pixels or wider prints sharply.`
    // The words and colours are saved first, so the picture belongs to the design on screen.
    await api.put<CardDesign>(`/card-designs/${scope}`, draft)
    const d = await api.upload<CardDesign>(`/card-designs/${scope}/images/${kind}`, file)
    setDesign(d)
    setDraft(fieldsOf(d))
    setDirty(false)
    setWarn(note)
    setNotice('Picture uploaded and the design saved.')
    void loadScopes(scope)
  })

  const removeImage = (kind: CardImageKind) => run(`remove-${kind}`, async () => {
    setDesign(await api.delete<CardDesign>(`/card-designs/${scope}/images/${kind}`))
  })

  const removeDesign = () => run('remove', async () => {
    const d = await api.delete<CardDesign>(`/card-designs/${scope}`)
    setDesign(d)
    setDraft(fieldsOf(d))
    setDirty(false)
    setRemoving(false)
    setNotice(isGroup ? 'The group’s card is back to the plain card.' : `${d.name} uses the group’s card again.`)
    void loadScopes(scope)
  })

  const choose = (next: string) => (dirty ? setPendingScope(next) : setScope(next))

  return (
    <>
      <div className="page-head">
        <h1>Card design</h1>
      </div>
      <p className="muted">
        How the coupon cards look when they are printed and mailed for you (6 × 4 inch postcards). The group has one card for every restaurant; a restaurant
        can have its own instead. Cards you print yourself use the layout in Settings, not this.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {warn && <Alert kind="warn" onClose={() => setWarn(null)}>{warn}</Alert>}

      <div className="panel">
        <Field label="Whose card">
          <select value={scope} onChange={(e) => choose(e.target.value)} aria-label="Whose card to design">
            {scopes.map((s) => (
              <option key={s.scope} value={s.scope}>
                {s.name}{s.scope !== 'group' ? (s.hasOwn ? ' (its own card)' : ' (uses the group’s card)') : ''}
              </option>
            ))}
          </select>
        </Field>
        {design && !isGroup && !design.hasOwn && (
          <p className="muted" style={{ marginBottom: 0 }}>
            <Badge kind="neutral">uses the group’s card</Badge> {canEdit ? 'Change anything below and save to give this restaurant its own card.' : ''}
          </p>
        )}
        {design && !isGroup && design.hasOwn && (
          <p className="muted" style={{ marginBottom: 0 }}>
            <Badge kind="accent">its own card</Badge> Last changed {design.updatedAt ? fmtDateTime(design.updatedAt) : ''}{design.updatedBy ? ` by ${design.updatedBy}` : ''}.
          </p>
        )}
        {design && !canEdit && (
          <Alert kind="info">
            {isGroup ? 'Only the investment group’s administrators can change the group’s card.' : 'Only this restaurant’s owners and the group’s administrators can change its card.'} You can
            see how it looks below.
          </Alert>
        )}
      </div>

      {!design || !draft ? <Spinner /> : (
        <>
          <div className="panel">
            <h2>How it will print</h2>
            <p className="muted">
              Both sides of a sample card for {current?.restaurantId ? current.name : 'one of your restaurants'}. The guest’s address, the return address and the
              postage are added in the blank area on the right of the back.{dirty && ' Not saved yet.'}
            </p>
            {preview ? (
              <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap' }}>
                <Side html={preview.frontHtml} label="Front" scale={0.8} />
                <Side html={preview.backHtml} label="Back (the shaded areas are printed over when it is mailed)" scale={0.8} mailZones />
              </div>
            ) : <Spinner label="Drawing the card…" />}
          </div>

          <fieldset disabled={!canEdit || busy !== null} style={{ border: 0, padding: 0, margin: 0, minWidth: 0 }}>
            <div className="panel">
              <h2>The front</h2>
              <div className="source-choice two" role="radiogroup" aria-label="How the front is made">
                {FRONTS.map((f) => (
                  <label key={f.value} className={draft.front === f.value ? 'selected' : ''}>
                    <input type="radio" name="front" checked={draft.front === f.value} onChange={() => chooseFront(f.value)} />
                    <span className="source-label">{f.label}</span>
                    <span className="source-hint">{f.hint}</span>
                  </label>
                ))}
              </div>

              {draft.front === 'designed' && (
                <div className="grid" style={{ marginTop: '1rem' }}>
                  <div className="source-choice" role="radiogroup" aria-label="Layout">
                    {LAYOUTS.map((l) => (
                      <label key={l.value} className={draft.layout === l.value ? 'selected' : ''}>
                        <input type="radio" name="layout" checked={draft.layout === l.value} onChange={() => set('layout', l.value)} />
                        <span className="source-label">{l.label}</span>
                        <span className="source-hint">{l.hint}</span>
                      </label>
                    ))}
                  </div>
                  <div className="grid grid-2">
                    <Field label="Headline" hint={`Up to 60 characters. Empty prints “Thank you!”. ${(draft.headline ?? '').length}/60`}>
                      <input value={draft.headline ?? ''} maxLength={60} onChange={(e) => set('headline', e.target.value)} placeholder="Thank you!" />
                    </Field>
                    <Field label="Message" hint={`Up to 220 characters. ${(draft.message ?? '').length}/220`}>
                      <textarea rows={2} value={draft.message ?? ''} maxLength={220} onChange={(e) => set('message', e.target.value)} placeholder="We appreciate you letting us know about your visit." />
                    </Field>
                  </div>
                  <div className="grid grid-4">
                    <Field label="Background colour">
                      <input type="color" style={{ width: 72, height: 38, padding: 3 }} value={draft.backgroundColor ?? '#005daa'} onChange={(e) => set('backgroundColor', e.target.value)} aria-label="Background colour" />
                    </Field>
                    <Field label="Colour of the words">
                      <input type="color" style={{ width: 72, height: 38, padding: 3 }} value={draft.textColor ?? '#ffffff'} onChange={(e) => set('textColor', e.target.value)} aria-label="Colour of the words" />
                    </Field>
                  </div>
                  <div className="grid grid-2">
                    <ImageSlot kind="logo" label="Logo" hint="PNG with a clear background works best. Up to 5 MB." image={image('logo')} inherited={inherited('logo')}
                      busy={busy} canEdit={canEdit} onUpload={upload} onRemove={removeImage} />
                    <ImageSlot kind="photo" label="Photo" hint={draft.layout === 'centered' ? 'Not used by the “Words in the middle” layout.' : '1800 pixels or wider prints sharply. Up to 5 MB.'}
                      image={image('photo')} inherited={inherited('photo')} busy={busy} canEdit={canEdit} onUpload={upload} onRemove={removeImage} />
                  </div>
                  {draft.layout !== 'centered' && !image('photo') && !inherited('photo') && (
                    <Alert kind="info">This layout needs a photo. Until one is uploaded, the card prints with the words in the middle.</Alert>
                  )}
                </div>
              )}

              {draft.front === 'custom' && (
                <div className="grid" style={{ marginTop: '1rem' }}>
                  <div className="grid grid-4">
                    <Field label="Background colour">
                      <input type="color" style={{ width: 72, height: 38, padding: 3 }} value={draft.backgroundColor ?? '#005daa'} onChange={(e) => set('backgroundColor', e.target.value)} aria-label="Background colour of the front" />
                    </Field>
                    <Field label="Colour of the words">
                      <input type="color" style={{ width: 72, height: 38, padding: 3 }} value={draft.textColor ?? '#ffffff'} onChange={(e) => set('textColor', e.target.value)} aria-label="Colour of the words on the front" />
                    </Field>
                  </div>
                  <CardLayoutEditor side="front" elements={draft.frontLayout ?? []} onChange={(next) => set('frontLayout', next)} ink={draft.textColor ?? '#ffffff'}
                    background={draft.backgroundColor ?? '#005daa'} images={design.images} mergeFields={mergeFields} sampleQr={design.sampleQr} canEdit={canEdit} busy={busy !== null}
                    onUploadPicture={upload} onStartAgain={() => set('frontLayout', design.starterFront)} startAgainLabel="Start again from the starter front" />
                </div>
              )}

              {draft.front === 'picture' && (
                <div style={{ marginTop: '1rem' }}>
                  <ImageSlot kind="picture" label="Artwork for the whole front"
                    hint={`${IDEAL_W} × ${IDEAL_H} pixels (6¼ × 4¼ inches at 300 dpi, which includes ⅛ inch trimmed off each edge), PNG or JPEG, up to 5 MB. Keep words at least ¼ inch from the edges.`}
                    image={image('picture')} inherited={inherited('picture')} busy={busy} canEdit={canEdit} onUpload={upload} onRemove={removeImage} />
                  {!image('picture') && !inherited('picture') && <Alert kind="info">Until a picture is uploaded, the card prints with the plain front.</Alert>}
                </div>
              )}
            </div>

            <div className="panel">
              <h2>The back</h2>
              <p className="muted">
                The back carries the coupon. The right side is kept clear: the guest’s address and the postage are printed there when the card is mailed, in place
                of the stamp box and address lines on a hand-written card.
              </p>
              <div className="source-choice" role="radiogroup" aria-label="How the back is laid out">
                {BACKS.map((b) => (
                  <label key={b.value} className={draft.backStyle === b.value ? 'selected' : ''}>
                    <input type="radio" name="backStyle" checked={draft.backStyle === b.value} onChange={() => chooseBack(b.value)} />
                    <span className="source-label">{b.label}</span>
                    <span className="source-hint">{b.hint}</span>
                  </label>
                ))}
              </div>

              {draft.backStyle === 'custom' && (
                <div style={{ marginTop: '1rem' }}>
                  <CardLayoutEditor side="back" elements={draft.backLayout ?? []} onChange={(next) => set('backLayout', next)} ink={draft.inkColor ?? '#2b4a8b'} background="#ffffff"
                    images={design.images} mergeFields={mergeFields} sampleQr={design.sampleQr} canEdit={canEdit} busy={busy !== null}
                    onUploadPicture={upload} onStartAgain={() => set('backLayout', design.standardBack)} startAgainLabel="Start again from the classic card" />
                </div>
              )}

              {draft.backStyle !== 'simple' && (
                <div className="grid" style={{ marginTop: '1rem' }}>
                  {draft.backStyle === 'classic' && <div className="grid grid-2">
                    <ImageSlot kind="backlogo" label="Logo for the back" hint="Printed in the top left corner, beside the restaurant’s name and address. A logo in your ink colour on a white or clear background looks best. Up to 5 MB."
                      image={image('backlogo')} inherited={inherited('backlogo')} busy={busy} canEdit={canEdit} onUpload={upload} onRemove={removeImage} />
                    <ImageSlot kind="footer" label="Small picture for the bottom corner" hint="For example a strip of social media icons, printed above the web address. Up to 5 MB."
                      image={image('footer')} inherited={inherited('footer')} busy={busy} canEdit={canEdit} onUpload={upload} onRemove={removeImage} />
                  </div>}
                  <div className="grid grid-2">
                    <Field label="Web address" hint={draft.backStyle === 'custom' ? 'Printed wherever the card has the “Web address” field, for example culvers.com. Leave empty to print none.' : 'Printed under the restaurant’s address and in the bottom corner, for example culvers.com. Leave empty to print none.'}>
                      <input value={draft.website ?? ''} maxLength={60} onChange={(e) => set('website', e.target.value)} placeholder="culvers.com" />
                    </Field>
                    <Field label="Ink colour" hint="The colour of the words and lines.">
                      <input type="color" style={{ width: 72, height: 38, padding: 3 }} value={draft.inkColor ?? '#2b4a8b'} onChange={(e) => set('inkColor', e.target.value)} aria-label="Ink colour" />
                    </Field>
                  </div>
                  <Field label="Small print" hint={`Up to 420 characters. Leave empty to print the standard wording. ${(draft.legalText ?? '').length}/420`}>
                    <textarea rows={3} value={draft.legalText ?? ''} maxLength={420} onChange={(e) => set('legalText', e.target.value)} placeholder={design.defaultLegalText} />
                  </Field>
                  <p className="muted small" style={{ margin: 0 }}>
                    The restaurant’s name and address come from Restaurants &amp; staff, and “good for … months” from Settings. The offer, date issued and the name
                    of whoever authorized the card are printed on the lines for each guest.
                  </p>
                </div>
              )}

              <div style={{ marginTop: '1rem' }}>
                <Field label="A note from the restaurant (optional)" hint={`Up to 160 characters, printed above the coupon. ${(draft.backMessage ?? '').length}/160`}>
                  <input value={draft.backMessage ?? ''} maxLength={160} onChange={(e) => set('backMessage', e.target.value)} placeholder="We’re sorry we missed the mark. Please give us another chance." />
                </Field>
              </div>
            </div>
          </fieldset>

          {canEdit && (
            <div className="row-actions" style={{ marginBottom: '2rem' }}>
              <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={busy !== null || (!dirty && design.hasOwn)}>
                {busy === 'save' ? 'Saving…' : isGroup ? 'Save the group’s card' : design.hasOwn ? 'Save this restaurant’s card' : 'Give this restaurant its own card'}
              </button>
              {dirty && <button type="button" className="btn" onClick={() => { setDraft(fieldsOf(design)); setDirty(false) }} disabled={busy !== null}>Undo changes</button>}
              {design.hasOwn && (
                <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>
                  {isGroup ? 'Back to the plain card' : 'Use the group’s card instead'}
                </button>
              )}
            </div>
          )}
        </>
      )}

      {removing && design && (
        <Confirm
          title={isGroup ? 'Go back to the plain card?' : `Use the group’s card for ${design.name}?`}
          message={isGroup
            ? 'The group’s design and its pictures are removed. Restaurants without their own card get the plain card. Restaurants with their own card are not affected.'
            : 'This restaurant’s own design and its pictures are removed, and its cards use the group’s card.'}
          confirmLabel={isGroup ? 'Remove the design' : 'Use the group’s card'}
          danger
          busy={busy === 'remove'}
          onCancel={() => setRemoving(false)}
          onConfirm={() => void removeDesign()}
        />
      )}
      {pendingScope && (
        <Confirm
          title="Leave without saving?"
          message="The changes to this card have not been saved."
          confirmLabel="Leave without saving"
          danger
          onCancel={() => setPendingScope(null)}
          onConfirm={() => { setScope(pendingScope); setPendingScope(null) }}
        />
      )}
    </>
  )
}

function ImageSlot({ kind, label, hint, image, inherited, busy, canEdit, onUpload, onRemove }: {
  kind: CardImageKind; label: string; hint: string; image?: CardImage; inherited?: CardImage; busy: string | null; canEdit: boolean
  onUpload: (kind: CardImageKind, file: File) => void; onRemove: (kind: CardImageKind) => void
}) {
  const input = useRef<HTMLInputElement>(null)
  const shown = image ?? inherited
  return (
    <div>
      <div className="field-label" style={{ fontWeight: 600, marginBottom: 4 }}>{label}</div>
      {shown && (
        <img src={shown.url} alt={`The ${label.toLowerCase()} on the card`}
          style={{ display: 'block', maxWidth: kind === 'logo' ? 180 : 280, maxHeight: 190, border: '1px solid var(--border)', borderRadius: 4, background: '#f3f4f6', marginBottom: 6 }} />
      )}
      {shown && (
        <div className="muted small" style={{ marginBottom: 6 }}>
          {shown.widthPx && shown.heightPx ? `${shown.widthPx} × ${shown.heightPx} pixels, ` : ''}{Math.round(shown.bytes / 1024)} KB
          {inherited && !image ? ' (from the group’s card)' : ''}
        </div>
      )}
      <input ref={input} type="file" accept="image/png,image/jpeg" hidden aria-label={`Choose a picture for the ${label.toLowerCase()}`}
        onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f) onUpload(kind, f) }} />
      {canEdit && (
        <div className="row-actions">
          <button type="button" className="btn btn-sm" onClick={() => input.current?.click()} disabled={busy !== null}>
            {busy === `upload-${kind}` ? 'Uploading…' : image ? `Replace ${label.toLowerCase()}` : `Upload ${label.toLowerCase()}`}
          </button>
          {image && <button type="button" className="btn btn-sm btn-danger" onClick={() => onRemove(kind)} disabled={busy !== null}>{busy === `remove-${kind}` ? 'Removing…' : `Remove ${label.toLowerCase()}`}</button>}
        </div>
      )}
      <div className="muted small" style={{ marginTop: 4 }}>{hint}</div>
    </div>
  )
}
