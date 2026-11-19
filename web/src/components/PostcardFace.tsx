import { useState, type CSSProperties } from 'react'
import type { CommentSummary, PostcardFieldKey, PostcardSettings, RestaurantDto } from '../api/types'
import { fmtDate } from './ui'

/** What goes on a card. A CommentSummary has all of it; the settings page passes a sample. */
export type CardContent = Pick<
  CommentSummary,
  'guestFirstName' | 'guestLastName' | 'streetAddress' | 'city' | 'state' | 'zip' | 'couponOffer' | 'dateIssued' | 'expiresOn' | 'authorizedBy'
>

const ADDRESS_FIELDS: PostcardFieldKey[] = ['guestName', 'street', 'cityStateZip']
export const FIELD_LABELS: Record<PostcardFieldKey, string> = {
  offer: 'Offer ("Please enjoy ___")',
  dateIssued: 'Date issued',
  goodUntil: 'Good until',
  authorizedBy: 'Authorized by',
  guestName: 'Guest name',
  street: 'Street address',
  cityStateZip: 'City, state and zip',
  qr: 'QR code (¾ inch square, leads staff back to the card’s record)',
}

/** The QR code prints ¾ inch square. */
export const QR_SIZE_IN = 0.75

export function cardValues(c: CardContent): Record<PostcardFieldKey, string> {
  return {
    offer: c.couponOffer ?? '',
    dateIssued: fmtDate(c.dateIssued),
    goodUntil: fmtDate(c.expiresOn),
    authorizedBy: c.authorizedBy ?? '',
    guestName: `${c.guestFirstName} ${c.guestLastName}`.trim(),
    street: c.streetAddress ?? '',
    cityStateZip: [[c.city, c.state].filter(Boolean).join(', '), c.zip].filter(Boolean).join(' '),
    qr: '',
  }
}

export const hasMailingAddress = (c: CardContent) => Boolean(c.streetAddress?.trim() && c.city?.trim() && c.state?.trim() && c.zip?.trim())

/**
 * One side of a postcard at its real size (CSS inches, so screen and paper match).
 *  - Pre-printed stock: only the blanks, each at its configured position.
 *  - Blank stock: the whole card, with the wording, a return address and a box for the stamp.
 * When postage was bought the address side is the provider's label (see PostageLabel), so the guest's address
 * and the stamp box are left off here; two addresses on one piece of mail confuse the sorting machines.
 */
export default function PostcardFace({
  card,
  settings,
  restaurant,
  validityMonths,
  withAddress = true,
  outline = false,
  qr,
}: {
  card: CardContent
  settings: PostcardSettings
  restaurant?: RestaurantDto
  validityMonths: number
  withAddress?: boolean
  outline?: boolean
  /** The card's QR code as a picture address, or "sample" to show where it goes. Left out, no code is printed. */
  qr?: string
}) {
  const v = cardValues(card)
  const size: CSSProperties = { width: `${settings.widthIn}in`, height: `${settings.heightIn}in`, fontSize: `${settings.fontPt}pt` }
  const shift = `translate(${settings.nudgeXIn}in, ${settings.nudgeYIn}in)`

  if (settings.stock === 'Preprinted') {
    return (
      <div className={`print-card preprinted ${outline ? 'outlined' : ''}`} style={size}>
        <div className="print-shift" style={{ transform: shift }}>
          {(Object.keys(settings.fields) as PostcardFieldKey[])
            .filter((k) => k !== 'qr' && settings.fields[k].show && v[k] && (withAddress || !ADDRESS_FIELDS.includes(k)))
            .map((k) => (
              <span key={k} className="print-field" style={{ left: `${settings.fields[k].x}in`, top: `${settings.fields[k].y}in` }}>
                {v[k]}
              </span>
            ))}
          {qr && settings.fields.qr?.show && (
            <span className="print-field" style={{ left: `${settings.fields.qr.x}in`, top: `${settings.fields.qr.y}in` }}>
              <QrMark qr={qr} />
            </span>
          )}
        </div>
      </div>
    )
  }

  const returnLines = restaurant ? [restaurant.name, restaurant.streetAddress, [[restaurant.city, restaurant.state].filter(Boolean).join(', '), restaurant.zip].filter(Boolean).join(' ')].filter(Boolean) : []
  return (
    <div className={`print-card blank ${outline ? 'outlined' : ''}`} style={size}>
      <div className="print-shift blank-layout" style={{ transform: shift }}>
        <div className="blank-coupon">
          {restaurant && <div className="blank-from">{restaurant.name}</div>}
          <p className="blank-enjoy">
            Please enjoy <span className="blank-fill">{v.offer || ' '}</span> the next time you visit us.
          </p>
          <div className="blank-line"><span>Date issued</span><span className="blank-fill">{v.dateIssued}</span></div>
          <div className="blank-line"><span>Good until</span><span className="blank-fill">{v.goodUntil}</span></div>
          <div className="blank-line"><span>Authorized by</span><span className="blank-fill">{v.authorizedBy || ' '}</span></div>
          <div className="blank-foot">
            {qr && <QrMark qr={qr} />}
            <p className="blank-legal">
              This coupon is good for {validityMonths} month{validityMonths === 1 ? '' : 's'} from the date of issue. Please present this card prior to
              ordering. One order per card. Cannot be exchanged for gift certificates or cash in whole or part and has no cash value. Not valid with other
              promotion or discounts. Valid only with authorized signature.
            </p>
          </div>
        </div>
        {withAddress && (
          <div className="blank-mail">
            <div className="blank-return">{returnLines.map((l, i) => <div key={i}>{l}</div>)}</div>
            <div className="blank-stamp">Place stamp here</div>
            <div className="blank-to">
              <div>{v.guestName}</div>
              <div>{v.street}</div>
              <div>{v.cityStateZip}</div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

/** The card's QR code at its printed size, or a stand-in box on the settings preview. */
function QrMark({ qr }: { qr: string }) {
  const size = { width: `${QR_SIZE_IN}in`, height: `${QR_SIZE_IN}in`, flex: 'none' as const, display: 'block' }
  if (qr === 'sample')
    return (
      <span style={{ ...size, border: '1px dashed #555', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '7pt', textAlign: 'center', lineHeight: 1.1, whiteSpace: 'normal' }}>
        QR code
      </span>
    )
  return <img src={qr} alt="QR code for this card" style={size} />
}

/**
 * The postage provider's mailing label (addresses, barcode and the postage itself) as the card's address side.
 * Turned a quarter turn when the label's shape is the opposite of the card's.
 */
export function PostageLabel({ url, settings, test }: { url: string; settings: PostcardSettings; test: boolean }) {
  const [turn, setTurn] = useState(false)
  const landscape = settings.widthIn >= settings.heightIn
  return (
    <div className="print-card label-side outlined" style={{ width: `${settings.widthIn}in`, height: `${settings.heightIn}in` }}>
      <img
        src={url}
        alt="USPS postage label"
        className={turn ? 'turned' : ''}
        style={turn ? { width: `${settings.heightIn}in`, height: `${settings.widthIn}in` } : { width: `${settings.widthIn}in`, height: `${settings.heightIn}in` }}
        onLoad={(e) => setTurn(e.currentTarget.naturalWidth >= e.currentTarget.naturalHeight !== landscape)}
      />
      {test && <div className="label-test no-print">Test label: not valid postage</div>}
    </div>
  )
}
