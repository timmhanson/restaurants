import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { api, qs } from '../api/client'
import type { CommentSummary, MailStatus, PagedResult } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import CommentDrawer from '../components/CommentDrawer'
import DataTable, { type Column } from '../components/DataTable'
import { FollowUpBadge, followUpDue } from '../components/FollowUp'
import SendAgainModal from '../components/SendAgainModal'
import Stat from '../components/Stat'
import { Alert, Badge, Spinner, fmtDate, fmtDay, today } from '../components/ui'
import { MAIL_STATUS, MailStatusBadge } from './MailCardsPage'

/** How many cards are fetched at a time, and the most the page loads. */
const PAGE = 500
const MOST = 5000

const daysAgo = (n: number) => {
  const d = new Date()
  d.setDate(d.getDate() - n)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

type Show = 'open' | 'reply' | 'call' | 'all'
const SHOWS: Show[] = ['open', 'reply', 'call', 'all']

/** A card that is with the mailing service, or has been (a cancelled one never went). */
const mailedForYou = (c: CommentSummary) => Boolean(c.mailStatus && c.mailStatus !== 'cancelled')
const wasSent = (c: CommentSummary) => Boolean(c.sentAt)
const howSent = (c: CommentSummary) => (!c.sentAt ? null : mailedForYou(c) ? 'Mailed for you' : 'Printed here, stamp by hand')
const guest = (c: CommentSummary) => `${c.guestLastName}, ${c.guestFirstName}`.replace(/^, |, $/g, '')
/** What is waiting to be done for a guest: a reply, and the "did the card arrive?" call. */
const needsReply = (c: CommentSummary) => !c.responded
const needsAction = (c: CommentSummary) => needsReply(c) || followUpDue(c)
const toDo = (c: CommentSummary) =>
  [needsReply(c) ? (c.attemptCount > 0 ? 'Try the guest again' : 'Reply to the guest') : null, followUpDue(c) ? 'Call: did the card arrive?' : null, wasSent(c) ? null : 'Send the card'].filter(Boolean) as string[]

/**
 * The work list for following up with guests. A guest's comment is finished with when a person has replied to the
 * guest, the coupon card has been sent, and, some days after it went, someone has checked that it arrived. Sending
 * the card is not replying: a card can be on its way while the guest still waits to hear from somebody.
 *
 * Each row shows what is left to do, when the card was sent and when it really went into the mail, and whether the
 * guest has been replied to. "Open" records a reply, an attempt or the follow-up call; "History" lists every attempt
 * to send the card with the address used, and sends it again. "Every card" turns the page into a report by date.
 */
export default function FollowUpPage() {
  const { restaurants, groupId, has } = useAuth()
  const [params] = useSearchParams()
  const wanted = params.get('show') as Show | null
  const [show, setShow] = useState<Show>(wanted && SHOWS.includes(wanted) ? wanted : 'open')
  const [from, setFrom] = useState(daysAgo(90))
  const [to, setTo] = useState(today())
  const [cards, setCards] = useState<CommentSummary[] | null>(null)
  const [total, setTotal] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [restaurantId, setRestaurantId] = useState('')
  const [sent, setSent] = useState<'' | 'yes' | 'no' | 'mail' | 'hand'>('')
  const [status, setStatus] = useState<'' | MailStatus>('')
  const [find, setFind] = useState('')
  /** The card whose details and follow-up are open, and the one whose history of sends is open. */
  const [opened, setOpened] = useState<CommentSummary | null>(null)
  const [replyFirst, setReplyFirst] = useState(false)
  const [history, setHistory] = useState<CommentSummary | null>(null)

  useEffect(() => {
    if (wanted && SHOWS.includes(wanted)) setShow(wanted)
  }, [wanted])

  // The work list holds everything still waiting, whenever the guest visited; "Every card" goes by the dates.
  const everything = show === 'all'
  useEffect(() => {
    let stale = false
    setCards(null)
    setError(null)
    ;(async () => {
      // Where each mailed card is, asked of the mailing service now, so no status is left standing. Best effort:
      // the list shows what is known if the mailing service cannot be reached.
      await api.post('/comments/mail/refresh').catch(() => undefined)
      const all: CommentSummary[] = []
      let count = 0
      const query = everything ? { enteredFrom: from, enteredTo: to, sort: 'createdAt', dir: 'desc' } : { status: 'open', sort: 'visitDate', dir: 'asc' }
      for (let page = 1; page <= MOST / PAGE; page++) {
        const r = await api.get<PagedResult<CommentSummary>>(`/comments${qs({ ...query, page, pageSize: PAGE })}`)
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
  }, [everything, from, to, groupId])

  const rows = useMemo(() => {
    const text = find.trim().toLowerCase()
    return (cards ?? []).filter((c) => {
      if (show === 'open' && !needsAction(c)) return false
      if (show === 'reply' && !needsReply(c)) return false
      if (show === 'call' && !followUpDue(c)) return false
      if (restaurantId && c.restaurantId !== Number(restaurantId)) return false
      if (sent === 'yes' && !wasSent(c)) return false
      if (sent === 'no' && wasSent(c)) return false
      if (sent === 'mail' && !(wasSent(c) && mailedForYou(c))) return false
      if (sent === 'hand' && !(wasSent(c) && !mailedForYou(c))) return false
      if (status && c.mailStatus !== status) return false
      if (text && !`${c.guestFirstName} ${c.guestLastName} ${c.couponOffer ?? ''} ${c.city ?? ''} ${c.phone ?? ''}`.toLowerCase().includes(text)) return false
      return true
    })
  }, [cards, show, restaurantId, sent, status, find])

  const replace = (next: CommentSummary) => {
    setCards((list) => list && list.map((c) => (c.id === next.id ? { ...c, ...next, sends: next.sends ?? c.sends } : c)))
    setOpened((o) => (o && o.id === next.id ? { ...o, ...next } : o))
  }
  const open = (c: CommentSummary, reply = false) => {
    setReplyFirst(reply)
    setOpened(c)
  }

  const canAct = has('MarkResponded')
  const columns: Column<CommentSummary>[] = [
    { key: 'guest', label: 'Guest', value: guest, render: (c) => <><strong>{c.guestFirstName} {c.guestLastName}</strong>{c.phone && <div className="muted small">{c.phone}</div>}</> },
    ...(restaurants.length > 1 ? [{ key: 'restaurant', label: 'Restaurant', value: (c: CommentSummary) => c.restaurantName }] : []),
    { key: 'visit', label: 'Visit', value: (c) => c.visitDate, render: (c) => <>{fmtDate(c.visitDate)}<div className="muted small">{c.reason}</div></> },
    {
      key: 'todo', label: 'To do', value: (c) => toDo(c).join('; ') || null,
      render: (c) => (toDo(c).length === 0
        ? <Badge kind="ok">nothing</Badge>
        : <>{toDo(c).map((t) => <div key={t}><Badge kind={t === 'Send the card' ? 'neutral' : t.startsWith('Call') ? 'warn' : 'danger'}>{t}</Badge></div>)}</>),
    },
    { key: 'replied', label: 'Replied to', value: (c) => (c.responded ? c.repliedOn ?? c.respondedAt ?? 'yes' : null), render: (c) => <FollowUpBadge c={c} /> },
    { key: 'replyMethod', label: 'How replied', value: (c) => (c.responded ? c.replyMethod ?? null : null), csvOnly: true },
    { key: 'repliedBy', label: 'Replied by', value: (c) => (c.responded ? c.respondedByName ?? null : null), csvOnly: true },
    {
      key: 'sent', label: 'Card sent', value: (c) => c.sentAt ?? null,
      render: (c) => (c.sentAt ? <>{fmtDay(c.sentAt)}<div className="muted small">{howSent(c)}</div></> : <Badge kind="warn">not sent yet</Badge>),
    },
    { key: 'how', label: 'How it was sent', value: howSent, csvOnly: true },
    { key: 'sends', label: 'Times sent', num: true, value: (c) => c.sends ?? 0, csvOnly: true },
    {
      key: 'inMail', label: 'In the mail', value: (c) => c.inMailAt ?? null,
      render: (c) => (c.inMailAt
        ? <span title="The day the mailing service handed the card to the post office">{fmtDay(c.inMailAt)}<div className="muted small">{MAIL_STATUS[c.mailStatus as MailStatus]?.label ?? ''}</div></span>
        : mailedForYou(c) ? <MailStatusBadge status={c.mailStatus as MailStatus} test={c.mailTest} />
          : <span className="muted" title="Only cards printed and mailed for you are tracked into the mail">—</span>),
    },
    { key: 'status', label: 'Mailing status', value: (c) => (c.mailStatus ? `${MAIL_STATUS[c.mailStatus as MailStatus]?.label ?? c.mailStatus}${c.mailTest ? ' (test order)' : ''}` : null), csvOnly: true },
    {
      key: 'call', label: 'Follow-up call', value: (c) => c.followedUpOn ?? c.followUpDueOn ?? null,
      render: (c) => (c.followedUpOn ? <>Done {fmtDate(c.followedUpOn)}<div className="muted small">{c.followedUpByName}</div></>
        : c.followUpDueOn ? (followUpDue(c) ? <Badge kind="warn">due since {fmtDate(c.followUpDueOn)}</Badge> : <span className="muted">from {fmtDate(c.followUpDueOn)}</span>)
          : <span className="muted">—</span>),
    },
    { key: 'redeemed', label: 'Coupon used', value: (c) => c.redeemedOn ?? null, render: (c) => (c.redeemedOn ? fmtDate(c.redeemedOn) : <span className="muted">—</span>) },
    {
      key: 'actions', label: '', value: () => null, noSort: true, noCsv: true,
      render: (c) => (
        <div className="row-actions" style={{ flexWrap: 'nowrap' }}>
          {canAct && needsReply(c)
            ? <button type="button" className="btn btn-sm btn-primary" onClick={() => open(c, true)} aria-label={`Record a reply to ${c.guestFirstName} ${c.guestLastName}`}>Reply</button>
            : <button type="button" className={`btn btn-sm ${canAct && followUpDue(c) ? 'btn-primary' : ''}`} onClick={() => open(c)} aria-label={`Open ${c.guestFirstName} ${c.guestLastName}’s card`}>Open</button>}
          <button type="button" className="btn btn-sm" onClick={() => setHistory(c)} aria-label="Show this card’s history of sends, or send it again">
            {canAct ? (wasSent(c) ? 'History · Send again' : 'History · Send') : 'History'}
          </button>
        </div>
      ),
    },
  ]

  const all = cards ?? []
  return (
    <>
      <div className="page-head"><h1>Follow up</h1></div>
      <p className="muted">
        Who still needs something from you. A guest is finished with when someone has <strong>replied</strong> to them, their coupon card has been <strong>sent</strong>, and
        someone has checked it arrived. Sending a card does not count as replying. Cards printed and mailed for you show the day they really went into the mail; that is
        checked with the mailing service each time this page opens. Click a column’s heading to sort by it.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="panel">
        <div className="filters" style={{ marginBottom: 0 }}>
          <label>Show
            <select value={show} onChange={(e) => setShow(e.target.value as Show)}>
              <option value="open">Everything waiting</option>
              <option value="reply">Guests waiting for a reply</option>
              <option value="call">Follow-up calls due</option>
              <option value="all">Every card, by date</option>
            </select>
          </label>
          {everything && (
            <>
              <label>Created from<input type="date" value={from} max={to} onChange={(e) => e.target.value && setFrom(e.target.value)} /></label>
              <label>to<input type="date" value={to} min={from} onChange={(e) => e.target.value && setTo(e.target.value)} /></label>
            </>
          )}
          {restaurants.length > 1 && (
            <label>Restaurant
              <select value={restaurantId} onChange={(e) => setRestaurantId(e.target.value)}>
                <option value="">All my restaurants</option>
                {restaurants.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
              </select>
            </label>
          )}
          <label>Card
            <select value={sent} onChange={(e) => setSent(e.target.value as typeof sent)}>
              <option value="">Sent or not</option>
              <option value="no">Not sent yet</option>
              <option value="yes">Sent</option>
              <option value="mail">Sent: mailed for you</option>
              <option value="hand">Sent: printed here, stamp by hand</option>
            </select>
          </label>
          <label>Mailing status
            <select value={status} onChange={(e) => setStatus(e.target.value as typeof status)}>
              <option value="">Any</option>
              {(Object.keys(MAIL_STATUS) as MailStatus[]).map((s) => <option key={s} value={s}>{MAIL_STATUS[s].label}</option>)}
            </select>
          </label>
          <label>Find<input value={find} onChange={(e) => setFind(e.target.value)} placeholder="Guest, phone, offer or city" aria-label="Find a guest, a phone number, an offer or a city" /></label>
        </div>
      </div>
      {!cards ? (!error && <Spinner label="Loading…" />) : (
        <>
          <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
            <Stat value={all.filter(needsReply).length.toLocaleString()} label="guests waiting for a reply" />
            <Stat value={all.filter(followUpDue).length.toLocaleString()} label="follow-up calls due" />
            <Stat value={all.filter((c) => !wasSent(c)).length.toLocaleString()} label="of these, card not sent yet" />
            <Stat value={all.filter((c) => mailedForYou(c) && !c.inMailAt && !c.mailTest).length.toLocaleString()} label="of these, mailed for you but not in the mail yet" />
          </div>
          {total > cards.length && <Alert kind="warn">There are {total.toLocaleString()} cards; the first {cards.length.toLocaleString()} are shown.{everything ? ' Choose a shorter range of dates to see the rest.' : ''}</Alert>}
          <DataTable<CommentSummary>
            title={`${rows.length.toLocaleString()} card${rows.length === 1 ? '' : 's'}`}
            rows={rows}
            columns={columns}
            rowKey={(c) => c.id}
            filename={`follow-up-${today()}.csv`}
            defaultSort={everything ? { key: 'sent', desc: true } : { key: 'visit', desc: false }}
            download={has('ExportComments')}
            empty={cards.length === 0 ? (everything ? 'No cards were created in these dates.' : 'Nothing is waiting: every guest has been replied to and no follow-up call is due.') : 'No cards match these filters.'}
          />
        </>
      )}
      {opened && <CommentDrawer comment={opened} onClose={() => setOpened(null)} onChanged={replace} startReply={replyFirst} />}
      {history && <SendAgainModal card={history} onClose={() => setHistory(null)} onChanged={replace} />}
    </>
  )
}
