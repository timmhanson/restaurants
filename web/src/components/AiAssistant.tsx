import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { api, qs } from '../api/client'
import type { AiConfig, AiFace, AiGroupSettings, AiProcessResponse, AiRequestRow, AiSettings, AiSite, AiUsage, AiUsageBucket, AiUsageList, ServiceTestResult } from '../api/types'
import DataTable, { type Column } from './DataTable'
import Stat from './Stat'
import { Alert, Badge, Confirm, Field, Spinner, fmtDateTime } from './ui'

/** Tenths of a cent as dollars: 30 is $0.03, 18 is $0.018. */
export const aiDollars = (mills: number) => `$${(mills / 1000).toFixed(mills % 10 === 0 ? 2 : 3)}`
const dollars = (cents: number) => `$${(cents / 100).toFixed(2)}`
const cost = (micros: number) => `$${(micros / 1_000_000).toFixed(micros < 10_000 ? 4 : 2)}`
const thisMonth = () => new Date().toISOString().slice(0, 7)

/** The row of faces: how upset the guest was. The one chosen stands for a reward; choosing another changes it. */
export function Faces({ faces, value, onPick, disabled }: { faces: AiFace[]; value: number | null; onPick: (face: AiFace) => void; disabled?: boolean }) {
  return (
    <div className="ai-faces" role="radiogroup" aria-label="How upset the guest was">
      {faces.map((f) => (
        <button key={f.level} type="button" role="radio" aria-checked={value === f.level} className={`ai-face ${value === f.level ? 'on' : ''}`} disabled={disabled}
          onClick={() => onPick(f)} aria-label={`${f.label}: ${f.reward ?? 'no reward set'}`} title={f.meaning}>
          <span className="ai-face-emoji" aria-hidden="true">{f.face}</span>
          <span className="ai-face-label">{f.label}</span>
          <span className="ai-face-reward">{f.reward ?? '—'}</span>
        </button>
      ))}
    </div>
  )
}

/**
 * The AI assistant at the top of Enter Card, shown only where it is switched on. The person types what happened in
 * their own words and presses Process: the card below is filled in for them to check, an email to the guest is
 * drafted (always: it costs nothing extra), and a face shows how upset the guest was, with the reward it stands for. Each press is charged
 * to the investment group at the price on the button; a press that fails is not charged. Nothing is saved until the
 * card is.
 */
export function AiCardPanel({ restaurantId, mood, onFilled, onMood }: {
  restaurantId: number
  mood: number | null
  onFilled: (answer: AiProcessResponse) => void
  onMood: (face: AiFace) => void
}) {
  const [config, setConfig] = useState<AiConfig | null>(null)
  const [text, setText] = useState('')
  const [faces, setFaces] = useState<AiFace[] | null>(null)
  const [why, setWhy] = useState<string | null>(null)
  const [done, setDone] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setConfig(null)
    if (!restaurantId) return
    api.get<AiConfig>(`/ai/config${qs({ restaurantId })}`).then(setConfig).catch(() => setConfig(null))
  }, [restaurantId])

  // Hidden entirely where the assistant is not switched on (or while that is not known yet).
  if (!config?.enabled) return null

  const process = async () => {
    setBusy(true)
    setError(null)
    try {
      // A key of its own for each press: if the same press is sent twice it is answered, and charged, once.
      const answer = await api.post<AiProcessResponse>('/ai/process', { requestKey: crypto.randomUUID(), restaurantId, text })
      setFaces(answer.faces)
      setWhy(answer.fields.moodWhy ?? null)
      setDone((n) => n + 1)
      onFilled(answer)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel ai-panel">
      <h2>Let the assistant fill in the card <Badge kind="accent">optional</Badge></h2>
      <p className="muted">
        Type what happened in your own words, or paste the guest’s message, then press Process. The card below is filled in for you to check and correct before
        you save it, and an email to the guest is drafted at the bottom of the card. Each press costs your investment group {config.price}.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <Field label="What happened">
        <textarea rows={4} value={text} maxLength={4000} onChange={(e) => setText(e.target.value)} data-no-track
          placeholder="e.g. Maria Lopez called, 815-555-0142. She came through the drive-thru last night, waited 20 minutes and her burger was cold. She was polite but said it is the second time." />
      </Field>
      <div className="row-actions" style={{ marginTop: '0.5rem' }}>
        <button type="button" className="btn btn-primary" onClick={() => void process()} disabled={busy || text.trim().length < 15}
          aria-label={`Process with the assistant, ${config.price}`}>
          {busy ? 'Reading…' : done > 0 ? `Process again · ${config.price}` : `Process · ${config.price}`}
        </button>
        {done > 0 && !busy && <span className="muted small">Filled in below. Check every field before saving{done > 1 ? ` (${done} presses, each charged)` : ''}.</span>}
      </div>
      {faces && (
        <div style={{ marginTop: '0.9rem' }}>
          <strong>How upset was the guest?</strong>{' '}
          <span className="muted small">The assistant’s pick is marked. Choose another face and the reward on the card changes with it.</span>
          <Faces faces={faces} value={mood} onPick={onMood} disabled={busy} />
          {why && <p className="muted small" style={{ margin: '0.35rem 0 0' }}>Why: {why}</p>}
        </div>
      )}
    </div>
  )
}

