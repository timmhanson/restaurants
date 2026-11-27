import { useEffect, useState } from 'react'
import { api, downloadBlob } from '../api/client'
import type { Backups } from '../api/types'
import { Alert, Badge, Confirm, Empty, Spinner, fmtDateTime } from './ui'

const size = (bytes: number) => (bytes >= 1_048_576 ? `${(bytes / 1_048_576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`)

const every = (minutes: number) => (minutes === 60 ? 'every hour' : `every ${minutes} minutes`)
const span = (hours: number) => (hours % 24 === 0 ? `${hours / 24} day${hours === 24 ? '' : 's'}` : `${hours} hours`)

/**
 * The copies of the site's database, for the System Administrator: how the last backup went, every copy there is,
 * "Back up now", and a copy to download. A copy holds everything the site knows, so downloading one asks first.
 */
export default function BackupsPanel() {
  const [data, setData] = useState<Backups | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [downloading, setDownloading] = useState<string | null>(null)
  const [all, setAll] = useState(false)

  useEffect(() => {
    api.get<Backups>('/system/backups').then(setData).catch((e) => setError((e as Error).message))
  }, [])

  const run = async () => {
    setBusy('run')
    setError(null)
    try {
      setData(await api.post<Backups>('/system/backups'))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
    }
  }
  const download = async (name: string) => {
    setBusy(name)
    setError(null)
    try {
      downloadBlob(await api.blob(`/system/backups/${encodeURIComponent(name)}/download`), name)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(null)
      setDownloading(null)
    }
  }

  const last = data?.last
  const files = data ? (all ? data.files : data.files.slice(0, 8)) : []
  return (
    <div className="panel">
      <div className="page-head" style={{ marginBottom: 0 }}>
        <h2 style={{ margin: 0 }}>Backups of the database</h2>
        <span className="grow" />
        <button type="button" className="btn btn-primary" onClick={() => void run()} disabled={busy !== null || !data}>{busy === 'run' ? 'Backing up…' : 'Back up now'}</button>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!data ? (!error && <Spinner />) : (
        <>
          <p className="muted">
            {data.enabled
              ? <>A copy of the whole database is made {every(data.everyMinutes)} and every time the site starts, which is what a deploy does, before the new version changes anything. It is made while the site carries on working, and checked before it is kept.{' '}</>
              : <>The automatic backups are switched off for this site. “Back up now” still works.{' '}</>}
            {data.offSite
              ? <>Copies go to the site’s own disk and to the site’s Azure Storage account, which is a separate service. Every copy is kept for {span(data.keepHourlyHours)}; after that the first copy of each day, for {data.keepDailyDays} days; after that the first copy of each month, always. The disk keeps the {span(data.keepHourlyHours)} and {data.onSiteDays} days of daily copies.</>
              : <>Copies are kept on the site’s own disk: every copy for {span(data.keepHourlyHours)}, then the first of each day for {data.onSiteDays} days. No storage account is set up, so there is no second place: download a copy now and then.</>}
          </p>
          {last ? (
            last.ok ? (
              <Alert kind="success">
                Last backup: {fmtDateTime(last.at)}, {size(last.bytes)}, by {last.by}. {last.offSite ? 'It is on the disk and in storage.' : 'It is on the site’s disk.'}
              </Alert>
            ) : (
              <Alert kind="error">The backup at {fmtDateTime(last.at)} did not finish. {last.problem} It is tried again in ten minutes; “Back up now” tries at once.</Alert>
            )
          ) : (
            <Alert kind="info">No backup has been made yet. The first is made a few minutes after the site starts; “Back up now” makes one at once.</Alert>
          )}
          {data.files.length === 0 ? <Empty>No copies yet.</Empty> : (
            <div className="table-wrap">
              <table className="compact">
                <thead><tr><th>Made</th><th>Size</th><th>Kept</th><th /></tr></thead>
                <tbody>
                  {files.map((f) => (
                    <tr key={f.name}>
                      <td>{fmtDateTime(f.at)}</td>
                      <td>{size(f.bytes)}</td>
                      <td>{f.onSite && <Badge kind="neutral">site’s disk</Badge>} {f.offSite && <Badge kind="ok">storage</Badge>}</td>
                      <td>
                        <button type="button" className="btn btn-sm" disabled={busy !== null} onClick={() => setDownloading(f.name)} aria-label={`Download the backup made ${fmtDateTime(f.at)}`}>
                          {busy === f.name ? 'Downloading…' : 'Download'}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
          {data.files.length > 8 && (
            <button type="button" className="btn-link" onClick={() => setAll((x) => !x)}>{all ? 'Show the last 8' : `Show all ${data.files.length} copies`}</button>
          )}
          <p className="muted small" style={{ marginBottom: 0 }}>
            A copy is a compressed SQLite database (<code>.db.gz</code>). To put one back: stop the site, replace the database file on its <code>/home/data</code> share with the
            unzipped copy, and start the site. The keys of outside services (USPS, PostGrid, Stripe) are saved encrypted and their encryption key is not part of a copy, so
            after restoring onto a new site they are entered again on Connected Systems. Group settings live in Azure Storage, not in the database.
          </p>
        </>
      )}
      {downloading && (
        <Confirm
          title="Download this backup?"
          message="A backup holds everything the site knows: guests’ names and addresses, every account and all billing. Keep the file somewhere safe and do not send it by email. The download is recorded in the audit log."
          confirmLabel="Download"
          busy={busy !== null}
          onConfirm={() => void download(downloading)}
          onCancel={() => setDownloading(null)}
        />
      )}
    </div>
  )
}
