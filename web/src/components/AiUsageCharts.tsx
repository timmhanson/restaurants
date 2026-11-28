import { Link } from 'react-router-dom'
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { AiChange, AiCharts, AiRequestRow } from '../api/types'
import { OTHER, REPLIED, SERIES } from './palette'
import { Badge, Modal, fmtDateTime } from './ui'

/** The five faces, as on the card: 1 (a small slip) to 5 (furious). */
export const MOODS = ['🙂 A small slip', '😐 Let down', '🙁 Upset', '😠 Angry', '😡 Furious']
export const moodName = (level?: number | null) => (level && MOODS[level - 1]) || 'no face'
const aiDollars = (mills: number) => `$${(mills / 1000).toFixed(mills % 10 === 0 ? 2 : 3)}`
const pct = (n: number, of: number) => (of > 0 ? `${Math.round((n / of) * 100)}%` : '0%')

function Changes({ title, rows, empty }: { title: string; rows: AiChange[]; empty: string }) {
  return (
    <div>
      <h3 style={{ margin: '0 0 0.35rem' }}>{title}</h3>
      {rows.length === 0 ? <p className="muted small" style={{ margin: 0 }}>{empty}</p> : (
        <div className="table-wrap">
          <table className="compact">
            <thead><tr><th>The assistant suggested</th><th>The card was saved with</th><th style={{ textAlign: 'right' }}>Times</th></tr></thead>
            <tbody>{rows.map((c) => <tr key={`${c.from}>${c.to}`}><td>{c.from}</td><td>{c.to}</td><td style={{ textAlign: 'right' }}>{c.count}</td></tr>)}</tbody>
          </table>
        </div>
      )}
    </div>
  )
}

/**
 * The AI assistant's usage as graphs, for a group's own requests or (for the System Administrator) every group's:
 * how much it is used day by day, by level, by person and by restaurant; which faces the assistant picks beside
 * the faces that end up on the cards; and how often people keep or change its face and its reward, and to what.
 * The same figures are in the tables and CSV downloads beside it.
 */