// ---- The group's own panel (on Billing): on or off, the level, the rewards, and every request ------------------

const requestColumns = (withRestaurant: boolean): Column<AiRequestRow>[] => [
  { key: 'at', label: 'When', value: (r) => r.at, render: (r) => fmtDateTime(r.at) },
  { key: 'by', label: 'Who', value: (r) => r.by },
  ...(withRestaurant ? [{ key: 'restaurant', label: 'Restaurant', value: (r: AiRequestRow) => r.restaurant }] : []),
  {
    key: 'card', label: 'Card it was for', value: (r) => r.guest ?? (r.commentId ? `card ${r.commentId}` : null),
    render: (r) => (r.outcome !== 'ok' ? <span className="muted">—</span>
      : r.commentId ? <Link to={`/comments?card=${r.commentId}`}>{r.guest || `Card ${r.commentId}`}</Link>
        : <>{r.guest ? `${r.guest} ` : ''}<Badge kind="warn" title="Process was pressed, and no card was saved from it">no card saved</Badge></>),
  },
  { key: 'reason', label: 'Reason', value: (r) => r.reason ?? null },
  { key: 'level', label: 'Level', value: (r) => r.level },
  {
    key: 'price', label: 'Charged', num: true, value: (r) => r.priceMills / 1000,
    render: (r) => (r.outcome === 'ok' ? aiDollars(r.priceMills) : <Badge kind="neutral" title="It did not work, so it was not charged">not charged</Badge>),
  },
]

/**
 * The AI assistant for an investment group, on its Billing page: whether it is on (and whose decision that is), the
 * level the group has chosen with what each costs, the reward each face stands for, what has been spent this month
 * against the limit, and every request with the card it was for. Each change saves as it is made.
 */
