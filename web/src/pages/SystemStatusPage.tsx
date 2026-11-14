import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { EndpointStat, ErrorRow, SystemStatus } from '../api/types'
import DataTable from '../components/DataTable'
import Stat from '../components/Stat'
import { Alert, Badge, Spinner, fmtDateTime } from '../components/ui'

const size = (bytes: number) => (bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`)
function since(iso: string) {
  const mins = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000))
  if (mins < 60) return `${mins} minute${mins === 1 ? '' : 's'}`
  const hours = Math.round(mins / 60)
  if (hours < 48) return `${hours} hour${hours === 1 ? '' : 's'}`
  const days = Math.round(hours / 24)
  return `${days} days`
}

/** Is the site itself healthy: the running version, its services, how it has performed in the last day and what failed. */
export default function SystemStatusPage() {
  const [data, setData] = useState<SystemStatus | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [sending, setSending] = useState(false)

  const load = useCallback(() => {
    api.get<SystemStatus>('/system/status').then(setData).catch((e) => setError((e as Error).message))
  }, [])
  useEffect(load, [load])

  const testEmail = async () => {
    setSending(true)
    setError(null)
    setNotice(null)
    try {
      const res = await api.post<{ message: string }>('/system/test-email')
      setNotice(res.message)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSending(false)
    }
  }

  return (
    <>
      <div className="page-head">
        <h1>System status</h1>
        <span className="grow" />
        <button type="button" className="btn" onClick={load}>Refresh</button>
      </div>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {!data ? (
        <Spinner />
      ) : (
        <>
          <div className="grid grid-4 stat-grid" style={{ marginBottom: '1rem' }}>
            <Stat value={data.errors24h} label="server errors in the last 24 hours" warn={data.errors24h > 0} />
            <Stat value={data.requests24h.toLocaleString()} label={`requests in the last 24 hours (${data.refused24h} refused)`} />
            <Stat value={`${data.avgMs24h} ms`} label={`average response (19 in 20 under ${data.p95Ms24h} ms)`} warn={data.p95Ms24h > 3000} />
            <Stat value={since(data.startedAt)} label="since the site last restarted" />
          </div>

          <div className="grid grid-2">
            <div className="panel">
              <h2>Services</h2>
              <dl className="kv">
                <dt>Version</dt>
                <dd><code>{data.version}</code> <span className="muted">({data.environment})</span></dd>
                <dt>Started</dt>
                <dd>{fmtDateTime(data.startedAt)}</dd>
                <dt>Database</dt>
                <dd>{data.database}, {size(data.databaseBytes)}<div className="muted small">Structure version: {data.latestMigration ?? 'unknown'}</div></dd>
                <dt>Group settings</dt>
                <dd>{data.settingsStore}</dd>
                <dt>Address checking</dt>
                <dd>
                  {data.addressCheckingConfigured ? <Badge kind="ok">set up</Badge> : <Badge kind="neutral">not set up</Badge>} {data.addressChecking}
                  <div className="muted small">
                    {data.addressCheckingConfigured
                      ? data.addressesConfirmed + ' card address(es) confirmed by USPS, ' + data.addressesNotFound + ' not recognised.'
                      : 'Guest addresses are saved as typed. Enter the USPS account on Connected Systems to check them.'}
                  </div>
                </dd>
                <dt>Postage</dt>
                <dd>
                  {data.postageGroupAccounts > 0 ? <Badge kind="ok">{data.postageGroupAccounts} group account(s)</Badge> : <Badge kind="neutral">no group accounts yet</Badge>}{' '}
                  {data.postage}
                  <div className="muted small">
                    Each investment group adds its own EasyPost account in its Settings and pays for its own postage.{' '}
                    {data.postageConfigured
                      ? 'The site also has a shared account, used for groups without their own.'
                      : 'There is no shared site account, so groups without one print for a stamp by hand.'}
                  </div>
                  <div className="muted small">
                    {data.postageCardsBought + ' card(s) of postage bought, $' + (data.postageCentsSpent / 100).toFixed(2) + ' in all' + (data.postageTestLabels ? '; ' + data.postageTestLabels + ' test label(s)' : '') + '.'}
                  </div>
                </dd>
                <dt>Email</dt>
                <dd>
                  {data.emailConfigured ? <Badge kind="ok">working service</Badge> : <Badge kind="danger">not set up</Badge>} {data.email}
                  <div style={{ marginTop: '0.4rem' }}>
                    <button type="button" className="btn btn-sm" onClick={testEmail} disabled={sending || !data.emailConfigured || !data.testEmailTo}>
                      {sending ? 'Sending…' : 'Send me a test email'}
                    </button>{' '}
                    <span className="muted small">{data.testEmailTo ? `to ${data.testEmailTo}` : 'Add an email to your account on People first.'}</span>
                  </div>
                </dd>
                <dt>Interaction log</dt>
                <dd>
                  Kept for {data.logRetentionDays} days{data.oldestLogEntry ? `; oldest entry ${fmtDateTime(data.oldestLogEntry)}` : ''}.{' '}
                  <Link to="/activity">Open the log</Link>
                </dd>
              </dl>
            </div>
            <div className="panel">
              <h2>What is stored</h2>
              <table className="compact">
                <tbody>
                  {data.tables.map((t) => (
                    <tr key={t.name}>
                      <td>{t.name}</td>
                      <td className="num">{t.rows.toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>

          <DataTable<ErrorRow>
            title="Recent server errors"
            help="The last 20 requests that failed on the server, with what went wrong. An error or two right after a new version goes live is normal while it starts."
            rows={data.recentErrors}
            rowKey={(e) => `${e.at}-${e.path}`}
            filename="server-errors.csv"
            defaultSort={{ key: 'at', desc: true }}
            empty="No server errors on record."
            columns={[
              { key: 'at', label: 'When', value: (e) => e.at, render: (e) => fmtDateTime(e.at) },
              { key: 'who', label: 'Who', value: (e) => e.userName ?? null, render: (e) => <>{e.userName ?? <span className="muted">not signed in</span>}<div className="muted small">{e.groupName}</div></> },
              { key: 'what', label: 'Request', value: (e) => `${e.method} ${e.path}`, render: (e) => <code className="small">{e.method} {e.path}</code> },
              { key: 'status', label: 'Result', num: true, value: (e) => e.status, render: (e) => <Badge kind="danger">{e.status}</Badge> },
              { key: 'detail', label: 'What went wrong', value: (e) => e.detail ?? null, render: (e) => <span className="small">{e.detail ?? <span className="muted">not recorded</span>}</span> },
            ]}
          />

          <DataTable<EndpointStat>
            title="Slowest requests in the last 24 hours"
            rows={data.slowest}
            rowKey={(e) => e.endpoint}
            filename="slowest-requests.csv"
            defaultSort={{ key: 'avg', desc: true }}
            empty="No requests in the last 24 hours."
            columns={[
              { key: 'endpoint', label: 'Request', value: (e) => e.endpoint, render: (e) => <code className="small">{e.endpoint}</code> },
              { key: 'calls', label: 'Calls', num: true, value: (e) => e.calls },
              { key: 'avg', label: 'Average (ms)', num: true, value: (e) => e.avgMs },
              { key: 'max', label: 'Slowest (ms)', num: true, value: (e) => e.maxMs },
              { key: 'errors', label: 'Errors', num: true, value: (e) => e.errors, render: (e) => (e.errors > 0 ? <Badge kind="danger">{e.errors}</Badge> : 0) },
            ]}
          />
        </>
      )}
    </>
  )
}
