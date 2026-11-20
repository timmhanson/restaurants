import { Link } from 'react-router-dom'
import type { GroupSettingsData } from '../api/types'
import { Alert, Field } from './ui'

type Restaurant = GroupSettingsData['restaurants'][number]

/**
 * An investment group's settings as the System Administrator makes them. Every setting is either one answer for
 * all of the group's restaurants, or left to each restaurant; where it is left to each restaurant, the
 * restaurant's own choice can be set here too. What is set for all restaurants takes precedence over a
 * restaurant's own choice. Nothing is saved from here: the dialog this sits in saves it with the rest.
 */
export default function GroupSettingsEditor({ value: v, onChange }: { value: GroupSettingsData; onChange: (next: GroupSettingsData) => void }) {
  const set = (change: Partial<GroupSettingsData>) => onChange({ ...v, ...change })
  // Each setting is yours for this group, which the group cannot change, or left to the group's own administrators.
  const theirs = (key: string) => v.groupDecides.includes(key)
  const hand = (key: string, label: string) => (
    <label className="check small" style={{ marginTop: '0.3rem' }}>
      <input type="checkbox" checked={theirs(key)} aria-label={`Let the group’s administrators change: ${label}`}
        onChange={(e) => set({ groupDecides: e.target.checked ? [...v.groupDecides, key] : v.groupDecides.filter((k) => k !== key) })} />
      <span>{theirs(key) ? 'The group’s administrators can change this' : 'Let the group’s administrators change this (now: yours, and it takes precedence)'}</span>
    </label>
  )
  const setRestaurant = (id: number, change: Partial<Restaurant>) => onChange({ ...v, restaurants: v.restaurants.map((r) => (r.id === id ? { ...r, ...change } : r)) })
  const siteFixesMode = v.siteMailMode !== 'each'
  const mode = siteFixesMode ? v.siteMailMode : v.mailMode
  const oneSignature = v.signatureForAll != null
  const perRestaurant = v.mail === 'each' || (!siteFixesMode && v.mailMode === 'each') || v.usualWay === 'each' || !oneSignature
  const active = v.restaurants.filter((r) => r.isActive)
  // Live somewhere without a live key, or test somewhere without a test key, would leave those cards unable to go.
  const needsLive = v.mail !== 'off' && (mode === 'live' || (mode === 'each' && active.some((r) => r.mailLive)))
  const needsTest = v.mail !== 'off' && (mode === 'test' || (mode === 'each' && active.some((r) => !r.mailLive)))

  return (
    <>
      <h3 style={{ margin: '0.5rem 0 0' }}>Settings for this group</h3>
      <p className="muted small" style={{ margin: 0 }}>
        Each setting is one answer for all of the group’s restaurants, or left to each restaurant. What is set for all restaurants takes precedence over a
        restaurant’s own choice. Under each one, choose whether it is yours for this group (the group cannot change it) or the group’s own administrators
        may change it. Below them, a restaurant’s owners make the restaurant’s own choices.
      </p>
      <div className="grid grid-2">
        <Field label="Having cards printed and mailed" hint="Until this is on for a restaurant, its people see “Mailed for you” greyed out on the print screens.">
          <select value={v.mail} onChange={(e) => set({ mail: e.target.value as GroupSettingsData['mail'] })}>
            <option value="on">On for all restaurants</option>
            <option value="off">Off for all restaurants</option>
            <option value="each">Each restaurant decides</option>
          </select>
          {hand('mail', 'having cards printed and mailed')}
        </Field>
        <Field label="Real or test mailing" hint={siteFixesMode
          ? 'Set for every investment group on Billing, so it cannot be changed for one group.'
          : 'Real: cards are printed, mailed and billed. Test: a proof is made, and nothing is printed, mailed or billed.'}>
          {siteFixesMode ? (
            <input readOnly value={v.siteMailMode === 'live' ? 'Real mailing for every group (set for the whole site)' : 'Test mailing for every group (set for the whole site)'} />
          ) : (
            <select value={v.mailMode} onChange={(e) => set({ mailMode: e.target.value as GroupSettingsData['mailMode'] })}>
              <option value="live">Real mailing for all restaurants</option>
              <option value="test">Test mailing for all restaurants</option>
              <option value="each">Each restaurant decides</option>
            </select>
          )}
          {!siteFixesMode && hand('mode', 'real or test mailing')}
        </Field>
        <Field label="How cards usually go out" hint="What the print screens start on. Whoever is printing can still pick another way.">
          <select value={v.usualWay} onChange={(e) => set({ usualWay: e.target.value as GroupSettingsData['usualWay'] })}>
            <option value="mail">Mailed for you, for all restaurants</option>
            <option value="stamp">Stamps by hand, for all restaurants</option>
            <option value="each">Each restaurant decides</option>
          </select>
          {hand('usual', 'how cards usually go out')}
        </Field>
        <Field label="A payment method before any card is mailed" hint="Payment is the group’s, so this is one answer for the whole group, and always yours. A group with credit can mail without one, as far as the credit covers.">
          <select value={v.payRule} onChange={(e) => set({ payRule: e.target.value as GroupSettingsData['payRule'] })}>
            <option value="site">Follow the site’s setting ({v.siteRequiresPay ? 'required' : 'not required'})</option>
            <option value="required">Required for this group</option>
            <option value="notrequired">Not required for this group</option>
          </select>
        </Field>
        <Field label="Name that signs cards entered with the team login" hint="Managers, owners and administrators sign with their own name. This is for the shared team login.">
          <select value={oneSignature ? 'all' : 'each'} onChange={(e) => set({ signatureForAll: e.target.value === 'all' ? v.signatureForAll ?? '' : null })}>
            <option value="all">One name for all restaurants</option>
            <option value="each">Each restaurant decides</option>
          </select>
          {hand('signature', 'the name that signs for the team login')}
        </Field>
        {oneSignature && (
          <Field label="The name, for all restaurants">
            <input value={v.signatureForAll ?? ''} maxLength={60} onChange={(e) => set({ signatureForAll: e.target.value })} placeholder="For example: Michael Smith" />
          </Field>
        )}
      </div>
      {needsLive && !v.hasLiveKey && <Alert kind="warn">Real mailing needs the PostGrid live key, which has not been entered. Add it on <Link to="/connected-systems">Connected Systems</Link>, or those cards cannot be sent.</Alert>}
      {needsTest && !v.hasTestKey && <Alert kind="warn">Test mailing needs the PostGrid test key, which has not been entered. Add it on <Link to="/connected-systems">Connected Systems</Link>, or those test orders cannot be sent.</Alert>}

      {v.restaurants.length > 0 && (
        <>
          <h4 style={{ margin: '0.5rem 0 0' }}>Restaurant by restaurant</h4>
          <p className="muted small" style={{ margin: 0 }}>
            {perRestaurant
              ? 'Where a setting above says “Each restaurant decides”, the restaurant’s own choice is set here (its owners can change it too). The others show what applies to all.'
              : 'Every setting above is the same for all restaurants, so there is nothing to choose here. Pick “Each restaurant decides” above to set one restaurant differently.'}
          </p>
          <div className="table-wrap">
            <table>
              <thead><tr><th>Restaurant</th><th>Cards mailed for it</th><th>Real or test</th><th>Usually goes out</th><th>Team login signs as</th></tr></thead>
              <tbody>
                {v.restaurants.map((r) => (
                  <tr key={r.id} className={r.isActive ? '' : 'inactive'}>
                    <td><strong>{r.name}</strong>{!r.isActive && <span className="muted small"> (inactive)</span>}</td>
                    <td>
                      {v.mail === 'each' ? (
                        <select value={r.mailOn ? 'on' : 'off'} aria-label={`Cards mailed for ${r.name}`} onChange={(e) => setRestaurant(r.id, { mailOn: e.target.value === 'on' })}>
                          <option value="on">On</option><option value="off">Off</option>
                        </select>
                      ) : <span className="muted">{v.mail === 'on' ? 'On (all restaurants)' : 'Off (all restaurants)'}</span>}
                    </td>
                    <td>
                      {mode === 'each' ? (
                        <select value={r.mailLive ? 'live' : 'test'} aria-label={`Real or test mailing for ${r.name}`} onChange={(e) => setRestaurant(r.id, { mailLive: e.target.value === 'live' })}>
                          <option value="live">Real</option><option value="test">Test</option>
                        </select>
                      ) : <span className="muted">{mode === 'live' ? 'Real' : 'Test'} ({siteFixesMode ? 'whole site' : 'all restaurants'})</span>}
                    </td>
                    <td>
                      {v.usualWay === 'each' ? (
                        <select value={r.mailByDefault ? 'mail' : 'stamp'} aria-label={`How cards usually go out for ${r.name}`} onChange={(e) => setRestaurant(r.id, { mailByDefault: e.target.value === 'mail' })}>
                          <option value="mail">Mailed for you</option><option value="stamp">Stamps by hand</option>
                        </select>
                      ) : <span className="muted">{v.usualWay === 'mail' ? 'Mailed for you' : 'Stamps by hand'} (all restaurants)</span>}
                    </td>
                    <td>
                      {oneSignature
                        ? <span className="muted">{v.signatureForAll?.trim() || 'No name yet'} (all restaurants)</span>
                        : <input value={r.signatureName ?? ''} maxLength={60} aria-label={`Name that signs for the team login at ${r.name}`} placeholder="No name" onChange={(e) => setRestaurant(r.id, { signatureName: e.target.value })} />}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
      <p className="muted small" style={{ margin: 0 }}>
        Credit: {v.creditCents > 0 ? `$${(v.creditCents / 100).toFixed(2)} unused` : 'none'}. {v.payLabel ? `Pays with ${v.payLabel}.` : 'No payment method saved.'}{' '}
        Fees, limits, credits and bills are on <Link to="/system-billing">Billing</Link>.
      </p>
    </>
  )
}