export function AiGroupPanel() {
  const [s, setS] = useState<AiSettings | null>(null)
  const [usage, setUsage] = useState<AiUsageList | null>(null)
  const [month, setMonth] = useState(thisMonth())
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get<AiSettings>('/ai/settings').then(setS).catch((e) => setError((e as Error).message))
  }, [])
  useEffect(() => {
    setUsage(null)
    api.get<AiUsageList>(`/ai/requests${qs({ month })}`).then(setUsage).catch(() => setUsage(null))
  }, [month])

  const save = async (change: { on?: boolean; level?: string; rewards?: (string | null)[] }) => {
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      setS(await api.put<AiSettings>('/ai/settings', change))
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (!s) return error ? <div className="panel"><h2>AI assistant</h2><Alert kind="error">{error}</Alert></div> : null
  // Nothing to show a group the assistant has not been offered to.
  if (!s.on && s.decidedBy === 'site') return null
  const level = s.levels.find((l) => l.id === s.level)
  const bySite = s.decidedBy === 'site'
  return (
    <div className="panel">
      <h2>AI assistant {s.on ? <Badge kind="ok">on</Badge> : <Badge kind="neutral">off</Badge>}</h2>
      <p className="muted">
        On Enter Card, the assistant reads what someone types about a guest’s visit and fills in the card, picks how upset the guest was (which sets the
        reward) and, when asked, drafts an email. A person always checks it before the card is saved. You are charged for each press of Process, on your monthly bill.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!s.ready && s.on && <Alert kind="warn">The assistant is switched on for you, but it is not set up on this site yet, so it does not appear on Enter Card. Ask the site’s administrator.</Alert>}
      <div className="grid grid-4 stat-grid" style={{ marginBottom: '0.75rem' }}>
        <Stat value={s.usedRequests.toLocaleString()} label="requests this month" />
        <Stat value={aiDollars(s.usedMills)} label={`charged this month, of your ${dollars(s.limitCents)} limit`} warn={s.usedMills >= s.limitCents * 10} />
        <Stat value={level?.price ?? '—'} label={`each request, at the ${level?.label ?? ''} level`} />
      </div>

      <h3>On or off</h3>
      {bySite ? (
        <p className="muted">It is on for all your restaurants. That was decided by the people who run the site.</p>
      ) : (
        <label className="check">
          <input type="checkbox" checked={s.on} disabled={busy || !s.canChange} onChange={(e) => void save({ on: e.target.checked })} />
          <span>Show the assistant on Enter Card, for all restaurants <span className="muted">(your investment group decides this)</span></span>
        </label>
      )}

      <h3>Level</h3>
      <div className="source-choice" role="radiogroup" aria-label="Level of the assistant">
        {s.levels.map((l) => (
          <label key={l.id} className={l.id === s.level ? 'selected' : ''} style={l.allowed ? undefined : { opacity: 0.55 }}>
            <input type="radio" name="ai-level" checked={l.id === s.level} disabled={busy || !s.canChange || !l.allowed} onChange={() => void save({ level: l.id })} />
            <span className="source-label">{l.label} · {l.price} a request</span>
            <span className="source-hint">{l.allowed ? l.says : 'Not offered to your investment group. Ask the people who run the site.'}</span>
          </label>
        ))}
      </div>

      <h3>The reward each face stands for</h3>
      <p className="muted">The assistant picks a face from what the guest went through. The reward beside it is what goes on the card, and whoever enters the card can pick another face.</p>
      <div className="ai-faces">
        {s.faces.map((f, i) => (
          <label key={f.level} className="ai-face static" title={f.meaning}>
            <span className="ai-face-emoji" aria-hidden="true">{f.face}</span>
            <span className="ai-face-label">{f.label}</span>
            <select value={f.reward ?? ''} disabled={busy || !s.canChange} aria-label={`Reward for ${f.label}`}
              onChange={(e) => void save({ rewards: s.faces.map((x, j) => (j === i ? e.target.value : x.reward ?? '')) })}>
              {s.offers.map((o) => <option key={o} value={o}>{o}</option>)}
            </select>
          </label>
        ))}
      </div>
      {saved && <p className="muted small" style={{ margin: '0.4rem 0 0' }}>Saved.</p>}
      <p className="muted small" style={{ margin: '0.4rem 0 0' }}>
        Who can press Process is a permission, “Use the AI assistant”: administrators and owners can unless that has been switched off, managers only when it is
        given to them, and the shared team login never. It is set with the other permissions.
      </p>
      {!s.canChange && <p className="muted small">Your investment group’s administrators choose the level and the rewards.</p>}

      <h3 style={{ marginTop: '1.25rem' }}>Every request</h3>
      <div className="filters">
        <label>Month<input type="month" value={month} max={thisMonth()} onChange={(e) => e.target.value && setMonth(e.target.value)} /></label>
      </div>
      {!usage ? <Spinner label="Loading…" /> : (
        <>
          <p className="muted">
            {usage.requests.toLocaleString()} press{usage.requests === 1 ? '' : 'es'} of Process: {usage.charged.toLocaleString()} charged ({aiDollars(usage.mills)})
            {usage.requests > usage.charged ? `, ${usage.requests - usage.charged} that did not work and were not charged` : ''}
            {usage.notSaved > 0 ? `. ${usage.notSaved} of the charged ones did not become a saved card.` : '.'}
          </p>
          <DataTable<AiRequestRow> title="Requests" rows={usage.rows} columns={requestColumns(true)} rowKey={(r) => r.id} filename={`ai-assistant-${month}.csv`}
            defaultSort={{ key: 'at', desc: true }} empty="No requests in this month." />
        </>
      )}
    </div>
  )
}