export default function AiUsageCharts({ charts, forSite = false }: { charts: AiCharts; forSite?: boolean }) {
  const saved = charts.faceKept + charts.faceRaised + charts.faceLowered
  const kept = [
    { name: 'The face', Kept: charts.faceKept, 'Made more upset': charts.faceRaised, 'Made less upset': charts.faceLowered, Changed: 0 },
    { name: 'The reward', Kept: charts.rewardKept, 'Made more upset': 0, 'Made less upset': 0, Changed: charts.rewardChanged },
  ]
  const faces = charts.faces.map((f) => ({ name: `${f.face} ${f.label}`, 'The assistant’s pick': f.suggested, 'On the saved card': f.final }))
  const people = charts.byPerson.map((b) => ({ name: b.label, Requests: b.charged }))
  const places = charts.byRestaurant.map((b) => ({ name: b.label, Requests: b.charged }))
  const bar = (data: { name: string; Requests: number }[], title: string, note: string) => (
    <div className="panel" style={{ margin: 0 }}>
      <h3 style={{ marginTop: 0 }}>{title}</h3>
      <p className="muted small" style={{ margin: '0 0 0.5rem' }}>{note}</p>
      <div style={{ height: Math.max(140, 44 + data.length * 34) }}>
        <ResponsiveContainer>
          <BarChart data={data} layout="vertical" margin={{ left: 30, right: 16 }}>
            <CartesianGrid strokeDasharray="3 3" horizontal={false} />
            <XAxis type="number" allowDecimals={false} /><YAxis type="category" dataKey="name" width={130} /><Tooltip />
            <Bar dataKey="Requests" fill={SERIES[0]} radius={[0, 4, 4, 0]} />
          </BarChart>
        </ResponsiveContainer>
      </div>
    </div>
  )
  return (
    <div className="grid" style={{ gap: '1rem' }}>
      <div className="grid grid-2" style={{ gap: '1rem' }}>
        <div className="panel" style={{ margin: 0 }}>
          <h3 style={{ marginTop: 0 }}>Requests, day by day</h3>
          <p className="muted small" style={{ margin: '0 0 0.5rem' }}>Each press of Process. One that did not work is not charged.</p>
          <div className="chart-box">
            <ResponsiveContainer>
              <BarChart data={charts.byDay.map((b) => ({ name: b.label, Charged: b.charged, 'Did not work': b.failed }))}>
                <CartesianGrid strokeDasharray="3 3" vertical={false} />
                <XAxis dataKey="name" /><YAxis allowDecimals={false} /><Tooltip /><Legend />
                <Bar dataKey="Charged" stackId="a" fill={SERIES[0]} />
                <Bar dataKey="Did not work" stackId="a" fill={OTHER} radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
        <div className="panel" style={{ margin: 0 }}>
          <h3 style={{ marginTop: 0 }}>By level</h3>
          <p className="muted small" style={{ margin: '0 0 0.5rem' }}>
            {charts.byLevel.filter((b) => b.charged > 0).map((b) => `${b.label}: ${b.charged} for ${aiDollars(b.mills)}`).join(' · ') || 'No charged requests.'}
          </p>
          <div className="chart-box">
            <ResponsiveContainer>
              <BarChart data={charts.byLevel.map((b) => ({ name: b.label, Requests: b.charged }))}>
                <CartesianGrid strokeDasharray="3 3" vertical={false} />
                <XAxis dataKey="name" /><YAxis allowDecimals={false} /><Tooltip />
                <Bar dataKey="Requests" fill={SERIES[0]} radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
      </div>

      <div className="grid grid-2" style={{ gap: '1rem' }}>
        {bar(people, 'Who uses it', 'Charged requests by the person who pressed Process.')}
        {(forSite || places.length > 1) && bar(places, 'By restaurant', 'Charged requests at each restaurant.')}
      </div>

      <div className="grid grid-2" style={{ gap: '1rem' }}>
        <div className="panel" style={{ margin: 0 }}>
          <h3 style={{ marginTop: 0 }}>How upset guests were</h3>
          <p className="muted small" style={{ margin: '0 0 0.5rem' }}>The face the assistant picked, beside the face on the card as it was saved.</p>
          <div className="chart-box">
            <ResponsiveContainer>
              <BarChart data={faces}>
                <CartesianGrid strokeDasharray="3 3" vertical={false} />
                <XAxis dataKey="name" interval={0} tick={{ fontSize: 11 }} /><YAxis allowDecimals={false} /><Tooltip /><Legend />
                <Bar dataKey="The assistant’s pick" fill={SERIES[0]} radius={[4, 4, 0, 0]} />
                <Bar dataKey="On the saved card" fill={SERIES[1]} radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
        <div className="panel" style={{ margin: 0 }}>
          <h3 style={{ marginTop: 0 }}>Did people keep what it suggested?</h3>
          <p className="muted small" style={{ margin: '0 0 0.5rem' }}>
            Of {saved} card{saved === 1 ? '' : 's'} saved from the assistant: the face was kept on {pct(charts.faceKept, saved)}, the reward on {pct(charts.rewardKept, saved)}.
            {charts.noCard > 0 ? ` ${charts.noCard} charged request${charts.noCard === 1 ? '' : 's'} never became a card.` : ''}
          </p>
          <div style={{ height: 190 }}>
            <ResponsiveContainer>
              <BarChart data={kept} layout="vertical" margin={{ left: 20, right: 16 }}>
                <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                <XAxis type="number" allowDecimals={false} /><YAxis type="category" dataKey="name" width={90} /><Tooltip /><Legend />
                <Bar dataKey="Kept" stackId="a" fill={REPLIED} />
                <Bar dataKey="Made more upset" stackId="a" fill={SERIES[1]} />
                <Bar dataKey="Made less upset" stackId="a" fill={SERIES[0]} />
                <Bar dataKey="Changed" stackId="a" fill={SERIES[3]} radius={[0, 4, 4, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
      </div>

      <div className="grid grid-2" style={{ gap: '1rem' }}>
        <div className="panel" style={{ margin: 0 }}><Changes title="Faces that were changed" rows={charts.faceChanges} empty="Nobody has changed the assistant’s face." /></div>
        <div className="panel" style={{ margin: 0 }}><Changes title="Rewards that were changed" rows={charts.rewardChanges} empty="Nobody has changed the assistant’s reward." /></div>
      </div>
    </div>
  )
}

/**
 * One request in full: what was typed, what the assistant filled in from it, the level and effort it ran at, what
 * it was charged, and what went on the card in the end (the face and reward kept or changed). The System
 * Administrator also sees the model, the tokens and what it cost the site.
 */
export function AiRequestDetail({ row, forSite = false, onClose }: { row: AiRequestRow; forSite?: boolean; onClose: () => void }) {
  const f = row.fields
  const ok = row.outcome === 'ok'
  const faceChanged = row.commentId != null && row.finalMood != null && row.mood != null && row.finalMood !== row.mood
  const rewardChanged = row.commentId != null && (row.finalOffer ?? '').toLowerCase() !== (row.offer ?? '').toLowerCase()
  const line = (label: string, value?: string | null) => (value ? <><dt>{label}</dt><dd style={{ whiteSpace: 'pre-wrap' }}>{value}</dd></> : null)
  return (
    <Modal title={`Request · ${fmtDateTime(row.at)}`} onClose={onClose} wide>
      <div className="grid" data-no-track>
        <dl className="kv" style={{ margin: 0 }}>
          <dt>Who</dt><dd>{row.by} at {row.restaurant}{row.group ? ` (${row.group})` : ''}</dd>
          <dt>Level</dt><dd>{row.level}{forSite && row.model ? ` · ${row.model}` : ''}</dd>
          <dt>Effort</dt><dd>{row.effort ?? 'low'} <span className="muted">(how hard it was asked to think; kept low so a request is quick and its cost predictable)</span></dd>
          <dt>Charged</dt><dd>{ok ? aiDollars(row.priceMills) : <><Badge kind="neutral">not charged</Badge> It did not work.{forSite && row.error ? ` ${row.error}` : ''}</>}</dd>
          {forSite && <><dt>Cost to you</dt><dd>${((row.costMicros ?? 0) / 1_000_000).toFixed(4)} · {row.inputTokens ?? 0} tokens in, {row.outputTokens ?? 0} out</dd></>}
        </dl>

        <h3 style={{ margin: '0.5rem 0 0' }}>What was typed</h3>
        <div className="ai-typed">{row.input || <span className="muted">Nothing was kept.</span>}</div>

        {ok && f && (
          <>
            <h3 style={{ margin: '0.5rem 0 0' }}>What the assistant filled in</h3>
            <dl className="kv" style={{ margin: 0 }}>
              {line('Guest', [f.guestFirstName, f.guestLastName].filter(Boolean).join(' '))}
              {line('Phone', f.phone)}
              {line('Email', f.email)}
              {line('Address', [f.streetAddress, f.city, [f.state, f.zip].filter(Boolean).join(' ')].filter(Boolean).join(', '))}
              {line('Visit', f.visitDate)}
              {line('Came in via', f.source)}
              {line('Reason', f.reason)}
              {line('Details', f.details)}
              <dt>Face</dt><dd>{moodName(f.mood)}{f.moodWhy ? <div className="muted small">Why: {f.moodWhy}</div> : null}</dd>
              {line('Reward', row.offer)}
              {line('Email subject', f.emailSubject)}
              {line('Email', f.emailBody)}
            </dl>

            <h3 style={{ margin: '0.5rem 0 0' }}>What went on the card</h3>
            {row.commentId == null ? (
              <p style={{ margin: 0 }}><Badge kind="warn">no card saved</Badge> Process was pressed, and no card was saved from it.</p>
            ) : (
              <dl className="kv" style={{ margin: 0 }}>
                <dt>Card</dt><dd>{forSite ? `Card ${row.commentId}` : <Link to={`/comments?card=${row.commentId}`}>{row.guest || `Card ${row.commentId}`}</Link>}</dd>
                <dt>Face</dt>
                <dd>{faceChanged ? <><Badge kind="warn">changed</Badge> {moodName(row.mood)} → <strong>{moodName(row.finalMood)}</strong></> : <><Badge kind="ok">kept</Badge> {moodName(row.finalMood ?? row.mood)}</>}</dd>
                <dt>Reward</dt>
                <dd>{rewardChanged ? <><Badge kind="warn">changed</Badge> {row.offer || 'none'} → <strong>{row.finalOffer || 'none'}</strong></> : <><Badge kind="ok">kept</Badge> {row.finalOffer || row.offer || 'none'}</>}</dd>
              </dl>
            )}
          </>
        )}
        <div className="actions"><button type="button" className="btn" onClick={onClose}>Close</button></div>
      </div>
    </Modal>
  )
}
