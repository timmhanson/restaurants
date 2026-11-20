import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { DirectoryRestaurant, SystemPerson, UserDto } from '../api/types'
import { MapRestaurantsModal, ResetPasswordModal } from '../pages/UsersPage'
import { Alert, Badge, Confirm, Empty, RoleBadge, Spinner, fmtDateTime } from './ui'

/**
 * Everyone in one investment group, shown to the System Administrator on the Investment groups screen itself: who
 * they are, what they are, where they work and whether they can sign in, with the account actions that belong at
 * this level (map them to restaurants, reset a password, suspend or restore). Roles are changed inside the group.
 */
export default function GroupPeoplePanel({ groupId, groupName, onOpenInGroup, onChanged }: {
  groupId: number
  groupName: string
  /** Opens the group's own All Staff screen, where roles and restaurants are changed. */
  onOpenInGroup: () => void
  onChanged?: () => void
}) {
  const [people, setPeople] = useState<SystemPerson[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [resetting, setResetting] = useState<UserDto | null>(null)
  const [suspending, setSuspending] = useState<UserDto | null>(null)
  const [mapping, setMapping] = useState<UserDto | null>(null)
  const [restaurants, setRestaurants] = useState<DirectoryRestaurant[]>([])
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      const [all, places] = await Promise.all([api.get<SystemPerson[]>('/system/people'), api.get<DirectoryRestaurant[]>('/system/restaurants')])
      setPeople(all.filter((p) => p.user.groupId === groupId))
      setRestaurants(places.filter((r) => r.groupId === groupId))
    } catch (e) {
      setError((e as Error).message)
    }
  }, [groupId])
  useEffect(() => { void load() }, [load])

  const toggle = async (u: UserDto) => {
    setBusy(true)
    setError(null)
    try {
      await api.put(`/users/${u.id}/status`, { active: !u.isActive })
      setNotice(u.isActive ? `${u.displayName} is suspended and can no longer sign in.` : `${u.displayName} can sign in again.`)
      await load()
      onChanged?.()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
      setSuspending(null)
    }
  }

  return (
    <div style={{ padding: '0.75rem 1rem', background: 'var(--bg, #f6f7f9)' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, flexWrap: 'wrap', marginBottom: 6 }}>
        <strong>People in {groupName}</strong>
        <button type="button" className="btn btn-sm" onClick={onOpenInGroup} title="Opens the group’s own All Staff screen, where people are added and their roles and restaurants are changed">
          Add people or change roles in the group
        </button>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {!people ? (
        !error && <Spinner />
      ) : people.length === 0 ? (
        <Empty>Nobody has an account in this group yet.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Person</th><th>Role</th><th>Restaurants</th><th>Account</th><th>Last sign-in</th><th /></tr></thead>
            <tbody>
              {people.map(({ user: u, restaurants }) => (
                <tr key={u.id} className={u.isActive ? '' : 'inactive'}>
                  <td>
                    <strong>{u.displayName}</strong>
                    <div className="muted small">{u.username}{u.email && u.email !== u.username ? ` · ${u.email}` : ''}</div>
                  </td>
                  <td>
                    <RoleBadge role={u.role} />
                    {u.role === 'GroupAdmin' && restaurants.length > 0 && <> <RoleBadge role="Owner" /></>}
                  </td>
                  <td>
                    {u.role === 'GroupAdmin'
                      ? <>Every restaurant{restaurants.length > 0 && <div className="muted small">Also an owner of {restaurants.join(', ')}</div>}</>
                      : restaurants.length ? restaurants.join(', ') : <Badge kind="warn">none assigned</Badge>}
                  </td>
                  <td>
                    {!u.isActive ? <Badge kind="danger">Suspended</Badge>
                      : u.mustChangePassword ? <Badge kind="warn">Temporary password</Badge>
                        : !u.lastLoginAt ? <Badge kind="warn">Never signed in</Badge>
                          : <Badge kind="ok">Active</Badge>}
                  </td>
                  <td>{u.lastLoginAt ? fmtDateTime(u.lastLoginAt) : <span className="muted">never</span>}</td>
                  <td>
                    <div className="row-actions">
                      <button type="button" className="btn btn-sm" aria-label={`Restaurants for ${u.displayName}`} onClick={() => setMapping(u)}
                        title={u.role === 'GroupAdmin' ? 'Choose the restaurants this administrator also owns' : 'Choose the restaurants this person is assigned to'}>
                        Restaurants
                      </button>
                      <button type="button" className="btn btn-sm" aria-label={`Reset the password of ${u.displayName}`} onClick={() => setResetting(u)}>Reset password</button>
                      <button type="button" className="btn btn-sm" aria-label={`${u.isActive ? 'Suspend' : 'Restore'} ${u.displayName}`} onClick={() => setSuspending(u)}>{u.isActive ? 'Suspend' : 'Restore'}</button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {mapping && (
        <MapRestaurantsModal
          user={mapping}
          restaurants={restaurants.filter((r) => r.isActive || mapping.restaurantIds.includes(r.id))}
          ownerScoped={false}
          onClose={() => setMapping(null)}
          onSaved={(message) => {
            setMapping(null)
            setNotice(message)
            void load()
            onChanged?.()
          }}
        />
      )}
      {resetting && (
        <ResetPasswordModal
          user={resetting}
          canWaive
          onClose={() => setResetting(null)}
          onSaved={(required) => {
            setNotice(required && resetting.role !== 'TeamMember' ? `${resetting.displayName} will be asked to choose a new password at their next sign-in.` : `${resetting.displayName}'s password was set.`)
            setResetting(null)
            void load()
          }}
        />
      )}
      {suspending && (
        <Confirm
          title={suspending.isActive ? `Suspend ${suspending.displayName}?` : `Restore ${suspending.displayName}?`}
          message={suspending.isActive
            ? `${suspending.displayName} will be signed out everywhere and cannot sign in until restored. Nothing they entered is removed.`
            : `${suspending.displayName} will be able to sign in again with their current password.`}
          confirmLabel={suspending.isActive ? 'Suspend' : 'Restore'}
          danger={suspending.isActive}
          busy={busy}
          onConfirm={() => void toggle(suspending)}
          onCancel={() => setSuspending(null)}
        />
      )}
    </div>
  )
}