// ---- The System Administrator: the site's key --------------------------------------------------------------------

/** Connected Systems: the site's Anthropic API key, and what each level runs on, costs the site and is charged at. */
export function AiSitePanel({ onChanged }: { onChanged?: () => void }) {
  const [site, setSite] = useState<AiSite | null>(null)
  const [key, setKey] = useState('')
  const [entering, setEntering] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [test, setTest] = useState<ServiceTestResult | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | 'remove' | null>(null)

  useEffect(() => {
    api.get<AiSite>('/system/ai').then(setSite).catch((e) => setError((e as Error).message))
  }, [])

  const runTest = async () => {
    setBusy('test')
    setTest(null)
    setError(null)
    try {
      setTest(await api.post<ServiceTestResult>('/system/ai/test'))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }
  const save = async (e: FormEvent) => {
    e.preventDefault()
    setBusy('save')
    setError(null)
    setNotice(null)
    try {
      setSite(await api.put<AiSite>('/system/ai', { apiKey: key }))
      setKey('')
      setEntering(false)
      setNotice('Saved.')
      onChanged?.()
      setBusy(null)
      await runTest()
    } catch (err) {
      setError((err as Error).message)
      setBusy(null)
    }
  }
  const remove = async () => {
    setBusy('remove')
    setError(null)
    try {
      setSite(await api.delete<AiSite>('/system/ai'))
      setNotice('Removed. The assistant no longer appears on Enter Card for any group.')
      onChanged?.()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(null)
      setRemoving(false)
    }
  }

  return (
    <div className="panel">
      <h2>Claude (Anthropic)</h2>
      <p className="muted">
        Runs the AI assistant on Enter Card. It is off for every investment group until you switch it on for the group, or leave that to the group, under{' '}
        <Link to="/groups">Investment groups</Link> (Edit). Create an API key in Anthropic’s Console (API keys) and enter it here: Anthropic bills this account
        for what is used, and you bill each group per request. Groups see “the assistant” and its levels, never Anthropic’s or Claude’s name.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {test && <Alert kind={test.ok ? 'success' : 'error'} onClose={() => setTest(null)}>{test.message}</Alert>}
      {!site ? (!error && <Spinner />) : (
        <>
          {!site.canEnter ? (
            <Alert kind="info">This copy of the site uses a stand-in for the assistant, so no key is needed here.</Alert>
          ) : (
            <>
              {site.configured ? (
                <dl className="kv">
                  <dt>Key</dt>
                  <dd><Badge kind="accent">saved</Badge> ending in <code>{site.keyEnding}</code></dd>
                  <dt>Entered</dt>
                  <dd>{site.source === 'Saved in the site' ? `${site.savedAt ? fmtDateTime(site.savedAt) : ''}${site.savedBy ? ` by ${site.savedBy}` : ''}` : 'In the web app’s settings in Azure. Entering a key here takes over from it.'}</dd>
                </dl>
              ) : (
                <p><Badge kind="neutral">not set up</Badge> Until a key is entered the assistant does not appear for anyone.</p>
              )}
              {entering || !site.configured ? (
                <form onSubmit={save} className="grid" style={{ maxWidth: 560, marginTop: '0.75rem' }}>
                  <Field label="Anthropic API key (begins sk-ant-)" hint="Stored encrypted and never shown again.">
                    <input type="password" value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} required minLength={20} />
                  </Field>
                  <div className="row-actions">
                    {entering && <button type="button" className="btn" onClick={() => { setEntering(false); setKey('') }} disabled={busy !== null}>Cancel</button>}
                    <button type="submit" className="btn btn-primary" disabled={busy !== null || key.trim().length < 20}>{busy === 'save' ? 'Saving…' : 'Save and test'}</button>
                  </div>
                </form>
              ) : (
                <div className="row-actions" style={{ marginTop: '0.75rem' }}>
                  <button type="button" className="btn btn-primary" onClick={() => void runTest()} disabled={busy !== null} title="Reads one made-up comment at the quickest level: a fraction of a cent">{busy === 'test' ? 'Testing…' : 'Test connection'}</button>
                  <button type="button" className="btn" onClick={() => setEntering(true)} disabled={busy !== null}>Replace key</button>
                  {site.source === 'Saved in the site' && <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy !== null}>Remove</button>}
                </div>
              )}
            </>
          )}
          <h3 style={{ marginTop: '1rem' }}>Levels</h3>
          <div className="table-wrap">
            <table className="compact">
              <thead><tr><th>What groups see</th><th>Model</th><th>Charged to the group</th><th>Costs you, per million tokens</th></tr></thead>
              <tbody>
                {site.levels.map((l) => (
                  <tr key={l.id}><td>{l.label}</td><td>{l.model}</td><td>{l.price} a request</td><td>${l.inPerMillion} in · ${l.outPerMillion} out</td></tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="muted small" style={{ marginBottom: 0 }}>
            A request is about 2,000 tokens in and under 1,000 out, so what a request costs you is below what the group is charged at every level. What each one really
            cost is on <Link to="/reports">Reports</Link>, under AI assistant. A group’s monthly limit is {dollars(site.defaultLimitCents)} unless you set its own.
          </p>
        </>
      )}
      {removing && (
        <Confirm title="Remove the Anthropic key?" message="The assistant will disappear from Enter Card for every group until a key is entered again. Requests already made stay on record and on the bills. Nothing is changed at Anthropic."
          confirmLabel="Remove" danger busy={busy === 'remove'} onConfirm={() => void remove()} onCancel={() => setRemoving(false)} />
      )}
    </div>
  )
}

// ---- The System Administrator: one group ------------------------------------------------------------------------

/**
 * In the Edit group dialog: whether the assistant is on for this group, off, or left to the group to decide; the
 * level; and the most it may be charged in a month. Each change is saved as it is made (unlike the rest of the dialog).
 */
export function AiGroupEditor({ groupId }: { groupId: number }) {
  const [g, setG] = useState<AiGroupSettings | null>(null)
  const [limit, setLimit] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get<AiGroupSettings>(`/system/groups/${groupId}/ai`).then((d) => { setG(d); setLimit(d.limitCents == null ? '' : (d.limitCents / 100).toFixed(2)) }).catch((e) => setError((e as Error).message))
  }, [groupId])

  const save = async (change: Record<string, unknown>) => {
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      setG(await api.put<AiGroupSettings>(`/system/groups/${groupId}/ai`, change))
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }
  const saveLimit = () => {
    const text = limit.trim()
    if (text === '') return void save({ useDefaultLimit: true })
    const amount = Number(text)
    if (!Number.isFinite(amount) || amount < 0) return setError('Enter the monthly limit in dollars, or leave it empty for the usual limit.')
    void save({ limitCents: Math.round(amount * 100) })
  }

  if (!g) return error ? <Alert kind="error">{error}</Alert> : <Spinner />
  const choice = g.groupDecides ? 'group' : g.on ? 'on' : 'off'
  return (
    <div>
      <p className="muted">
        The assistant on Enter Card fills in a card from what someone types, and the group is charged for each request. It is off until you say otherwise.
        This part saves as you change it.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <div className="source-choice" role="radiogroup" aria-label="AI assistant for this group">
        <label className={choice === 'off' ? 'selected' : ''}>
          <input type="radio" name="ai-choice" checked={choice === 'off'} disabled={busy} onChange={() => void save({ on: false, groupDecides: false })} />
          <span className="source-label">Off</span>
          <span className="source-hint">The group does not see the assistant at all.</span>
        </label>
        <label className={choice === 'on' ? 'selected' : ''}>
          <input type="radio" name="ai-choice" checked={choice === 'on'} disabled={busy} onChange={() => void save({ on: true, groupDecides: false })} />
          <span className="source-label">On for the group</span>
          <span className="source-hint">On at all its restaurants. The group cannot switch it off.</span>
        </label>
        <label className={choice === 'group' ? 'selected' : ''}>
          <input type="radio" name="ai-choice" checked={choice === 'group'} disabled={busy} onChange={() => void save({ groupDecides: true })} />
          <span className="source-label">The group decides</span>
          <span className="source-hint">Its administrators (and owners you allow) switch it on or off on their Billing page. It is {g.on ? 'on' : 'off'} now.</span>
        </label>
      </div>
      <div className="form-row" style={{ marginTop: '0.75rem' }}>
        <Field label="Highest level the group can choose" hint="Levels above this one are not offered to the group.">
          <select value={g.maxLevel} disabled={busy} onChange={(e) => void save({ maxLevel: e.target.value })}>
            {g.levels.map((l) => <option key={l.id} value={l.id}>{l.label} · {l.price} a request</option>)}
          </select>
        </Field>
        <Field label="Level in use" hint="The group can change this itself, up to the highest level.">
          <select value={g.level} disabled={busy} onChange={(e) => void save({ level: e.target.value })}>
            {g.levels.map((l, i) => <option key={l.id} value={l.id} disabled={i > g.levels.findIndex((x) => x.id === g.maxLevel)}>{l.label} · {l.price} a request</option>)}
          </select>
        </Field>
        <Field label="Most it can be charged in a month ($)" hint={`Empty uses the usual ${dollars(g.defaultLimitCents)}. 0 stops every request.`}>
          <input value={limit} inputMode="decimal" disabled={busy} onChange={(e) => setLimit(e.target.value)} onBlur={saveLimit} placeholder={(g.defaultLimitCents / 100).toFixed(2)} />
        </Field>
      </div>
      <p className="muted small" style={{ marginBottom: 0 }}>
        This month: {g.usedRequests.toLocaleString()} request{g.usedRequests === 1 ? '' : 's'}, {aiDollars(g.usedMills)} charged. {saved && <strong>Saved.</strong>}
      </p>
    </div>
  )
}

