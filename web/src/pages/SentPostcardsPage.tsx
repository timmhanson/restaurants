import { useEffect, useMemo, useState } from 'react'
import { api, qs } from '../api/client'
import type { CommentSummary, MailStatus, PagedResult } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import DataTable, { type Column } from '../components/DataTable'
import SendAgainModal from '../components/SendAgainModal'
import Stat from '../components/Stat'
import { Alert, Badge, Spinner, fmtDate, fmtDay, today } from '../components/ui'
import { MAIL_STATUS, MailStatusBadge } from './MailCardsPage'

/** How many cards are fetched at a time, and the most the report loads for one range of dates. */
const PAGE = 500
const MOST = 5000

const daysAgo = (n: number) => {
  const d = new Date()
  d.setDate(d.getDate() - n)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

/** A card that is with the mailing service, or has been (a cancelled one never went). */
const mailedForYou = (c: CommentSummary) => Boolean(c.mailStatus && c.mailStatus !== 'cancelled')
/** A reply that was a postcard, as opposed to a phone call, an email or a word in person. */
const repliedByCard = (c: CommentSummary) => c.responded && (Boolean(c.printedAt) || /mail|card/i.test(c.replyMethod ?? ''))
const wasSent = (c: CommentSummary) => mailedForYou(c) || repliedByCard(c)
/** The day the postcard went: handed to the mailing service, or marked as sent after printing. */
const sentAt = (c: CommentSummary) => (mailedForYou(c) ? c.mailedAt ?? c.respondedAt : repliedByCard(c) ? c.respondedAt : null) ?? null
const howSent = (c: CommentSummary) => (mailedForYou(c) ? 'Mailed for you' : repliedByCard(c) ? 'Printed here, stamp by hand' : null)
const guest = (c: CommentSummary) => `${c.guestLastName}, ${c.guestFirstName}`.replace(/^, |, $/g, '')

/**
 * The postcards a restaurant has sent, as a report: when each card was created, when it was sent and how, whether
 * the guest has been replied to and when, and, for cards printed and mailed for the restaurant, where the card is.
 * Every column sorts; the filters narrow by date, restaurant, sent or not, how it was sent, mailing status and reply.
 * For anyone who may see guest comments, for the restaurants they work at.
 */
export default function SentPostcardsPage() {
  const { restaurants, groupId, has } = useAuth()
  const [from, setFrom] = useState(daysAgo(90))
  const [to, setTo] = useState(today())
  const [cards, setCards] = useState<CommentSummary[] | null>(null)
  const [total, setTotal] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [restaurantId, setRestaurantId] = useState('')
  const [show, setShow] = useState<'sent' | 'notsent' | 'all'>('sent')
  const [how, setHow] = useState<'' | 'mail' | 'hand'>('')
  const [status, setStatus] = useState<'' | MailStatus>('')
  const [replied, setReplied] = useState<'' | 'yes' | 'no'>('')
  const [find, setFind] = useState('')
  /** The card whose history of sends is open. */
  const [opened, setOpened] = useState<CommentSummary | null>(null)

  useEffect(() => {
    let stale = false
    setCards(null)
    setError(null)
    ;(async () => {
      // Where each mailed card is, asked of the mailing service now, so no status is left standing. Best effort:
      // the report shows what is known if the mailing service cannot be reached.
      await api.post('/comments/mail/refresh').catch(() => undefined)
      const all: CommentSummary[] = []
      let count = 0
      for (let page = 1; page <= MOST / PAGE; page++) {
        const r = await api.get<PagedResult<CommentSummary>>(`/comments${qs({ enteredFrom: from, enteredTo: to, sort: 'createdAt', dir: 'desc', page, pageSize: PAGE })}`)
        all.push(...r.items)
        count = r.total
        if (all.length >= r.total || r.items.length === 0) break
      }
      if (!stale) {
        setCards(all)
        setTotal(count)
      }
    })().catch((e) => { if (!stale) setError((e as Error).message) })
    return () => { stale = true }
  }, [from, to, groupId])

  const rows = useMemo(() => {
    const text = find.trim().toLowerCase()
    return (cards ?? []).filter((c) => {
      if (restaurantId && c.restaurantId !== Number(restaurantId)) return false
      if (show === 'sent' && !wasSent(c)) return false
      if (show === 'notsent' && wasSent(c)) return false
      if (how === 'mail' && !mailedForYou(c)) return false
      if (how === 'hand' && (mailedForYou(c) || !repliedByCard(c))) return false
      if (status && c.mailStatus !== status) return false
      if (replied === 'yes' && !c.responded) return false
      if (replied === 'no' && c.responded) return false
      if (text && !`${c.guestFirstName} ${c.guestLastName} ${c.couponOffer ?? ''} ${c.city ?? ''}`.toLowerCase().includes(text)) return false
      return true
    })
  }, [cards, restaurantId, show, how, status, replied, find])

  const columns: Column<CommentSummary>[] = [
    { key: 'guest', label: 'Guest', value: guest, render: (c) => <strong>{c.guestFirstName} {c.guestLastName}</strong> },
    ...(restaurants.length > 1 ? [{ key: 'restaurant', label: 'Restaurant', value: (c: CommentSummary) => c.restaurantName }] : []),
    { key: 'offer', label: 'Offer', value: (c) => c.couponOffer ?? null },
    { key: 'created', label: 'Created', value: (c) => c.createdAt, render: (c) => <>{fmtDay(c.createdAt)}<div className="muted small">{c.createdByName}</div></> },
    {
      key: 'sent', label: 'Sent', value: sentAt,
      render: (c) => (sentAt(c) ? <>{fmtDay(sentAt(c))}<div className="muted small">{howSent(c)}</div></> : <Badge kind="warn">not sent yet</Badge>),
    },
    { key: 'how', label: 'How it was sent', value: howSent, csvOnly: true },
    {
      key: 'sends', label: 'Times sent', num: true, value: (c) => c.sends ?? 0,
      render: (c) => ((c.sends ?? 0) > 1 ? <Badge kind="accent" title="This card has been sent more than once">{c.sends}</Badge> : <>{c.sends ?? 0}</>),
    },
    {
      key: 'status', label: 'Mailing status', value: (c) => (c.mailStatus ? `${MAIL_STATUS[c.mailStatus as MailStatus]?.label ?? c.mailStatus}${c.mailTest ? ' (test order)' : ''}` : null),
      render: (c) => (c.mailStatus ? <MailStatusBadge status={c.mailStatus as MailStatus} test={c.mailTest} /> : <span className="muted" title="Only cards printed and mailed for you have a mailing status">—</span>),
    },
    {
      key: 'replied', label: 'Replied to', value: (c) => (c.responded ? c.respondedAt ?? 'yes' : null),
      render: (c) => (c.responded
        ? <><Badge kind="ok">Replied</Badge> {fmtDay(c.respondedAt)}<div className="muted small">{[c.replyMethod, c.respondedByName].filter(Boolean).join(' · ')}</div></>
        : <Badge kind="neutral">not yet</Badge>),
    },
    { key: 'replyMethod', label: 'How replied', value: (c) => (c.responded ? c.replyMethod ?? null : null), csvOnly: true },
    { key: 'redeemed', label: 'Coupon used', value: (c) => c.redeemedOn ?? null, render: (c) => (c.redeemedOn ? fmtDate(c.redeemedOn) : <span className="muted">—</span>) },
    {
      key: 'actions', label: '', value: () => null, noSort: true, noCsv: true,
      render: (c) => (
        <button type="button" className="btn btn-sm" onClick={() => setOpened(c)} aria-label="Show this card’s history of sends, or send it again">
          {has('MarkResponded') ? (wasSent(c) ? 'History · Send again' : 'History · Send') : 'History'}
        </button>
      ),
    },
  ]

  const sent = (cards ?? []).filter(wasSent)
  return (
    <>
      <div className="page-head"><h1>Sent postcards</h1></div>
      <p className="muted">
        Every coupon postcard, with when it was created, when it was sent and how, and whether the guest has been replied to. Cards printed and mailed for you also show
        where they are; that is checked with the mailing service each time this page opens. Click a column’s heading to sort by it. A card’s
        <strong> History</strong> lists every attempt to send it with the address used, and lets it be sent again.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="panel">
        <div className="filters" style={{ marginBottom: 0 }}>
          <label>Created from<input type="date" value={from} max={to} onChange={(e) => e.target.value && setFrom(e.target.value)} /></label>
          <label>to<input type="date" value={to} min={from} onChange={(e) => e.target.value && setTo(e.target.value)} /></label>
          {restaurants.length > 1 && (
            <label>Restaurant
              <select value={restaurantId} onChange={(e) => setRestaurantId(e.target.value)}>
                <option value="">All my restaurants</option>
                {restaurants.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
              </select>
            </label>
          )}
          <label>Show
            <select value={show} onChange={(e) => setShow(e.target.value as typeof show)}>
              <option value="sent">Sent postcards</option>
              <option value="notsent">Not sent yet</option>
              <option value="all">Every card</option>
            </select>
          </label>
          <label>How it was sent
            <select value={how} onChange={(e) => setHow(e.target.value as typeof how)}>
              <option value="">Either way</option>
              <option value="mail">Mailed for you</option>
              <option value="hand">Printed here, stamp by hand</option>
            </select>
          </label>
          <label>Mailing status
            <select value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
              <option value="">Any</option>
              {(Object.keys(MAIL_STATUS) as MailStatus[]).map((s) => <option key={s} value={s}>{MAIL_STATUS[s].label}</option>)}
            </select>
          </label>
          <label>Replied to
            <select value={replied} onChange={(e) => setReplied(e.target.value as typeof replied)}>
              <option value="">Either</option>
              <option value="yes">Replied</option>
              <option value="no">Not replied yet</option>
            </select>
          </label>
          <label>Find<input value={find} onChange={(e) => setFind(e.target.value)} placeholder="Guest, offer or city" aria-label="Find a guest, an offer or a city" /></label>
        </div>
      </div>
      {!cards ? (!error && <Spinner label="Loading…" />) : (
        <>
          <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
            <Stat value={sent.length.toLocaleString()} label={`postcards sent, of ${cards.length.toLocaleString()} cards created in these dates`} />
            <Stat value={sent.filter(mailedForYou).length.toLocaleString()} label="mailed for you" />
            <Stat value={sent.filter((c) => !mailedForYou(c)).length.toLocaleString()} label="printed here, stamp by hand" />
            <Stat value={sent.filter((c) => c.redeemedOn).length.toLocaleString()} label="coupons used so far" />
          </div>
          {total > cards.length && <Alert kind="warn">These dates hold {total.toLocaleString()} cards; the newest {cards.length.toLocaleString()} are shown. Choose a shorter range of dates to see the rest.</Alert>}
          <DataTable<CommentSummary>
            title={`${rows.length.toLocaleString()} postcard${rows.length === 1 ? '' : 's'}`}
            rows={rows}
            columns={columns}
            rowKey={(c) => c.id}
            filename={`sent-postcards-${from}-to-${to}.csv`}
            defaultSort={{ key: 'sent', desc: true }}
            download={has('ExportComments')}
            empty={cards.length === 0 ? 'No cards were created in these dates.' : 'No postcards match these filters.'}
          />
        </>
      )}
      {opened && (
        <SendAgainModal
          card={opened}
          onClose={() => setOpened(null)}
          onChanged={(next) => setCards((list) => list && list.map((c) => (c.id === next.id ? { ...c, ...next, sends: c.sends } : c)))}
        />
      )}
    </>
  )
}
