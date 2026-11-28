import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { api } from '../api/client'
import type { GroupSettings, Onboarding, RestaurantDto } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { onboardingChanged } from '../components/SetupNotice'
import { Alert, Badge, Field, Spinner, fmtDateTime } from '../components/ui'

const money = (cents: number) => `$${(cents / 100).toFixed(2)}`
type Place = { name: string; streetAddress: string; city: string; state: string; zip: string; phone: string }
const blank: Place = { name: '', streetAddress: '', city: '', state: '', zip: '', phone: '' }
const placeOf = (r: RestaurantDto): Place => ({ name: r.name, streetAddress: r.streetAddress ?? '', city: r.city ?? '', state: r.state ?? '', zip: r.zip ?? '', phone: r.phone ?? '' })

/**
 * Getting Started: where a new investment group's administrator lands at their first sign-in. A welcome (with the
 * System Administrator's note, a short tour of how the site works and what has already been set up for them), then
 * a choice: set up now, or look around first. The wizard walks through a few short steps, each showing what is
 * there already for them to confirm or change. Nothing is forced: every step can be skipped, the wizard can be
 * left at any point, and it waits under Getting Started in the menu. The site needs only one restaurant to be used.
 */
export default function WelcomePage() {
  const { user, restaurants, settings, refreshContext } = useAuth()
  const nav = useNavigate()
  const [params, setParams] = useSearchParams()
  const [o, setO] = useState<Onboarding | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const step = params.get('step')

  useEffect(() => {
    api.get<Onboarding>('/onboarding').then(setO).catch((e) => setError((e as Error).message))
  }, [])

  const call = async (path: string) => {
    setBusy(true)
    setError(null)
    try {
      const next = await api.post<Onboarding>(`/onboarding/${path}`)
      setO(next)
      onboardingChanged()
      return next
    } catch (e) {
      setError((e as Error).message)
      return null
    } finally {
      setBusy(false)
    }
  }
  const goTo = (id: string | null) => setParams(id ? { step: id } : {})

  if (error && !o) return <Alert kind="error">{error}</Alert>
  if (!o) return <Spinner label="Loading…" />
  // Owners and managers (and an administrator of a group that is already set up, at their first visit) get the tour.
  if (!o.canRun || (o.tour && o.status === 'done' && !step)) return <PersonalTour steps={o.canRun} />

  const steps = o.steps
  const index = steps.findIndex((s) => s.id === step)
  const current = index >= 0 ? steps[index] : null
  const confirmed = steps.filter((s) => s.confirmed).length
  const first = (user?.displayName ?? '').split(' ')[0]

  const begin = async () => {
    if (await call('begin')) goTo((steps.find((s) => !s.confirmed) ?? steps[0]).id)
  }
  const later = async () => {
    if (await call('later')) nav('/enter')
  }
  /** Confirms this step and moves to the next one; after the last, on to the finish. */
  const confirm = async () => {
    if (!current) return
    if (await call(`steps/${current.id}/confirm`)) goTo(index + 1 < steps.length ? steps[index + 1].id : 'finish')
  }
  const skip = () => goTo(index + 1 < steps.length ? steps[index + 1].id : 'finish')
  const finish = async () => {
    if (await call('finish')) goTo('finish')
  }

  // ---- The welcome ----
  if (!step) {
    return (
      <div className="welcome">
        <div className="panel welcome-hero">
          <h1>{o.status === 'done' ? 'Getting started' : `Welcome${first ? `, ${first}` : ''}`}</h1>
          <p className="welcome-lead">
            {o.status === 'done'
              ? `${o.displayName} is set up. You can go back through any step below.`
              : `This is where ${o.displayName} keeps track of guest comments, sends each guest a coupon card, and makes sure someone follows up.`}
          </p>
          {o.welcomeNote && (
            <blockquote className="welcome-note">
              {o.welcomeNote}
              {o.welcomeNoteBy && <footer>{o.welcomeNoteBy}</footer>}
            </blockquote>
          )}
          {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
          <div className="row-actions">
            <button type="button" className="btn btn-primary" onClick={() => void begin()} disabled={busy}>
              {o.status === 'new' ? 'Set up now · about 5 minutes' : o.status === 'done' ? 'Go through the steps again' : `Continue setting up (${confirmed} of ${steps.length} done)`}
            </button>
            {o.status === 'done'
              ? <Link className="btn" to="/enter">Go to Enter Card</Link>
              : <button type="button" className="btn" onClick={() => void later()} disabled={busy}>Look around first</button>}
          </div>
          {o.status !== 'done' && <p className="muted small" style={{ marginBottom: 0 }}>Either way, nothing is locked. Set-up waits for you under Getting Started in the menu.</p>}
        </div>

        <h2 className="section-head">How it works</h2>
        <div className="grid grid-4 welcome-tour">
          <Tour n={1} title="Enter Card">When a guest complains or comments (a survey, a message, a phone call), someone types it in: who, when, what went wrong, and what you are offering them.</Tour>
          <Tour n={2} title="Print Cards">Each guest gets a coupon card. Print them yourselves and add a stamp, or have them printed and mailed for you.</Tour>
          <Tour n={3} title="Follow Up">A list of who still needs a reply, and who should be called to check their card arrived. Red numbers in the menu show what is waiting.</Tour>
          <Tour n={4} title="Reports">Every comment, what is being sent, and what you are billed, by restaurant.</Tour>
        </div>

        <h2 className="section-head">What is already here</h2>
        <div className="panel">
          <p className="muted" style={{ marginTop: 0 }}>Some of this may have been set up for you already. The steps let you check each part and change what is not right.</p>
          <ul className="welcome-list">
            {steps.map((s) => (
              <li key={s.id}>
                <button type="button" className="btn-link" onClick={() => goTo(s.id)}>{s.title}</button>{' '}
                {s.confirmed ? <Badge kind="ok">confirmed</Badge> : s.required && !o.minimumMet ? <Badge kind="danger">needed</Badge> : <Badge kind="neutral">to check</Badge>}
                <div className="muted small"><Summary o={o} id={s.id} /></div>
              </li>
            ))}
          </ul>
        </div>
      </div>
    )
  }

  // ---- The finish ----
  if (step === 'finish') {
    const open = steps.filter((s) => !s.confirmed)
    return (
      <div className="welcome">
        <div className="panel welcome-hero">
          <h1>{o.status === 'done' ? 'You are set up' : 'Almost there'}</h1>
          {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
          {o.status === 'done' ? (
            <>
              <p className="welcome-lead">{o.displayName} is ready to use. Here is what people usually do next.</p>
              <ul className="welcome-list">
                <li><Link to="/enter">Enter your first card</Link><div className="muted small">Try one with a real comment. You can remove it afterwards.</div></li>
                <li><Link to="/users">Add your people</Link><div className="muted small">Owners, managers, and a shared team login for each restaurant.</div></li>
                <li><Link to="/card-design">Design your coupon card</Link><div className="muted small">Your logo, colours and wording on the card guests receive. “Design it for me” there offers three designs to start from, free.</div></li>
                <li><Link to="/billing">Billing</Link><div className="muted small">Where your bills are, and how you pay them.</div></li>
              </ul>
              {open.length > 0 && <p className="muted">Not checked yet: {open.map((s) => s.title).join(', ')}. They are under Getting Started whenever you want them.</p>}
              <Link className="btn btn-primary" to="/enter">Go to Enter Card</Link>
            </>
          ) : !o.minimumMet ? (
            <>
              <p className="welcome-lead">One thing is still needed before the site can be used: a restaurant.</p>
              <button type="button" className="btn btn-primary" onClick={() => goTo('restaurants')}>Add a restaurant</button>
            </>
          ) : (
            <>
              <p className="welcome-lead">
                {open.length === 0 ? 'Every step is confirmed.' : `You have confirmed ${confirmed} of ${steps.length} steps. The rest can wait: ${open.map((s) => s.title).join(', ')}.`}
              </p>
              <div className="row-actions">
                <button type="button" className="btn btn-primary" onClick={() => void finish()} disabled={busy}>Finish set-up</button>
                {open.length > 0 && <button type="button" className="btn" onClick={() => goTo(open[0].id)}>Go back to {open[0].title}</button>}
                <button type="button" className="btn" onClick={() => void later()} disabled={busy}>Finish later</button>
              </div>
            </>
          )}
        </div>
      </div>
    )
  }

  if (!current) return <Alert kind="error">That step is not part of set-up. <Link to="/welcome">Back to Getting Started</Link></Alert>

  // ---- One step ----
  return (
    <div className="welcome">
      <div className="page-head">
        <h1>Getting started</h1>
        <span className="grow" />
        <button type="button" className="btn" onClick={() => void later()} disabled={busy}>Finish later</button>
      </div>
      <ol className="welcome-steps" aria-label="Set-up steps">
        {steps.map((s, i) => (
          <li key={s.id} className={`${s.id === current.id ? 'on' : ''} ${s.confirmed ? 'done' : ''}`}>
            <button type="button" onClick={() => goTo(s.id)} aria-current={s.id === current.id ? 'step' : undefined}>
              <span className="welcome-step-n">{s.confirmed ? '✓' : i + 1}</span> {s.title}
            </button>
          </li>
        ))}
      </ol>
      <div className="panel">
        <h2>{index + 1}. {current.title} {current.confirmed && <Badge kind="ok" title={current.confirmedAt ? `${current.confirmedBy ?? ''} ${fmtDateTime(current.confirmedAt)}` : undefined}>confirmed</Badge>}</h2>
        {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
        {current.id === 'group' && <GroupStep o={o} settings={settings} onSaved={refreshContext} />}
        {current.id === 'restaurants' && <RestaurantsStep restaurants={restaurants} onChanged={async () => { await refreshContext(); setO(await api.get<Onboarding>('/onboarding')); onboardingChanged() }} />}
        {current.id === 'offers' && <OffersStep settings={settings} onSaved={refreshContext} />}
        {current.id === 'sending' && <SendingStep o={o} />}
        {current.id === 'people' && <PeopleStep o={o} />}
        {current.id === 'assistant' && <AssistantStep o={o} />}
        <div className="actions" style={{ marginTop: '1rem' }}>
          {index > 0 && <button type="button" className="btn" onClick={() => goTo(steps[index - 1].id)} disabled={busy}>Back</button>}
          <button type="button" className="btn" onClick={skip} disabled={busy}>Skip for now</button>
          <button type="button" className="btn btn-primary" onClick={() => void confirm()} disabled={busy || (current.id === 'restaurants' && restaurants.length === 0)}
            title={current.id === 'restaurants' && restaurants.length === 0 ? 'Add a restaurant first' : undefined}>
            {busy ? 'Saving…' : index + 1 < steps.length ? 'Looks right, next' : 'Looks right'}
          </button>
        </div>
      </div>
    </div>
  )
}

/**
 * The welcome for one person: who they are here, and a card for each thing they can do, which depends on their
 * role and what they have been allowed. "Got it" records that they have seen it, so it is not shown again.
 */
function PersonalTour({ steps }: { steps: boolean }) {
  const { user, restaurants, has, isAdmin, isGroupWide, isGroupAdmin } = useAuth()
  const nav = useNavigate()
  const [busy, setBusy] = useState(false)
  const first = (user?.displayName ?? '').split(' ')[0]
  const places = restaurants.map((r) => r.name)
  const where = places.length === 0 ? 'no restaurant yet' : places.length <= 3 ? places.join(', ') : `${places.length} restaurants`
  const role = user?.role === 'Owner' ? 'an owner' : user?.role === 'Manager' ? 'a manager' : 'an administrator'
  const gotIt = async (to: string) => {
    setBusy(true)
    await api.post('/onboarding/welcomed').catch(() => undefined)
    onboardingChanged()
    nav(to)
  }
  const cards: { show: boolean; title: string; to: string; text: string }[] = [
    { show: true, title: 'Enter Card', to: '/enter', text: 'When a guest complains or comments (a survey, a message, a phone call), type it in: who, when, what went wrong and what they are being offered.' },
    { show: has('ViewComments') && has('MarkResponded'), title: 'Print Cards', to: '/print', text: 'Each guest gets a coupon card. Print them and add a stamp, or have them printed and mailed for you. The red number is how many have not been sent.' },
    { show: has('ViewComments'), title: 'Follow Up', to: '/follow-up', text: 'Who still needs a reply from someone, and who should be called to check their card arrived. Sending a card does not count as replying.' },
    { show: has('ViewComments'), title: 'All Comments', to: '/comments', text: 'Every comment for your restaurants, to search, open and correct.' },
    { show: has('ViewAnalytics'), title: 'Analytics', to: '/analytics', text: 'What goes wrong most, where and when, and how quickly guests are answered.' },
    { show: isAdmin, title: 'Restaurants & Staff', to: '/restaurants', text: 'Each restaurant’s details and who works there. Add managers, and a shared team login that only enters cards.' },
    { show: isAdmin, title: 'Billing', to: '/billing', text: 'What you are billed for cards mailed for you, and how it is paid.' },
    { show: isAdmin && has('DesignCards'), title: 'Card Design', to: '/card-design', text: 'Your logo, colours and wording on the card guests receive.' },
    { show: isGroupWide, title: 'Permissions and Settings', to: '/permissions', text: 'What owners and managers may do, and the choices on every card.' },
  ].filter((c) => c.show)
  return (
    <div className="welcome">
      <div className="panel welcome-hero">
        <h1>Welcome{first ? `, ${first}` : ''}</h1>
        <p className="welcome-lead">
          You are {role} here{isGroupAdmin ? ', so you can do everything in your investment group' : `, for ${where}`}. This is where guest comments are kept track of, each guest is
          sent a coupon card, and someone makes sure they are followed up.
        </p>
        <div className="row-actions">
          <button type="button" className="btn btn-primary" onClick={() => void gotIt('/enter')} disabled={busy}>Got it, take me to Enter Card</button>
          {has('ViewComments') && <button type="button" className="btn" onClick={() => void gotIt('/follow-up')} disabled={busy}>Show me what is waiting</button>}
          {steps && <Link className="btn" to="/welcome?step=group">See how the group is set up</Link>}
        </div>
        <p className="muted small" style={{ marginBottom: 0 }}>This welcome is shown once. Everything on it is in the menu on the left.</p>
      </div>

      <h2 className="section-head">What you can do</h2>
      <div className="grid grid-4 welcome-tour">
        {cards.map((c, i) => (
          <div key={c.title} className="panel welcome-tour-card">
            <div className="welcome-step-n">{i + 1}</div>
            <strong>{c.title}</strong>
            <p className="muted small" style={{ margin: '0.25rem 0 0.5rem' }}>{c.text}</p>
            <Link className="btn btn-sm" to={c.to}>Open</Link>
          </div>
        ))}
      </div>

      <h2 className="section-head">Good to know</h2>
      <div className="panel">
        <ul className="welcome-list">
          <li><strong>A guest is finished with</strong> when someone has replied to them, their card has been sent, and someone has checked it arrived.</li>
          <li><strong>The red numbers in the menu</strong> are what is waiting: cards not sent yet, and guests waiting for a reply or a follow-up call.</li>
          {!isGroupWide && <li><strong>What you see is what you have been allowed.</strong> If something you need is missing, ask {user?.role === 'Manager' ? 'your owner or administrator' : 'your investment group’s administrator'}.</li>}
          <li><strong>Your password</strong> is yours to change, with the Password button at the top right.</li>
        </ul>
      </div>
    </div>
  )
}

function Tour({ n, title, children }: { n: number; title: string; children: ReactNode }) {
  return (
    <div className="panel welcome-tour-card">
      <div className="welcome-step-n">{n}</div>
      <strong>{title}</strong>
      <p className="muted small" style={{ margin: '0.25rem 0 0' }}>{children}</p>
    </div>
  )
}

/** One line on the welcome saying how a part of the site stands right now. */
function Summary({ o, id }: { o: Onboarding; id: string }) {
  const s = o.sending
  if (id === 'group') return <>Shown to your people as “{o.displayName}”.</>
  if (id === 'restaurants') return <>{o.restaurants.length === 0 ? 'None yet. The site needs at least one.' : o.restaurants.map((r) => r.name).join(', ')}</>
  if (id === 'offers') return <>{o.offers.length} offers and {o.reasons.length} reasons to choose from on a card.</>
  if (id === 'sending') return <>{!s ? '' : s.mail === 'off' ? 'You print cards yourselves and add a stamp.' : `Cards can be printed and mailed for you${s.mode === 'test' ? ' (in test mode for now: nothing is really mailed or charged)' : ''}.`}</>
  if (id === 'people') return <>{o.people.length === 0 ? 'Only you so far.' : `${o.people.length} ${o.people.length === 1 ? 'person has' : 'people have'} an account.`}</>
  if (id === 'assistant') return <>{o.assistant?.on ? `On, at the ${o.assistant.level} level.` : 'Off. You can switch it on.'}</>
  return null
}

function GroupStep({ o, settings, onSaved }: { o: Onboarding; settings: GroupSettings | null; onSaved: () => Promise<void> }) {
  const [name, setName] = useState(o.displayName)
  const [note, setNote] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const save = async () => {
    if (!settings || !name.trim() || name.trim() === settings.groupDisplayName) return
    setBusy(true)
    setNote(null)
    try {
      await api.put<GroupSettings>('/settings', { ...settings, groupDisplayName: name.trim() })
      await onSaved()
      setNote('Saved.')
    } catch (e) {
      setNote((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <>
      <p className="muted">This is the name your people see at the top of the site. It was set up as below: change it if it is not how you want it written.</p>
      <Field label="Name shown in the site" hint="Saves when you leave the box.">
        <input value={name} maxLength={100} onChange={(e) => setName(e.target.value)} onBlur={() => void save()} disabled={busy} style={{ maxWidth: 420 }} />
      </Field>
      {note && <p className="muted small">{note}</p>}
    </>
  )
}

function RestaurantsStep({ restaurants, onChanged }: { restaurants: RestaurantDto[]; onChanged: () => Promise<void> }) {
  const [editing, setEditing] = useState<number | 'new' | null>(restaurants.length === 0 ? 'new' : null)
  const [v, setV] = useState<Place>(blank)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const open = (r: RestaurantDto | null) => {
    setEditing(r ? r.id : 'new')
    setV(r ? placeOf(r) : blank)
    setError(null)
  }
  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const was = restaurants.find((r) => r.id === editing)
      // What the restaurant already has that this form does not show goes back unchanged.
      const body = { ...v, name: v.name.trim(), signatureName: was?.signatureName ?? null, mailByDefault: was?.mailByDefault ?? false }
      if (was) await api.put(`/restaurants/${was.id}`, body)
      else await api.post('/restaurants', body)
      await onChanged()
      setEditing(null)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  const field = (k: keyof Place, label: string, hint?: string) => (
    <Field label={label} hint={hint}><input value={v[k]} onChange={(e) => setV((s) => ({ ...s, [k]: e.target.value }))} autoComplete="off" /></Field>
  )
  return (
    <>
      <p className="muted">
        The restaurants guest comments are for. This is the one thing the site cannot work without. The address is the return address on cards that are mailed,
        so it is worth getting right; the rest of each restaurant’s settings are on Restaurants &amp; Staff.
      </p>
      {restaurants.length === 0 ? <Alert kind="warn">No restaurant yet. Add the first one below.</Alert> : (
        <div className="table-wrap">
          <table className="compact">
            <thead><tr><th>Restaurant</th><th>Address</th><th>Phone</th><th /></tr></thead>
            <tbody>
              {restaurants.map((r) => (
                <tr key={r.id}>
                  <td><strong>{r.name}</strong></td>
                  <td>{r.streetAddress ? `${r.streetAddress}, ${[r.city, [r.state, r.zip].filter(Boolean).join(' ')].filter(Boolean).join(', ')}` : <Badge kind="warn">no address yet</Badge>}</td>
                  <td>{r.phone || <span className="muted">—</span>}</td>
                  <td><button type="button" className="btn btn-sm" onClick={() => open(r)} aria-label={`Change ${r.name}`}>Change</button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing === null ? (
        <button type="button" className="btn" style={{ marginTop: '0.75rem' }} onClick={() => open(null)}>+ Add a restaurant</button>
      ) : (
        <div className="followup-form" style={{ marginTop: '0.75rem' }}>
          <strong>{editing === 'new' ? 'New restaurant' : 'Change this restaurant'}</strong>
          {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
          {field('name', 'Name', 'As your people know it, e.g. “Culver’s of Joliet”.')}
          {field('streetAddress', 'Street address')}
          <div className="row">{field('city', 'City')}{field('state', 'State')}{field('zip', 'Zip')}</div>
          {field('phone', 'Phone')}
          <div className="row-actions">
            {(restaurants.length > 0 || editing !== 'new') && <button type="button" className="btn btn-sm" onClick={() => setEditing(null)} disabled={busy}>Cancel</button>}
            <button type="button" className="btn btn-sm btn-primary" onClick={() => void save()} disabled={busy || !v.name.trim()}>{busy ? 'Saving…' : 'Save restaurant'}</button>
          </div>
        </div>
      )}
    </>
  )
}

function ListEditor({ title, hint, items, onChange, disabled }: { title: string; hint: string; items: string[]; onChange: (next: string[]) => void; disabled: boolean }) {
  const [adding, setAdding] = useState('')
  const add = () => {
    const text = adding.trim()
    if (!text || items.some((i) => i.toLowerCase() === text.toLowerCase())) return
    onChange([...items, text])
    setAdding('')
  }
  return (
    <div>
      <h3 style={{ margin: '0.75rem 0 0.2rem' }}>{title}</h3>
      <p className="muted small" style={{ margin: 0 }}>{hint}</p>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0.35rem', margin: '0.5rem 0' }}>
        {items.map((i) => (
          <span key={i} className="chip">
            {i}
            <button type="button" className="btn-icon" onClick={() => onChange(items.filter((x) => x !== i))} disabled={disabled || items.length <= 1} aria-label={`Remove ${i}`}>×</button>
          </span>
        ))}
      </div>
      <div className="row-actions">
        <input value={adding} onChange={(e) => setAdding(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); add() } }} placeholder="Add another…" maxLength={100}
          style={{ maxWidth: 280 }} aria-label={`Add to ${title}`} />
        <button type="button" className="btn btn-sm" onClick={add} disabled={disabled || !adding.trim()}>Add</button>
      </div>
    </div>
  )
}

function OffersStep({ settings, onSaved }: { settings: GroupSettings | null; onSaved: () => Promise<void> }) {
  const [note, setNote] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  if (!settings) return <Spinner label="Loading…" />
  const save = async (change: Partial<GroupSettings>) => {
    setBusy(true)
    setNote(null)
    try {
      await api.put<GroupSettings>('/settings', { ...settings, ...change })
      await onSaved()
      setNote('Saved.')
    } catch (e) {
      setNote((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <>
      <p className="muted">These are the choices on every card. They start as the usual ones: take out what you never offer and add what you do. Each change saves as you make it.</p>
      <ListEditor title="What you offer a guest" hint="“Please enjoy ___ the next time you visit us.” List them from the smallest to the largest." items={settings.couponOffers} disabled={busy}
        onChange={(couponOffers) => void save({ couponOffers })} />
      <ListEditor title="Reasons a guest gets a card" hint="Used to see what goes wrong most, by restaurant." items={settings.reasonCategories} disabled={busy}
        onChange={(reasonCategories) => void save({ reasonCategories })} />
      {note && <p className="muted small">{note}</p>}
      <p className="muted small">A coupon is good for {settings.couponValidityMonths} months, and a guest is followed up {settings.followUpDays} days after their card is sent. Both can be changed on Settings.</p>
    </>
  )
}

function SendingStep({ o }: { o: Onboarding }) {
  const s = o.sending
  if (!s) return null
  return (
    <>
      <p className="muted">How coupon cards reach guests. This is how it has been set up for you; say so if any of it is not what you agreed.</p>
      <dl className="kv">
        <dt>Printing them yourselves</dt>
        <dd>Always available: print the cards on postcards and add a stamp.{s.printFeeCents > 0 ? ` A card you print yourself costs ${money(s.printFeeCents)}.` : ''}</dd>
        <dt>Printed and mailed for you</dt>
        <dd>
          {s.mail === 'off' ? <><Badge kind="neutral">off</Badge> Not switched on for your investment group.</>
            : <><Badge kind="ok">{s.mail === 'each' ? 'each restaurant decides' : 'on'}</Badge> {money(s.priceCents)} a card, on a monthly bill.</>}
          {' '}<span className="muted">{s.groupDecidesMail ? 'You can change this on Settings.' : 'This was decided by the people who run the site.'}</span>
        </dd>
        {s.mail !== 'off' && (
          <>
            <dt>Real or test</dt>
            <dd>{s.mode === 'test' ? <><Badge kind="warn">test</Badge> Cards sent to be mailed are only tried out: nothing is printed, mailed or charged until this is switched to real.</>
              : s.mode === 'live' ? <><Badge kind="ok">real</Badge> Cards are printed, mailed and billed.</> : 'Each restaurant decides.'}</dd>
            <dt>Paying</dt>
            <dd>{s.hasPayMethod ? 'A payment method is saved.' : s.payRequired ? <>A payment method is needed before cards can be mailed for real. Add one on <Link to="/billing">Billing</Link>.</> : <>Bills are on <Link to="/billing">Billing</Link>.</>}</dd>
          </>
        )}
        <dt>Who signs the cards</dt>
        <dd>Managers, owners and administrators sign with their own name. Cards entered with a shared team login are signed {s.signatureForAll ? `“${s.signatureForAll}”` : 'with the name set for each restaurant'}.</dd>
      </dl>
      <p className="muted small">Change any of this on <Link to="/settings">Settings</Link>; what the card looks like is on <Link to="/card-design">Card Design</Link>.</p>
    </>
  )
}

function PeopleStep({ o }: { o: Onboarding }) {
  return (
    <>
      <p className="muted">
        Who can sign in. Investment Group Administrators can do everything; owners run their own restaurants; managers do what they are allowed at the restaurants
        they work at; a shared team login at each restaurant only enters cards.
      </p>
      {o.people.length === 0 ? <p>Nobody else has an account yet.</p> : (
        <div className="table-wrap">
          <table className="compact">
            <thead><tr><th>Name</th><th>Role</th><th /></tr></thead>
            <tbody>{o.people.map((p) => <tr key={`${p.name}|${p.role}`}><td>{p.name}</td><td>{p.role}</td><td>{p.notSignedInYet && <Badge kind="neutral">has not signed in yet</Badge>}</td></tr>)}</tbody>
          </table>
        </div>
      )}
      <p style={{ marginTop: '0.75rem' }}>
        <Link className="btn" to="/users" target="_blank" rel="noreferrer">Add or change people on All Staff</Link>{' '}
        <span className="muted small">Opens in a new tab, so you can come back here. This can also wait until after set-up.</span>
      </p>
      <p className="muted small">What owners and managers may do is on <Link to="/permissions">Permissions</Link>.</p>
    </>
  )
}

function AssistantStep({ o }: { o: Onboarding }) {
  const a = o.assistant
  if (!a) return null
  return (
    <>
      <p className="muted">
        The AI assistant on Enter Card reads what someone types about a guest’s visit and fills in the card for them to check, and suggests the reward. You are
        charged for each use, on your monthly bill.
      </p>
      <dl className="kv">
        <dt>Now</dt>
        <dd>{a.on ? <><Badge kind="ok">on</Badge> at the {a.level} level, {a.price} a request.</> : <><Badge kind="neutral">off</Badge> Nobody sees it until it is switched on.</>}</dd>
        <dt>Who decides</dt>
        <dd>{a.groupDecides ? 'Your investment group. Switch it on or off, and choose its level and rewards, on Billing.' : 'The people who run the site switched it on for you. You choose its level and rewards on Billing.'}</dd>
      </dl>
      <p><Link className="btn" to="/billing" target="_blank" rel="noreferrer">Open the assistant’s settings on Billing</Link></p>
    </>
  )
}
