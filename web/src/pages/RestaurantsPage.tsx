import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api } from '../api/client'
import type { RestaurantDto, RestaurantRequest } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { Alert, Badge, Confirm, Empty, Field, Modal, Spinner } from '../components/ui'

export default function RestaurantsPage() {
  const { refreshContext, isSuperAdmin, groupId } = useAuth()
  const [list, setList] = useState<RestaurantDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<RestaurantDto | 'new' | null>(null)
  const [deactivating, setDeactivating] = useState<RestaurantDto | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setList(await api.get<RestaurantDto[]>('/restaurants?all=true'))
    } catch (e) {
      setError((e as Error).message)
      setList([])
    }
  }, [])
  useEffect(() => {
    void load()
  }, [load, groupId])

  const deactivate = async () => {
    if (!deactivating) return
    setBusy(true)
    try {
      await api.delete(`/restaurants/${deactivating.id}`)
      setDeactivating(null)
      await load()
      await refreshContext()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>Restaurants</h1>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')}>+ Add restaurant</button>
      </div>
      {isSuperAdmin && !groupId && <Alert kind="warn">Select an investment group in the top bar first.</Alert>}
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {list === null ? (
        <Spinner />
      ) : list.length === 0 ? (
        <Empty>No restaurants yet. Add the first one to start entering cards.</Empty>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Name</th><th>Address</th><th>Phone</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {list.map((r) => (
                <tr key={r.id} className={r.isActive ? '' : 'inactive'}>
                  <td><strong>{r.name}</strong></td>
                  <td>{[r.streetAddress, [r.city, r.state].filter(Boolean).join(', '), r.zip].filter(Boolean).join(' · ') || <span className="muted">—</span>}</td>
                  <td>{r.phone || <span className="muted">—</span>}</td>
                  <td>{r.isActive ? <Badge kind="ok">Active</Badge> : <Badge kind="neutral">Inactive</Badge>}</td>
                  <td>
                    <div className="row-actions">
                      <button type="button" className="btn btn-sm" onClick={() => setEditing(r)}>Edit</button>
                      {r.isActive && <button type="button" className="btn btn-sm btn-danger" onClick={() => setDeactivating(r)}>Deactivate</button>}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {editing && (
        <RestaurantModal
          restaurant={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null)
            void load()
            void refreshContext()
          }}
        />
      )}
      {deactivating && (
        <Confirm
          title="Deactivate restaurant?"
          danger
          busy={busy}
          confirmLabel="Deactivate"
          message={`Deactivate ${deactivating.name}? Its comments are kept, but nobody can enter new cards for it until it is reactivated from Edit.`}
          onCancel={() => setDeactivating(null)}
          onConfirm={() => void deactivate()}
        />
      )}
    </>
  )
}

function RestaurantModal({ restaurant, onClose, onSaved }: { restaurant: RestaurantDto | null; onClose: () => void; onSaved: () => void }) {
  const [v, setV] = useState<RestaurantRequest>({
    name: restaurant?.name ?? '',
    streetAddress: restaurant?.streetAddress ?? '',
    city: restaurant?.city ?? '',
    state: restaurant?.state ?? '',
    zip: restaurant?.zip ?? '',
    phone: restaurant?.phone ?? '',
    isActive: restaurant?.isActive ?? true,
  })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const set = (k: keyof RestaurantRequest, val: string | boolean) => setV((s) => ({ ...s, [k]: val }))
  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (restaurant) await api.put(`/restaurants/${restaurant.id}`, v)
      else await api.post('/restaurants', v)
      onSaved()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }
  return (
    <Modal title={restaurant ? `Edit ${restaurant.name}` : 'Add restaurant'} onClose={onClose}>
      <form onSubmit={submit} className="grid">
        {error && <Alert kind="error">{error}</Alert>}
        <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} required placeholder="Culver's of Joliet" /></Field>
        <Field label="Street address"><input value={v.streetAddress ?? ''} onChange={(e) => set('streetAddress', e.target.value)} /></Field>
        <div className="form-row">
          <Field label="City"><input value={v.city ?? ''} onChange={(e) => set('city', e.target.value)} /></Field>
          <Field label="State"><input value={v.state ?? ''} onChange={(e) => set('state', e.target.value)} /></Field>
          <Field label="Zip"><input value={v.zip ?? ''} onChange={(e) => set('zip', e.target.value)} /></Field>
        </div>
        <div className="form-row">
          <Field label="Phone"><input value={v.phone ?? ''} onChange={(e) => set('phone', e.target.value)} /></Field>
          <Field label="Status">
            <select value={v.isActive ? '1' : '0'} onChange={(e) => set('isActive', e.target.value === '1')}>
              <option value="1">Active</option><option value="0">Inactive</option>
            </select>
          </Field>
        </div>
        <div className="actions">
          <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Saving…' : 'Save'}</button>
        </div>
      </form>
    </Modal>
  )
}
