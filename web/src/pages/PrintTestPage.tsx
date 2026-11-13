import { useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import PostcardFace from '../components/PostcardFace'
import { sampleCard } from '../components/PostcardSettingsEditor'
import { Spinner } from '../components/ui'

/** Prints one sample card with the saved layout, to check alignment before printing real ones. Nothing is recorded. */
export default function PrintTestPage() {
  const { settings, restaurants } = useAuth()
  const nav = useNavigate()
  if (!settings) return <Spinner />
  const card = settings.postcard
  return (
    <div className="print-page">
      <style>{`@page { size: ${card.widthIn}in ${card.heightIn}in; margin: 0; }`}</style>
      <div className="print-toolbar no-print">
        <div className="inner">
          <div className="page-head" style={{ marginBottom: 0 }}>
            <h1 style={{ margin: 0 }}>Test card</h1>
            <span className="grow" />
            <button type="button" className="btn" onClick={() => nav('/settings')}>Back to settings</button>
            <button type="button" className="btn btn-primary" onClick={() => window.print()}>Print test card</button>
          </div>
          <div className="muted">
            A sample on {card.stock === 'Preprinted' ? 'pre-printed stock (only the blanks print)' : 'blank stock'}, {card.widthIn} × {card.heightIn} inches. In the print dialog choose that
            paper size, no margins and scale 100%. {card.stock === 'Preprinted' && 'Print onto plain paper first and hold it against a real card up to the light.'} Nothing is recorded.
          </div>
        </div>
      </div>
      <div className="print-sheet">
        <div className="print-scroll">
          <PostcardFace card={sampleCard(settings)} settings={card} restaurant={restaurants[0]} validityMonths={settings.couponValidityMonths} outline />
        </div>
      </div>
    </div>
  )
}
