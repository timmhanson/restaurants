import { useEffect, useRef, useState } from 'react'
import type { CSSProperties, KeyboardEvent as ReactKeyboardEvent, PointerEvent as ReactPointerEvent, ReactNode } from 'react'
import type { CardElement, CardElementType, CardFont, CardImage, CardMergeField } from '../api/types'
import { Alert, Field } from './ui'

/** The finished card is 6 x 4 inches; a CSS inch is 96 pixels. */
const CARD_W = 6
const CARD_H = 4
const PX_PER_IN = 96
/** Where the mail house prints on the back (inches from the card's top-left corner), as CardElements has them on the server. */
const ADDRESS_FROM_X = 3.645
const BARCODE_FROM_X = 1.375
const BARCODE_FROM_Y = 3.395
/** Printing can shift a little, so words closer than this to the edge may be trimmed. */
const EDGE = 0.125

const FONTS: Record<CardFont, { label: string; css: string }> = {
  sans: { label: 'Plain (like Arial)', css: "'Arimo',Arial,Helvetica,sans-serif" },
  geometric: { label: 'Round and wide (good for names and headings)', css: "'Montserrat','Helvetica Neue',Arial,sans-serif" },
  narrow: { label: 'Narrow (good for small print)', css: "'Roboto Condensed','Arial Narrow',Arial,sans-serif" },
  script: { label: 'Signature (a flowing hand)', css: "'Great Vibes','Snell Roundhand','Brush Script MT',cursive" },
}

/** The same typeface files the printed card carries, so the designer and the print match. */
const FONT_FACES = (
  [
    ['Arimo', 'arimo-latin-400-normal', 400, 'normal'], ['Arimo', 'arimo-latin-700-normal', 700, 'normal'],
    ['Arimo', 'arimo-latin-400-italic', 400, 'italic'], ['Arimo', 'arimo-latin-700-italic', 700, 'italic'],
    ['Montserrat', 'montserrat-latin-500-normal', 400, 'normal'], ['Montserrat', 'montserrat-latin-700-normal', 700, 'normal'],
    ['Montserrat', 'montserrat-latin-500-italic', 400, 'italic'],
    ['Roboto Condensed', 'roboto-condensed-latin-400-normal', 400, 'normal'], ['Roboto Condensed', 'roboto-condensed-latin-700-normal', 700, 'normal'],
    ['Great Vibes', 'great-vibes-latin-400-normal', 400, 'normal'],
  ] as const
).map(([family, file, weight, style]) => `@font-face{font-family:'${family}';font-style:${style};font-weight:${weight};src:url('/card-fonts/${file}.woff2') format('woff2')}`).join('')

const TYPE_NAMES: Record<CardElementType, string> = { text: 'Words', fill: 'Fill-in line', line: 'Line', box: 'Box', image: 'Picture', qr: 'QR code' }
const QR_CAPTION = 'Staff: scan to check'

const round2 = (n: number) => Math.round(n * 100) / 100
const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n))
const newId = () => Math.random().toString(36).slice(2, 10).padEnd(8, '0')

/**
 * The words with their fields filled in, as the server does it: a line that held only fields and comes out empty is
 * dropped (so an address block leaves no gap for a web address that was never set).
 */
export function fillFields(text: string | null | undefined, values: Record<string, string>): string {
  if (!text) return ''
  const lines: string[] = []
  for (const line of text.replace(/\r/g, '').split('\n')) {
    let hadField = false
    const filled = line.replace(/\{([A-Za-z]+)\}/g, (whole, key: string) => {
      if (!(key in values)) return whole
      hadField = true
      return values[key]
    })
    if (hadField && !filled.trim()) continue
    lines.push(filled)
  }
  return lines.join('\n')
}

function blank(type: CardElementType, n: number): CardElement {
  const base: CardElement = {
    id: newId(), type, x: 0.3, y: round2(0.3 + (n % 8) * 0.12), w: 2, h: 0, text: null, value: null, font: 'sans', size: 10, bold: false, italic: false, lineUnderLabel: false,
    align: 'left', color: 'ink', lineHeight: 1.2, thickness: 0.75, image: null,
  }
  if (type === 'text') return { ...base, text: 'Your words here' }
  if (type === 'fill') return { ...base, w: 3, text: 'Label:', value: '{offer}' }
  if (type === 'line') return { ...base, w: 2 }
  if (type === 'box') return { ...base, w: 1.5, h: 0.8 }
  if (type === 'image') return { ...base, w: 1.2, h: 0.6, image: `el-${newId().slice(0, 6)}` }
  return { ...base, w: 0.6, h: 0.6 }
}

