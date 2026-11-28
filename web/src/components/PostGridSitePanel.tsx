import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { PostGridSite, ServiceTestResult } from '../api/types'
import SiteKeyBlock from './SiteKeyBlock'
import { Alert, Spinner } from './ui'

/**
 * PostGrid on Connected Systems: the site's Print & Mail keys, which print and mail every investment group's cards
 * on the site's own PostGrid account. (PostGrid's Address Verification key is entered under Address verification.)
 */
export default function PostGridSitePanel({ onChanged }: { onChanged?: () => void }) {
  const [site, setSite] = useState<PostGridSite | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api.get<PostGridSite>('/system/postgrid').then(setSite).catch((e) => setError((e as Error).message))
  }, [])

  return (
    <div className="panel">
      <h2>PostGrid (printing and mailing cards)</h2>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {!site ? (
        !error && <Spinner />
      ) : (
        <>
          <p className="muted">
            Every investment group’s cards are printed and mailed on this one PostGrid account, so PostGrid bills you. You bill each group on{' '}
            <Link to="/system-billing">Billing</Link>, where you set your fee per card and each group’s monthly limit. Groups see “Mailed for you”; they never
            see PostGrid’s name. PostGrid gives you two <strong>Print &amp; Mail API keys</strong> (Settings, API Keys): a test key and a live key. Enter
            both. Whether cards go out for real or as tests is then a setting: for the whole site on <Link to="/system-billing">Billing</Link>, for one
            investment group under <Link to="/groups">Investment groups</Link> (Edit), or for one restaurant.
          </p>
          <h4 style={{ margin: '0.75rem 0 0.25rem' }}>Test key</h4>
          <SiteKeyBlock
            info={site.mailTestKey}
            label="PostGrid test key (begins test_)"
            hint="Used for anything set to test mailing: nothing is printed, mailed or charged. Stored encrypted and never shown again."
            testNote="Test key: cards sent with it are not printed, mailed or charged, and nothing is billed to groups. PostGrid makes a proof of each one."
            liveNote="This is a live key. Enter it under Live key instead."
            onSave={async (k) => {
              if (!/^test_/i.test(k.trim())) throw new Error('That is not a test key: PostGrid’s test keys begin test_. Enter a live key under Live key below.')
              setSite(await api.put<PostGridSite>('/system/postgrid/mail', { apiKey: k }))
              onChanged?.()
            }}
            onTest={() => api.post<ServiceTestResult>('/system/postgrid/mail/test?mode=test')}
            onRemove={async () => { setSite(await api.delete<PostGridSite>('/system/postgrid/mail?mode=test')); onChanged?.() }}
            removeTitle="Remove the PostGrid test key?"
            removeMessage="Restaurants set to test mailing will not be able to send test orders until a test key is entered again. Real mailing is not affected."
            onError={setError}
          />
          <h4 style={{ margin: '1rem 0 0.25rem' }}>Live key</h4>
          <SiteKeyBlock
            info={site.mailLiveKey}
            label="PostGrid live key (begins live_)"
            hint="Used for anything set to real mailing: cards are printed, mailed and charged to your PostGrid account. Stored encrypted and never shown again."
            testNote="This is a test key. Enter it under Test key instead."
            liveNote="Live key: cards sent with it are printed, mailed and charged to this PostGrid account, then billed to each group."
            onSave={async (k) => {
              if (/^test_/i.test(k.trim())) throw new Error('That is a test key. Enter it under Test key above; the live key begins live_.')
              setSite(await api.put<PostGridSite>('/system/postgrid/mail', { apiKey: k }))
              onChanged?.()
            }}
            onTest={() => api.post<ServiceTestResult>('/system/postgrid/mail/test?mode=live')}
            onRemove={async () => { setSite(await api.delete<PostGridSite>('/system/postgrid/mail?mode=live')); onChanged?.() }}
            removeTitle="Remove the PostGrid live key?"
            removeMessage="No group will be able to have cards mailed until a key is entered again; they can still print their own. Cards already sent are not affected, but their status can no longer be checked. Nothing is changed at PostGrid."
            onError={setError}
          />
          <p>
            {site.cardsMailed} card{site.cardsMailed === 1 ? '' : 's'} mailed ({site.cardsThisMonth} this month)
            {site.testCardsMailed > 0 ? `, ${site.testCardsMailed} test order${site.testCardsMailed === 1 ? '' : 's'} (not mailed)` : ''}.
          </p>
          <p className="muted" style={{ marginBottom: 0 }}>
            PostGrid’s Address Verification key (a different key) is entered under <strong>Address Verification</strong>, below.
          </p>
        </>
      )}
    </div>
  )
}
