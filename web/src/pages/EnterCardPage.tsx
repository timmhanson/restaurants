import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import type { CommentSummary } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import CardForm, { emptyCard } from '../components/CardForm'
import { Alert, Empty, fmtDate } from '../components/ui'

export default function EnterCardPage() {
  const { restaurants, settings, has, refreshContext } = useAuth()
  const [saved, setSaved] = useState<CommentSummary | null>(null)
  const [formKey, setFormKey] = useState(0)
  const initial = useMemo(() => emptyCard(restaurants.length === 1 ? restaurants[0].id : 0), [restaurants, formKey])

  if (!restaurants.length) {
    return (
      <>
        <h1>Enter a guest card</h1>
        <Empty>
          You are not assigned to a restaurant yet, so there is nowhere to file a card.{' '}
          <button type="button" className="btn-link" onClick={() => void refreshContext()}>
            Refresh
          </button>
        </Empty>
      </>
    )
  }

  return (
    <>
      <div className="page-head">
        <h1>Enter a guest card</h1>
        <span className="muted">Copy the postcard exactly as it was filled out.</span>
      </div>
      {!settings?.reasonCategories?.length && <Alert kind="warn">No reason categories are configured for this group yet. Ask an owner to set them up under Settings.</Alert>}
      {saved && (
        <Alert kind="success" onClose={() => setSaved(null)}>
          <strong>
            Saved card for {saved.guestFirstName} {saved.guestLastName}
          </strong>
          {fmtDate(saved.visitDate)} ({saved.dayOfWeek}) · {saved.reason} · {saved.restaurantName}
          {saved.couponOffer ? ` · ${saved.couponOffer}, good until ${fmtDate(saved.expiresOn)}` : ''}
          {saved.guestCommentCount > 1 && <div>This guest now has {saved.guestCommentCount} cards on file.</div>}
          {has('ViewComments') && (
            <div>
              <Link to="/comments">View all comments</Link>
            </div>
          )}
        </Alert>
      )}
      <CardForm
        key={formKey}
        initial={initial}
        onSaved={(c) => {
          setSaved(c)
          setFormKey((k) => k + 1)
          window.scrollTo({ top: 0, behavior: 'smooth' })
        }}
      />
    </>
  )
}