/** Roughly how tall an item prints, for warning about the areas the mail house prints over. */
function heightOf(el: CardElement, values: Record<string, string>): number {
  if (el.type === 'image' || el.type === 'box') return Math.max(el.h, 0.05)
  if (el.type === 'qr') return el.w
  if (el.type === 'line') return 0.02
  const lines = el.type === 'fill' ? 1 : Math.max(1, fillFields(el.text, values).split('\n').length)
  return (lines * el.size * el.lineHeight) / 72
}

/**
 * The card designer: one side of the card at its real proportions, where anything can be picked up and moved,
 * resized, reworded, added or removed. Words can carry fields such as {restaurant} or {offer}, which are filled
 * in for each card, so one design serves every restaurant in the group. What is drawn here is what the server
 * sends to be printed (CardElements.Render): same positions, same typefaces.
 */
export default function CardLayoutEditor({
  side, elements, onChange, ink, background, images, mergeFields, sampleQr, canEdit, busy, onUploadPicture, onStartAgain, startAgainLabel,
}: {
  side: 'back' | 'front'
  elements: CardElement[]
  onChange: (next: CardElement[]) => void
  /** What "the card's ink colour" is on this side. */
  ink: string
  background: string
  images: CardImage[]
  mergeFields: CardMergeField[]
  sampleQr: string
  canEdit: boolean
  busy: boolean
  onUploadPicture: (kind: string, file: File) => void
  onStartAgain: () => void
  startAgainLabel: string
}) {
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [scale, setScale] = useState(1.4)
  const holder = useRef<HTMLDivElement>(null)
  const canvas = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const latest = useRef(elements)
  latest.current = elements
  const history = useRef<CardElement[][]>([])
  const lastSnapshot = useRef(0)
  const [canUndo, setCanUndo] = useState(false)

  const values = Object.fromEntries(mergeFields.map((f) => [f.key, f.sample]))
  const selected = elements.find((e) => e.id === selectedId) ?? null
  const hasQr = elements.some((e) => e.type === 'qr')

  // The card is drawn as large as the page allows, up to about one and three-quarter times life size.
  useEffect(() => {
    const el = holder.current
    if (!el) return
    const fit = () => setScale(clamp(el.clientWidth / (CARD_W * PX_PER_IN), 0.5, 1.75))
    fit()
    const watch = new ResizeObserver(fit)
    watch.observe(el)
    return () => watch.disconnect()
  }, [])

  /** Remembers the card as it is, so the next change can be undone. Typing makes one step a second at most. */
  const snapshot = (always = true) => {
    const now = Date.now()
    if (!always && now - lastSnapshot.current < 1000) return
    lastSnapshot.current = now
    history.current = [...history.current.slice(-49), latest.current]
    setCanUndo(true)
  }
  const undo = () => {
    const before = history.current.pop()
    setCanUndo(history.current.length > 0)
    if (before) onChange(before)
  }

  const patch = (id: string, change: Partial<CardElement>) => onChange(latest.current.map((e) => (e.id === id ? { ...e, ...change } : e)))
  const edit = (change: Partial<CardElement>) => {
    if (!selected) return
    snapshot(false)
    patch(selected.id, change)
  }

  const add = (type: CardElementType) => {
    snapshot()
    const el = blank(type, latest.current.length)
    onChange([...latest.current, el])
    setSelectedId(el.id)
  }
  const remove = (id: string) => {
    snapshot()
    onChange(latest.current.filter((e) => e.id !== id))
    setSelectedId(null)
  }
  const duplicate = (el: CardElement) => {
    snapshot()
    const copy = { ...el, id: newId(), x: clamp(round2(el.x + 0.15), 0, CARD_W - 0.05), y: clamp(round2(el.y + 0.15), 0, CARD_H - 0.05) }
    onChange([...latest.current, copy])
    setSelectedId(copy.id)
  }
  const reorder = (id: string, toFront: boolean) => {
    snapshot()
    const el = latest.current.find((e) => e.id === id)
    if (!el) return
    const rest = latest.current.filter((e) => e.id !== id)
    onChange(toFront ? [...rest, el] : [el, ...rest])
  }

  /** Picking an item up: move it, drag its right edge to change the width, or its corner to change the size. */
  const drag = (e: ReactPointerEvent, el: CardElement, mode: 'move' | 'width' | 'size') => {
    if (!canEdit || busy || e.button !== 0) return
    e.preventDefault()
    e.stopPropagation()
    setSelectedId(el.id)
    canvas.current?.focus({ preventScroll: true })
    const from = { px: e.clientX, py: e.clientY, x: el.x, y: el.y, w: el.w, h: el.h }
    let moved = false
    const move = (ev: PointerEvent) => {
      const dx = (ev.clientX - from.px) / (PX_PER_IN * scale)
      const dy = (ev.clientY - from.py) / (PX_PER_IN * scale)
      if (!moved) {
        if (Math.abs(dx) + Math.abs(dy) < 0.02) return
        moved = true
        snapshot()
      }
      if (mode === 'move') patch(el.id, { x: clamp(round2(from.x + dx), 0, CARD_W - 0.05), y: clamp(round2(from.y + dy), 0, CARD_H - 0.05) })
      else if (el.type === 'qr') {
        const size = clamp(round2(from.w + Math.max(dx, dy)), 0.5, 2)
        patch(el.id, { w: size, h: size })
      } else if (mode === 'width') patch(el.id, { w: clamp(round2(from.w + dx), 0.1, CARD_W) })
      else patch(el.id, { w: clamp(round2(from.w + dx), 0.1, CARD_W), h: clamp(round2(from.h + dy), 0.05, CARD_H) })
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      window.removeEventListener('pointercancel', up)
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
    window.addEventListener('pointercancel', up)
  }

  const onKey = (e: ReactKeyboardEvent) => {
    if (!canEdit || busy) return
    if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'z') {
      e.preventDefault()
      undo()
      return
    }
    if (!selected) return
    const step = e.shiftKey ? 0.1 : 0.01
    const moves: Record<string, [number, number]> = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }
    if (moves[e.key]) {
      e.preventDefault()
      snapshot(false)
      // Only the direction that moves is rounded, so a nudge up or down leaves a finely set left edge alone.
      const [dx, dy] = moves[e.key]
      patch(selected.id, dx ? { x: clamp(round2(selected.x + dx), 0, CARD_W - 0.05) } : { y: clamp(round2(selected.y + dy), 0, CARD_H - 0.05) })
    } else if (e.key === 'Delete' || e.key === 'Backspace') {
      e.preventDefault()
      remove(selected.id)
    } else if (e.key === 'Escape') setSelectedId(null)
  }

  const colour = (el: CardElement) => (el.color === 'ink' ? ink : el.color)
  const pictureOf = (kind?: string | null) => images.find((i) => i.kind === kind)

  const draw = (el: CardElement): ReactNode => {
    const type: CSSProperties = { fontFamily: FONTS[el.font]?.css ?? FONTS.sans.css, fontSize: `${el.size}pt`, lineHeight: el.lineHeight, color: colour(el) }
    if (el.type === 'text') {
      const words = fillFields(el.text, values)
      if (!words || (words === QR_CAPTION && !hasQr))
        return <div style={{ ...type, fontStyle: 'italic', opacity: 0.55, minHeight: '0.14in' }}>{el.text?.trim() ? `${el.text} (nothing to print on the sample card)` : 'Empty: type some words'}</div>
      return <div style={{ ...type, whiteSpace: 'pre-wrap', overflowWrap: 'break-word', textAlign: el.align, fontWeight: el.bold ? 700 : 400, fontStyle: el.italic ? 'italic' : 'normal' }}>{words}</div>
    }
    if (el.type === 'fill') {
      const rule = `${el.thickness}pt solid ${colour(el)}`
      // What is filled in can have a typeface and size of its own: a signature in the signature hand, say.
      const script = el.valueFont === 'script' || (!el.valueFont && el.font === 'script')
      const valueType: CSSProperties = {
        ...(el.valueFont && FONTS[el.valueFont] ? { fontFamily: FONTS[el.valueFont].css } : {}),
        fontSize: el.valueSize ? `${el.valueSize}pt` : '1.1em',
        fontWeight: el.italic || script ? 400 : 700,
        fontStyle: el.italic && !script ? 'italic' : 'normal',
        ...(script ? { overflow: 'visible', lineHeight: 1, marginBottom: '-0.03in' } : {}),
      }
      return (
        <div style={{ ...type, display: 'flex', alignItems: 'flex-end', ...(el.lineUnderLabel ? { paddingBottom: '0.01in', borderBottom: rule } : {}) }}>
          <span style={{ flex: 'none', paddingRight: '0.05in', fontWeight: el.bold ? 700 : 400 }}>{fillFields(el.text, values)}</span>
          <span style={{ flex: 1, minWidth: 0, padding: '0 0.04in', whiteSpace: 'nowrap', overflow: 'hidden', color: '#1d1d1f', borderBottom: el.lineUnderLabel ? undefined : rule, ...valueType }}>
            {fillFields(el.value, values)}&nbsp;
          </span>
        </div>
      )
    }
    // A thin line gets a taller invisible strip around it, so it is easy to pick up.
    if (el.type === 'line') return <div style={{ padding: '0.04in 0', margin: '-0.04in 0' }}><div style={{ borderTop: `${el.thickness}pt solid ${colour(el)}` }} /></div>
    if (el.type === 'box') return <div style={{ height: `${Math.max(el.h, 0.05)}in`, border: `${el.thickness}pt solid ${colour(el)}`, boxSizing: 'border-box' }} />
    if (el.type === 'image') {
      const picture = pictureOf(el.image)
      const size: CSSProperties = { width: '100%', height: `${Math.max(el.h, 0.05)}in`, display: 'block' }
      return picture
        ? <img src={picture.url} alt="" draggable={false} style={{ ...size, objectFit: 'contain', objectPosition: `${el.align} center` }} />
        : <div style={{ ...size, boxSizing: 'border-box', border: '1px dashed #8a93a3', background: 'rgba(120,130,150,.08)', color: '#5b6474', font: '7pt/1.1 Arial, sans-serif', display: 'flex', alignItems: 'center', justifyContent: 'center', textAlign: 'center' }}>Picture: upload one below</div>
    }
    return <img src={sampleQr} alt="" draggable={false} style={{ width: '100%', height: `${el.w}in`, display: 'block' }} />
  }

  // What to tell the person about the selected item's place on the card.
  const warnings: string[] = []
  if (selected) {
    const h = heightOf(selected, values)
    const right = selected.x + selected.w
    if (side === 'back' && right > ADDRESS_FROM_X + 0.005) warnings.push('Part of this is in the area where the guest’s address and the postage are printed, and will be covered.')
    else if (side === 'back' && right > BARCODE_FROM_X + 0.005 && selected.y + h > BARCODE_FROM_Y + 0.005) warnings.push('Part of this is in the strip where the barcode is printed, and will be covered.')
    if (selected.x < EDGE || selected.y < EDGE || right > CARD_W - EDGE || selected.y + h > CARD_H - EDGE) warnings.push('This is within ⅛ inch of the card’s edge, where it may be trimmed off.')
  }

  const handle: CSSProperties = { position: 'absolute', width: 10 / scale, height: 10 / scale, background: '#fff', border: `${1.5 / scale}px solid #2563eb`, borderRadius: 2 / scale, touchAction: 'none' }
  const num = (label: string, key: 'x' | 'y' | 'w' | 'h' | 'size' | 'lineHeight' | 'thickness', step: number, min: number, max: number, hint?: string) =>
    selected && (
      <Field label={label} hint={hint}>
        <input type="number" step={step} min={min} max={max} value={selected[key]}
          onChange={(e) => { const v = Number(e.target.value); if (e.target.value !== '' && Number.isFinite(v)) edit(key === 'w' && selected.type === 'qr' ? { w: v, h: v } : { [key]: v }) }} />
      </Field>
    )

  return (
    <div>
      <style>{FONT_FACES}</style>
      {canEdit && (
        <div className="row-actions" style={{ marginBottom: 8, flexWrap: 'wrap' }}>
          <button type="button" className="btn btn-sm" onClick={() => add('text')}>Add words</button>
          <button type="button" className="btn btn-sm" onClick={() => add('fill')}>Add a fill-in line</button>
          <button type="button" className="btn btn-sm" onClick={() => add('image')}>Add a picture</button>
          <button type="button" className="btn btn-sm" onClick={() => add('line')}>Add a line</button>
          <button type="button" className="btn btn-sm" onClick={() => add('box')}>Add a box</button>
          {side === 'back' && !hasQr && <button type="button" className="btn btn-sm" onClick={() => add('qr')}>Add the QR code</button>}
          <button type="button" className="btn btn-sm" onClick={undo} disabled={!canUndo}>Undo</button>
          <button type="button" className="btn btn-sm" onClick={() => { snapshot(); setSelectedId(null); onStartAgain() }}>{startAgainLabel}</button>
        </div>
      )}

      <div ref={holder} style={{ maxWidth: CARD_W * PX_PER_IN * 1.75 }}>
        <div
          ref={canvas}
          tabIndex={0}
          role="application"
          aria-label={`The ${side} of the card. Click an item to select it, drag it to move it, or use the arrow keys.`}
          onKeyDown={onKey}
          onPointerDown={() => setSelectedId(null)}
          style={{ position: 'relative', width: CARD_W * PX_PER_IN * scale, height: CARD_H * PX_PER_IN * scale, overflow: 'hidden', border: '1px solid var(--border)', borderRadius: 4, background, outlineOffset: 2, userSelect: 'none' }}
        >
          <div style={{ position: 'absolute', left: 0, top: 0, width: `${CARD_W}in`, height: `${CARD_H}in`, transform: `scale(${scale})`, transformOrigin: '0 0' }}>
            {side === 'back' && (
              <>
                <Zone left={ADDRESS_FROM_X} top={0} width={CARD_W - ADDRESS_FROM_X} height={CARD_H}>The guest’s address and the postage are printed here</Zone>
                <Zone left={BARCODE_FROM_X} top={BARCODE_FROM_Y} width={ADDRESS_FROM_X - BARCODE_FROM_X} height={CARD_H - BARCODE_FROM_Y}>Barcode</Zone>
              </>
            )}
            {elements.map((el, i) => {
              const on = el.id === selectedId
              return (
                <div
                  key={el.id}
                  data-item={el.type}
                  title={canEdit ? `${TYPE_NAMES[el.type]}: drag to move` : TYPE_NAMES[el.type]}
                  onPointerDown={(e) => drag(e, el, 'move')}
                  style={{
                    position: 'absolute', left: `${el.x}in`, top: `${el.y}in`, width: `${el.w}in`, boxSizing: 'border-box', cursor: canEdit ? 'move' : 'default', touchAction: 'none',
                    outline: on ? `${1.5 / scale}px solid #2563eb` : undefined, outlineOffset: 1 / scale, zIndex: i + 1,
                  }}
                >
                  {draw(el)}
                  {on && canEdit && (el.type === 'image' || el.type === 'box' || el.type === 'qr'
                    ? <div aria-hidden="true" onPointerDown={(e) => drag(e, el, 'size')} style={{ ...handle, right: -6 / scale, bottom: -6 / scale, cursor: 'nwse-resize' }} />
                    : <div aria-hidden="true" onPointerDown={(e) => drag(e, el, 'width')} style={{ ...handle, right: -6 / scale, top: '50%', marginTop: -5 / scale, cursor: 'ew-resize' }} />)}
                </div>
              )
            })}
          </div>
        </div>
      </div>
      <p className="muted small" style={{ marginTop: 6 }}>
        {canEdit
          ? 'Click anything on the card to select it. Drag it to move it; drag the small square to resize it. The arrow keys move it a hundredth of an inch (with Shift, a tenth), and Delete removes it.'
          : 'This is how the card is laid out.'}
        {side === 'back' && ' The shaded areas are printed over when the card is mailed.'}
      </p>

      {selected && canEdit && (
        <div style={{ border: '1px solid var(--border)', borderRadius: 6, padding: '0.9rem', marginTop: 8 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap', marginBottom: 8 }}>
            <strong>{TYPE_NAMES[selected.type]}</strong>
            <div className="row-actions">
              <button type="button" className="btn btn-sm" onClick={() => duplicate(selected)}>Make a copy</button>
              <button type="button" className="btn btn-sm" onClick={() => reorder(selected.id, true)} title="Draw this on top of anything it overlaps">Bring to the front</button>
              <button type="button" className="btn btn-sm" onClick={() => reorder(selected.id, false)} title="Draw this underneath anything it overlaps">Send to the back</button>
              <button type="button" className="btn btn-sm btn-danger" onClick={() => remove(selected.id)}>Remove from the card</button>
            </div>
          </div>
          {warnings.map((w) => <Alert key={w} kind="warn">{w}</Alert>)}

          {selected.type === 'text' && (
            <div className="grid grid-2">
              <Field label="Words" hint="Press Enter for a new line. A line made only of fields that come out empty is left off the card.">
                <textarea rows={4} maxLength={600} value={selected.text ?? ''} onChange={(e) => edit({ text: e.target.value })} />
              </Field>
              <Field label="Add a field" hint="Fields are filled in for each card and each restaurant, so one design works for all of them.">
                <select value="" aria-label="Add a field to the words" onChange={(e) => { if (e.target.value) edit({ text: `${selected.text ?? ''}{${e.target.value}}` }) }}>
                  <option value="">Choose a field to add…</option>
                  {mergeFields.map((f) => <option key={f.key} value={f.key}>{f.label}{f.sample ? ` (${f.sample.length > 38 ? `${f.sample.slice(0, 38)}…` : f.sample})` : ''}</option>)}
                </select>
              </Field>
            </div>
          )}
          {selected.type === 'fill' && (
            <div className="grid grid-2">
              <Field label="Label" hint="Printed before the line, for example “Please enjoy:”.">
                <input maxLength={120} value={selected.text ?? ''} onChange={(e) => edit({ text: e.target.value })} />
              </Field>
              <Field label="What is printed on the line">
                <select value={mergeFields.some((f) => `{${f.key}}` === selected.value) ? selected.value ?? '' : selected.value ? 'other' : ''} aria-label="What is printed on the line"
                  onChange={(e) => edit({ value: e.target.value === 'other' ? selected.value ?? '' : e.target.value })}>
                  <option value="">Nothing (an empty line to write on)</option>
                  {mergeFields.map((f) => <option key={f.key} value={`{${f.key}}`}>{f.label}</option>)}
                  {selected.value && !mergeFields.some((f) => `{${f.key}}` === selected.value) && <option value="other">Other words</option>}
                </select>
              </Field>
            </div>
          )}
          {selected.type === 'image' && (
            <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
              {pictureOf(selected.image) && <img src={pictureOf(selected.image)!.url} alt="The picture in this place" style={{ maxWidth: 160, maxHeight: 70, border: '1px solid var(--border)', borderRadius: 4, background: '#f3f4f6' }} />}
              <input ref={fileInput} type="file" accept="image/png,image/jpeg" hidden aria-label="Choose a picture for this place on the card"
                onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ''; if (f && selected.image) onUploadPicture(selected.image, f) }} />
              <button type="button" className="btn btn-sm" onClick={() => fileInput.current?.click()} disabled={busy}>{pictureOf(selected.image) ? 'Replace the picture' : 'Upload a picture'}</button>
              <span className="muted small">
                PNG or JPEG, up to 5 MB. It is fitted inside the outline without being stretched.
                {pictureOf(selected.image)?.fromGroup ? ' This one comes from the group’s card.' : ''}
              </span>
            </div>
          )}

          {selected.type === 'image' && (
            <div className="grid grid-4">
              <Field label="Sits in its outline">
                <select value={selected.align} onChange={(e) => edit({ align: e.target.value as CardElement['align'] })}>
                  <option value="left">To the left</option>
                  <option value="center">In the middle</option>
                  <option value="right">To the right</option>
                </select>
              </Field>
            </div>
          )}

          {(selected.type === 'text' || selected.type === 'fill') && (
            <div className="grid grid-4">
              <Field label="Typeface">
                <select value={selected.font} onChange={(e) => edit({ font: e.target.value as CardFont })}>
                  {(Object.keys(FONTS) as CardFont[]).map((f) => <option key={f} value={f}>{FONTS[f].label}</option>)}
                </select>
              </Field>
              {num('Size (points)', 'size', 0.5, 4, 60)}
              {selected.type === 'text' && num('Line spacing', 'lineHeight', 0.05, 0.9, 2.5)}
              {selected.type === 'text' && (
                <Field label="Lined up">
                  <select value={selected.align} onChange={(e) => edit({ align: e.target.value as CardElement['align'] })}>
                    <option value="left">Left</option>
                    <option value="center">Centre</option>
                    <option value="right">Right</option>
                  </select>
                </Field>
              )}
              <div style={{ display: 'flex', gap: 14, alignItems: 'center', flexWrap: 'wrap' }}>
                <label className="check"><input type="checkbox" checked={selected.bold} onChange={(e) => edit({ bold: e.target.checked })} /> {selected.type === 'fill' ? 'Bold label' : 'Bold'}</label>
                <label className="check"><input type="checkbox" checked={selected.italic} onChange={(e) => edit({ italic: e.target.checked })} /> {selected.type === 'fill' ? 'Italic on the line (like a signature)' : 'Italic'}</label>
                {selected.type === 'fill' && <label className="check"><input type="checkbox" checked={selected.lineUnderLabel} onChange={(e) => edit({ lineUnderLabel: e.target.checked })} /> The line runs under the label too</label>}
              </div>
            </div>
          )}
          {selected.type === 'fill' && (
            <div className="grid grid-4" style={{ marginTop: 8 }}>
              <Field label="Typeface on the line" hint="For what is filled in. The signature hand suits a signature.">
                <select value={selected.valueFont ?? ''} onChange={(e) => edit({ valueFont: (e.target.value || null) as CardFont | null })}>
                  <option value="">The same as the label</option>
                  {(Object.keys(FONTS) as CardFont[]).filter((f) => f !== selected.font).map((f) => <option key={f} value={f}>{FONTS[f].label}</option>)}
                </select>
              </Field>
              <Field label="Size on the line (points)" hint="0 prints it a little larger than the label. A signature reads well at about 15.">
                <input type="number" step={0.5} min={0} max={60} value={selected.valueSize ?? 0}
                  onChange={(e) => { const v = Number(e.target.value); if (e.target.value !== '' && Number.isFinite(v)) edit({ valueSize: v }) }} />
              </Field>
            </div>
          )}

          <div className="grid grid-4" style={{ marginTop: 8 }}>
            {selected.type !== 'image' && selected.type !== 'qr' && (
              <Field label="Colour">
                <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                  <select value={selected.color === 'ink' ? 'ink' : 'own'} aria-label="Which colour" onChange={(e) => edit({ color: e.target.value === 'ink' ? 'ink' : ink })}>
                    <option value="ink">{side === 'back' ? 'The card’s ink colour' : 'The card’s colour for words'}</option>
                    <option value="own">Its own colour</option>
                  </select>
                  {selected.color !== 'ink' && <input type="color" style={{ width: 48, height: 34, padding: 2, flex: 'none' }} value={selected.color} onChange={(e) => edit({ color: e.target.value })} aria-label="This item’s own colour" />}
                </div>
              </Field>
            )}
            {(selected.type === 'line' || selected.type === 'box' || selected.type === 'fill') && num('Line thickness (points)', 'thickness', 0.25, 0.25, 6)}
            {num('From the left (inches)', 'x', 0.01, 0, CARD_W)}
            {num('From the top (inches)', 'y', 0.01, 0, CARD_H)}
            {num(selected.type === 'qr' ? 'Size (inches)' : 'Width (inches)', 'w', 0.01, selected.type === 'qr' ? 0.5 : 0.05, selected.type === 'qr' ? 2 : CARD_W, selected.type === 'qr' ? 'At least half an inch, so phones can read it.' : undefined)}
            {(selected.type === 'image' || selected.type === 'box') && num('Height (inches)', 'h', 0.01, 0.05, CARD_H)}
          </div>
        </div>
      )}
    </div>
  )
}

/** A part of the back the mail house prints over. Drawn under the card's items and never sent to the printer. */
function Zone({ left, top, width, height, children }: { left: number; top: number; width: number; height: number; children: string }) {
  return (
    <div
      aria-hidden="true"
      style={{
        position: 'absolute', left: `${left}in`, top: `${top}in`, width: `${width}in`, height: `${height}in`, boxSizing: 'border-box', pointerEvents: 'none',
        border: '1px dashed #9aa3b2', background: 'repeating-linear-gradient(135deg, rgba(120,130,150,.10) 0 6px, rgba(120,130,150,.03) 6px 12px)',
        display: 'flex', alignItems: 'center', justifyContent: 'center', textAlign: 'center', padding: 4, color: '#5b6474', font: '8pt/1.25 Arial, sans-serif',
      }}
    >
      {children}
    </div>
  )
}
