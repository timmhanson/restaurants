import type { ReactNode } from 'react'

/** A headline number with its label; `warn` turns the number red. */
export default function Stat({ value, label, warn = false }: { value: ReactNode; label: ReactNode; warn?: boolean }) {
  return (
    <div className="stat">
      <div className="stat-value" style={warn ? { color: 'var(--danger)' } : undefined}>{value}</div>
      <div className="stat-label">{label}</div>
    </div>
  )
}
