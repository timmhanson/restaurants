import { useMemo, useState, type ReactNode } from 'react'
import { downloadBlob } from '../api/client'

export interface Column<T> {
  key: string
  label: string
  /** Sort and CSV value. */
  value: (row: T) => string | number | null | undefined
  render?: (row: T) => ReactNode
  num?: boolean
  /** Leave out of the CSV (e.g. action buttons). */
  noCsv?: boolean
  noSort?: boolean
  /** In the CSV download only, not on screen (e.g. an email shown under the name). */
  csvOnly?: boolean
}

/** A sortable table with a CSV download of exactly what is shown, in the shown order. */
export default function DataTable<T>({
  title,
  help,
  rows,
  columns,
  rowKey,
  filename,
  defaultSort,
  empty = 'Nothing to show.',
}: {
  title: string
  help?: ReactNode
  rows: T[]
  columns: Column<T>[]
  rowKey: (row: T) => string | number
  filename: string
  defaultSort?: { key: string; desc: boolean }
  empty?: string
}) {
  const [sort, setSort] = useState(defaultSort ?? { key: columns[0].key, desc: false })
  const shown = columns.filter((c) => !c.csvOnly)
  const sorted = useMemo(() => {
    const col = columns.find((c) => c.key === sort.key)
    if (!col) return rows
    return [...rows].sort((a, b) => {
      const x = col.value(a), y = col.value(b)
      if (x == null && y == null) return 0
      if (x == null) return 1
      if (y == null) return -1
      const c = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y))
      return sort.desc ? -c : c
    })
  }, [rows, columns, sort])

  const download = () => {
    const cols = columns.filter((c) => !c.noCsv)
    const esc = (v: unknown) => {
      let t = v == null ? '' : String(v)
      if (/^[=+\-@]/.test(t)) t = `'${t}`
      return /[",\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t
    }
    const lines = [cols.map((c) => esc(c.label)).join(','), ...sorted.map((r) => cols.map((c) => esc(c.value(r))).join(','))]
    downloadBlob(new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv' }), filename)
  }

  return (
    <div className="panel">
      <div className="page-head">
        <h2 style={{ margin: 0 }}>{title}</h2>
        <span className="grow" />
        <button type="button" className="btn btn-sm" onClick={download} disabled={!rows.length}>
          ⬇ Download CSV
        </button>
      </div>
      {help && <p className="legend-note">{help}</p>}
      {rows.length === 0 ? (
        <p className="muted">{empty}</p>
      ) : (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                {shown.map((c) =>
                  c.noSort ? (
                    <th key={c.key} className={c.num ? 'num' : ''}>{c.label}</th>
                  ) : (
                    <th
                      key={c.key}
                      className={`sortable ${c.num ? 'num' : ''}`}
                      aria-sort={sort.key === c.key ? (sort.desc ? 'descending' : 'ascending') : 'none'}
                      onClick={() => setSort((s) => (s.key === c.key ? { key: c.key, desc: !s.desc } : { key: c.key, desc: Boolean(c.num) }))}
                    >
                      {c.label}
                      {sort.key === c.key ? (sort.desc ? ' ↓' : ' ↑') : ''}
                    </th>
                  ),
                )}
              </tr>
            </thead>
            <tbody>
              {sorted.map((r) => (
                <tr key={rowKey(r)}>
                  {shown.map((c) => (
                    <td key={c.key} className={c.num ? 'num' : ''}>
                      {c.render ? c.render(r) : (c.value(r) ?? <span className="muted">—</span>)}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
