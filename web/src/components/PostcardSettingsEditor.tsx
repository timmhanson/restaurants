import { Link } from 'react-router-dom'
import type { GroupSettings, PostcardFieldKey, PostcardSettings } from '../api/types'
import PostcardFace, { FIELD_LABELS, type CardContent } from './PostcardFace'
import { Field, addMonths, today } from './ui'

export function sampleCard(settings: Pick<GroupSettings, 'couponOffers' | 'couponValidityMonths'>): CardContent {
  return {
    guestFirstName: 'Pat', guestLastName: 'Sample', streetAddress: '123 Main Street', city: 'Joliet', state: 'IL', zip: '60431',
    couponOffer: settings.couponOffers[0] ?? 'Free Value Basket', dateIssued: today(), expiresOn: addMonths(today(), settings.couponValidityMonths), authorizedBy: 'A. Manager',
  }
}

/**
 * How coupon cards print: which card stock, its size, where each blank lands on pre-printed cards, and whether
 * postage may be bought. The preview is the card at its real size.
 */
export default function PostcardSettingsEditor({ settings, onChange, dirty }: { settings: GroupSettings; onChange: (p: PostcardSettings) => void; dirty: boolean }) {
  const p = settings.postcard
  const set = <K extends keyof PostcardSettings>(k: K, v: PostcardSettings[K]) => onChange({ ...p, [k]: v })
  const num = (v: string) => (v === '' || Number.isNaN(Number(v)) ? 0 : Number(v))
  const setField = (k: PostcardFieldKey, change: Partial<PostcardSettings['fields'][PostcardFieldKey]>) => onChange({ ...p, fields: { ...p.fields, [k]: { ...p.fields[k], ...change } } })

  return (
    <div className="panel">
      <h2>Printing coupon cards onto postcards</h2>
      <fieldset className="role-choice" style={{ marginBottom: '0.75rem' }}>
        <legend>Card stock</legend>
        <div className="source-choice two" role="radiogroup">
          <label className={p.stock === 'Preprinted' ? 'selected' : ''}>
            <input type="radio" name="stock" checked={p.stock === 'Preprinted'} onChange={() => set('stock', 'Preprinted')} />
            <span className="source-label">Pre-printed cards</span>
            <span className="source-hint">Your cards already carry the wording. Only the blanks are printed, at the positions below.</span>
          </label>
          <label className={p.stock === 'Blank' ? 'selected' : ''}>
            <input type="radio" name="stock" checked={p.stock === 'Blank'} onChange={() => set('stock', 'Blank')} />
            <span className="source-label">Blank postcards</span>
            <span className="source-hint">The whole card is printed: wording, coupon details, return address and the guest's address.</span>
          </label>
        </div>
      </fieldset>
      <div className="form-row">
        <Field label="Card width (inches)" hint="Measured the way the card feeds through the printer.">
          <input type="number" step="0.01" min={3} max={11} value={p.widthIn} onChange={(e) => set('widthIn', num(e.target.value))} />
        </Field>
        <Field label="Card height (inches)">
          <input type="number" step="0.01" min={3} max={9} value={p.heightIn} onChange={(e) => set('heightIn', num(e.target.value))} />
        </Field>
        <Field label="Text size (points)">
          <input type="number" step="0.5" min={7} max={18} value={p.fontPt} onChange={(e) => set('fontPt', num(e.target.value))} />
        </Field>
        <Field label="Move everything right (inches)" hint="Negative moves left. Use it to line up with your printer.">
          <input type="number" step="0.01" min={-1} max={1} value={p.nudgeXIn} onChange={(e) => set('nudgeXIn', num(e.target.value))} />
        </Field>
        <Field label="Move everything down (inches)" hint="Negative moves up.">
          <input type="number" step="0.01" min={-1} max={1} value={p.nudgeYIn} onChange={(e) => set('nudgeYIn', num(e.target.value))} />
        </Field>
      </div>
      <p className="legend-note">
        USPS charges the postcard rate for cards up to 6 × 4.25 inches; larger cards need letter postage.
      </p>

      {p.stock === 'Preprinted' && (
        <>
          <h3 style={{ marginTop: '1rem' }}>Where each blank is printed</h3>
          <p className="legend-note">
            Measure on one of your cards from its top-left corner to where the writing should start: inches across, then inches down. Print a test onto
            plain paper, hold it against a card up to the light, and adjust.
          </p>
          <div className="table-wrap" style={{ boxShadow: 'none' }}>
            <table>
              <thead>
                <tr><th>Print</th><th>Blank</th><th>Inches from the left</th><th>Inches from the top</th></tr>
              </thead>
              <tbody>
                {(Object.keys(FIELD_LABELS) as PostcardFieldKey[]).map((k) => (
                  <tr key={k}>
                    <td><input type="checkbox" aria-label={`Print ${FIELD_LABELS[k]}`} checked={p.fields[k].show} onChange={(e) => setField(k, { show: e.target.checked })} /></td>
                    <td>{FIELD_LABELS[k]}</td>
                    <td><input type="number" step="0.01" min={0} max={p.widthIn} aria-label={`${FIELD_LABELS[k]}: inches from the left`} value={p.fields[k].x} onChange={(e) => setField(k, { x: num(e.target.value) })} disabled={!p.fields[k].show} /></td>
                    <td><input type="number" step="0.01" min={0} max={p.heightIn} aria-label={`${FIELD_LABELS[k]}: inches from the top`} value={p.fields[k].y} onChange={(e) => setField(k, { y: num(e.target.value) })} disabled={!p.fields[k].show} /></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      <h3 style={{ marginTop: '1rem' }}>Preview (real size)</h3>
      <div className="print-scroll" style={{ padding: '0.25rem' }}>
        <PostcardFace card={sampleCard(settings)} settings={p} validityMonths={settings.couponValidityMonths} outline />
      </div>
      <p className="legend-note">
        {dirty ? 'Save the settings, then print a test card.' : <Link to="/print-test">Print a test card</Link>}
      </p>

      <h3 style={{ marginTop: '1rem' }}>Postage</h3>
      <label className="check">
        <input type="checkbox" checked={p.allowBoughtPostage} onChange={(e) => set('allowBoughtPostage', e.target.checked)} />
        <span>
          Allow postage to be bought when printing <span className="muted">(only people given the "buy postage" permission can, and only once the System Administrator has set up the
          postage service). Stamps by hand always work.</span>
        </span>
      </label>
    </div>
  )
}
