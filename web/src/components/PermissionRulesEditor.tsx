import type { PermissionRules, PermissionState } from '../api/types'

export interface PermissionChange { name: string; owners?: string; managers?: string }

const pretty = (p: string) => p.replace(/([A-Z])/g, ' $1').trim()

/**
 * What owners and managers may do in an investment group, one permission to a row. The same table serves both
 * levels that decide it:
 *  - the System Administrator (bySite): all can, none can, or the investment group decides;
 *  - the group's own administrators: the same for the rows left to them, and for managers "set for each manager".
 * A row decided higher up is shown but cannot be changed. Nobody can be given more than the level above holds, so
 * a permission owners do not hold is one no manager can hold either.
 */
export default function PermissionRulesEditor({ value, onChange, disabled }: { value: PermissionRules; onChange: (change: PermissionChange) => void; disabled?: boolean }) {
  const bySite = value.bySite
  return (
    <div className="table-wrap">
      <table>
        <thead><tr><th>Permission</th><th style={{ width: '27%' }}>Restaurant owners</th><th style={{ width: '27%' }}>Managers</th></tr></thead>
        <tbody>
          {value.permissions.map((p) => (
            <tr key={p.name}>
              <td>
                <strong>{pretty(p.name)}</strong>
                <div className="muted small">{p.description}</div>
              </td>
              <td>{bySite ? <SiteChoice p={p} who="owners" disabled={disabled} onChange={onChange} /> : <GroupOwners p={p} disabled={disabled} onChange={onChange} />}</td>
              <td>
                {!p.forManagers ? <span className="muted">Owners only</span>
                  : bySite ? <SiteChoice p={p} who="managers" disabled={disabled} onChange={onChange} />
                    : <GroupManagers p={p} disabled={disabled} onChange={onChange} />}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function SiteChoice({ p, who, disabled, onChange }: { p: PermissionState; who: 'owners' | 'managers'; disabled?: boolean; onChange: (c: PermissionChange) => void }) {
  const value = who === 'owners' ? p.siteOwners : p.siteManagers ?? 'group'
  const one = who === 'owners' ? 'owner' : 'manager'
  // A manager cannot hold what the owners do not, whatever is chosen for managers.
  const capped = who === 'managers' && !p.ownersCan
  const theirs = who === 'owners' ? (p.groupOwners === 'yes' ? 'all owners can' : 'no owner can')
    : p.groupManagers === 'yes' ? 'all managers can' : p.groupManagers === 'no' ? 'no manager can' : 'set for each manager'
  return (
    <>
      <select value={value} disabled={disabled} aria-label={`${pretty(p.name)}: ${who}`} onChange={(e) => onChange({ name: p.name, [who]: e.target.value })}>
        <option value="yes">All {one}s can</option>
        <option value="no">No {one} can</option>
        <option value="group">The group decides</option>
      </select>
      {capped ? <div className="muted small">No manager can, because the owners cannot.</div>
        : value === 'group' && <div className="muted small">They have chosen: {theirs}.</div>}
    </>
  )
}

const BY_SITE = 'Decided by the people who run the site.'

function GroupOwners({ p, disabled, onChange }: { p: PermissionState; disabled?: boolean; onChange: (c: PermissionChange) => void }) {
  if (p.siteOwners !== 'group') return <><strong>{p.ownersCan ? 'All owners can' : 'No owner can'}</strong><div className="muted small">{BY_SITE}</div></>
  return (
    <select value={p.groupOwners} disabled={disabled} aria-label={`${pretty(p.name)}: owners`} onChange={(e) => onChange({ name: p.name, owners: e.target.value })}>
      <option value="yes">All owners can</option>
      <option value="no">No owner can</option>
    </select>
  )
}

function GroupManagers({ p, disabled, onChange }: { p: PermissionState; disabled?: boolean; onChange: (c: PermissionChange) => void }) {
  if (!p.ownersCan) return <><strong>No manager can</strong><div className="muted small">Owners cannot, so managers cannot either.</div></>
  if (p.siteManagers !== 'group') return <><strong>{p.managers === 'all' ? 'All managers can' : 'No manager can'}</strong><div className="muted small">{BY_SITE}</div></>
  return (
    <select value={p.groupManagers ?? 'each'} disabled={disabled} aria-label={`${pretty(p.name)}: managers`} onChange={(e) => onChange({ name: p.name, managers: e.target.value })}>
      <option value="yes">All managers can</option>
      <option value="no">No manager can</option>
      <option value="each">Set for each manager</option>
    </select>
  )
}