// ---- The System Administrator: how it is being used --------------------------------------------------------------

const bucketColumns = (first: string): Column<AiUsageBucket>[] => [
  { key: 'label', label: first, value: (b) => b.label },
  { key: 'requests', label: 'Requests', num: true, value: (b) => b.requests },
  { key: 'charged', label: 'Charged', num: true, value: (b) => b.charged },
  { key: 'failed', label: 'Failed (not charged)', num: true, value: (b) => b.failed },
  { key: 'notSaved', label: 'No card saved', num: true, value: (b) => b.notSaved },
  { key: 'mills', label: 'Billed to groups', num: true, value: (b) => b.mills / 1000, render: (b) => aiDollars(b.mills) },
  { key: 'cost', label: 'Cost to you', num: true, value: (b) => b.costMicros / 1_000_000, render: (b) => cost(b.costMicros) },
  { key: 'margin', label: 'Difference', num: true, value: (b) => b.mills / 1000 - b.costMicros / 1_000_000, render: (b) => `$${(b.mills / 1000 - b.costMicros / 1_000_000).toFixed(2)}` },
]

/** On Reports: how the assistant is being used and by which groups, for the dates, groups and restaurants chosen there. */
export function AiUsageSection({ from, to, groupIds, restaurantIds }: { from: string; to: string; groupIds: number[]; restaurantIds: number[] }) {
  const [data, setData] = useState<AiUsage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [opened, setOpened] = useState<AiRequestRow | null>(null)
  const groups = groupIds.join(',')
  const places = restaurantIds.join(',')

  useEffect(() => {
    setError(null)
    api.get<AiUsage>(`/system/ai/usage${qs({ from, to, groupIds: groups, restaurantIds: places })}`).then(setData).catch((e) => setError((e as Error).message))
  }, [from, to, groups, places])

  return (
    <div className="panel">
      <h2>AI assistant</h2>
      <p className="muted">
        Every press of Process in these dates, for the groups and restaurants chosen above: how often it is used and by whom, what groups were charged, and what it
        cost you. A request is charged when it worked, whether or not a card was saved from it.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!data ? (!error && <Spinner label="Loading…" />) : data.requests === 0 ? (
        <p className="muted" style={{ marginBottom: 0 }}>No requests in these dates.</p>
      ) : (
        <>
          <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
            <Stat value={data.requests.toLocaleString()} label={`requests, ${data.charged.toLocaleString()} charged`} />
            <Stat value={aiDollars(data.mills)} label="billed to groups" />
            <Stat value={cost(data.costMicros)} label="cost to you" />
            <Stat value={data.notSaved.toLocaleString()} label="charged, but no card was saved" warn={data.notSaved > 0} />
          </div>
          <DataTable<AiUsageBucket> title="By investment group" rows={data.byGroup} columns={bucketColumns('Investment group')} rowKey={(b) => b.key} filename={`ai-by-group-${from}-to-${to}.csv`} defaultSort={{ key: 'requests', desc: true }} empty="None." />
          <DataTable<AiUsageBucket> title="By restaurant" rows={data.byRestaurant} columns={bucketColumns('Restaurant')} rowKey={(b) => b.key} filename={`ai-by-restaurant-${from}-to-${to}.csv`} defaultSort={{ key: 'requests', desc: true }} empty="None." />
          <DataTable<AiUsageBucket> title="By level" rows={data.byLevel} columns={bucketColumns('Level')} rowKey={(b) => b.key} filename={`ai-by-level-${from}-to-${to}.csv`} empty="None." />
          <DataTable<AiUsageBucket> title="Day by day" rows={data.byDay} columns={bucketColumns('Day')} rowKey={(b) => b.key} filename={`ai-by-day-${from}-to-${to}.csv`} empty="None." />
          <DataTable<AiRequestRow>
            title={`Every request${data.rows.length < data.requests ? ` (the newest ${data.rows.length.toLocaleString()})` : ''}`}
            rows={data.rows}
            columns={[
              { key: 'at', label: 'When', value: (r) => r.at, render: (r) => fmtDateTime(r.at) },
              { key: 'group', label: 'Investment group', value: (r) => r.group ?? '' },
              { key: 'restaurant', label: 'Restaurant', value: (r) => r.restaurant },
              { key: 'by', label: 'Who', value: (r) => r.by },
              {
                key: 'card', label: 'Card', value: (r) => (r.commentId ? `card ${r.commentId}${r.guest ? ` (${r.guest})` : ''}` : r.outcome === 'ok' ? `no card saved${r.guest ? ` (${r.guest})` : ''}` : ''),
                render: (r) => (r.outcome !== 'ok' ? <span className="muted">—</span> : r.commentId ? <>Card {r.commentId}{r.guest ? ` · ${r.guest}` : ''}</> : <><Badge kind="warn">no card saved</Badge>{r.guest ? ` ${r.guest}` : ''}</>),
              },
              { key: 'level', label: 'Level', value: (r) => r.level },
              { key: 'model', label: 'Model', value: (r) => r.model ?? '', csvOnly: true },
              { key: 'tokens', label: 'Tokens in / out', value: (r) => `${r.inputTokens ?? 0} / ${r.outputTokens ?? 0}` },
              { key: 'price', label: 'Billed', num: true, value: (r) => r.priceMills / 1000, render: (r) => (r.outcome === 'ok' ? aiDollars(r.priceMills) : <Badge kind="neutral">failed</Badge>) },
              { key: 'cost', label: 'Cost', num: true, value: (r) => (r.costMicros ?? 0) / 1_000_000, render: (r) => cost(r.costMicros ?? 0) },
              { key: 'typed', label: 'What was typed', value: (r) => r.input ?? '', csvOnly: true },
              { key: 'error', label: 'Why it failed', value: (r) => r.error ?? '', csvOnly: true },
              { key: 'open', label: '', value: () => null, noSort: true, noCsv: true, render: (r) => <button type="button" className="btn btn-sm" onClick={() => setOpened(r)} aria-label="Show what was typed for this request">Show</button> },
            ]}
            rowKey={(r) => r.id}
            filename={`ai-requests-${from}-to-${to}.csv`}
            defaultSort={{ key: 'at', desc: true }}
            empty="None."
          />
        </>
      )}
      {opened && (
        <Confirm title={`Request ${opened.id} · ${fmtDateTime(opened.at)}`} confirmLabel="Close" onConfirm={() => setOpened(null)} onCancel={() => setOpened(null)}
          message={`${opened.by} at ${opened.restaurant}${opened.group ? ` (${opened.group})` : ''}. ${opened.outcome === 'ok' ? `Billed ${aiDollars(opened.priceMills)}.` : `Failed, not billed: ${opened.error ?? ''}`} ${opened.commentId ? `Saved as card ${opened.commentId}.` : opened.outcome === 'ok' ? 'No card was saved from it.' : ''}\n\nWhat was typed:\n${opened.input ?? ''}`} />
      )}
    </div>
  )
}
