import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { GroupBilling } from '../api/types'
import { Alert } from './ui'

/**
 * How an investment group has its month billed: one bill for everything, a bill for each restaurant, or bills for
 * sets of restaurants the group puts together. The group's administrators choose. Each change is saved as it is
 * made, and applies from the next bills created; bills already made stay as they are.
 */
export default function BillSplitPanel({ data, onSaved }: { data: GroupBilling; onSaved: (next: GroupBilling) => void }) {
  const bills = data.bills
  const [names, setNames] = useState<Record<number, string>>({})
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved'>('idle')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setNames(Object.fromEntries((bills?.sets ?? []).map((r) => [r.restaurantId, r.set ?? ''])))
  }, [bills])

  if (!bills) return null
  const save = async (body: { split?: string; sets?: { restaurantId: number; set: string | null }[] }) => {
    setStatus('saving')
    setError(null)
    try {
      onSaved(await api.put<GroupBilling>('/billing/split', body))
      setStatus('saved')
    } catch (e) {
      setError(`Not saved: ${(e as Error).message}`)
      setStatus('idle')
    }
  }
  const busy = status === 'saving'
  const used = [...new Set(bills.sets.map((r) => r.set).filter((s): s is string => Boolean(s)))].sort()
  const unset = bills.sets.filter((r) => !r.set).length

  return (
    <div className="panel">
      <h2>How you are billed</h2>
      <p className="muted">
        Each month’s cards can come as one bill for everything, a bill for each restaurant, or bills for sets of restaurants you put together (for example by
        owner or by area). It applies from the next bills created; bills you already have stay as they are.{' '}
        {status === 'saving' ? 'Saving…' : status === 'saved' ? <strong>Saved.</strong> : ''}
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      <select value={bills.split} disabled={busy} aria-label="How your month is billed" onChange={(e) => void save({ split: e.target.value })}>
        <option value="one">One bill for everything</option>
        <option value="restaurant">A bill for each restaurant</option>
        <option value="sets">Bills for sets of restaurants I group</option>
      </select>
      {bills.split === 'sets' && (
        <>
          <p className="muted small" style={{ marginBottom: '0.4rem' }}>
            Give the restaurants that share a bill the same set name. {unset > 0 && used.length > 0 ? `The ${unset === 1 ? 'one' : unset} with no set name ${unset === 1 ? 'goes' : 'go'} on a bill called “Other restaurants”.` : ''}
            {used.length === 0 ? 'Until a restaurant has a set name, everything stays on one bill.' : ''}
          </p>
          <div className="table-wrap">
            <table>
              <thead><tr><th>Restaurant</th><th>Billed with (set name)</th></tr></thead>
              <tbody>
                {bills.sets.map((r) => (
                  <tr key={r.restaurantId}>
                    <td><strong>{r.restaurant}</strong></td>
                    <td>
                      <input value={names[r.restaurantId] ?? ''} maxLength={60} list="bill-sets" disabled={busy} placeholder="No set" aria-label={`Set that ${r.restaurant} is billed with`}
                        onChange={(e) => setNames((s) => ({ ...s, [r.restaurantId]: e.target.value }))}
                        onBlur={() => { const now = (names[r.restaurantId] ?? '').trim(); if (now !== (r.set ?? '')) void save({ sets: [{ restaurantId: r.restaurantId, set: now || null }] }) }} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <datalist id="bill-sets">{used.map((s) => <option key={s} value={s} />)}</datalist>
        </>
      )}
    </div>
  )
}
