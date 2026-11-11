import { authHeaders, sessionId, tokenStore } from './client'

/**
 * Sends page views, clicks and choices to the interaction log (readable only by System Administrators).
 * Records what was clicked or chosen by its label, never anything typed into a field.
 * New screens are covered automatically: any button, link or select is picked up.
 */
type Kind = 'Page' | 'Click' | 'Choice' | 'SignOut'
interface TrackedEvent {
  kind: Kind
  path: string
  label?: string
  detail?: string
  at: string
}

const queue: TrackedEvent[] = []
let timer: number | null = null

const clean = (s: string | null | undefined, max = 120) => (s ?? '').replace(/\s+/g, ' ').trim().slice(0, max) || undefined
const here = () => window.location.pathname + window.location.search

export function track(kind: Kind, label?: string, detail?: string, path = here()) {
  if (!tokenStore.get()) return
  queue.push({ kind, path, label: clean(label), detail: clean(detail, 300), at: new Date().toISOString() })
  if (queue.length >= 40) flush()
  else if (timer === null) timer = window.setTimeout(flush, 4000)
}

/** Sends what is queued. keepalive lets it finish while the page is closing. */
export function flush() {
  if (timer !== null) {
    window.clearTimeout(timer)
    timer = null
  }
  if (!queue.length || !tokenStore.get()) return
  const events = queue.splice(0, queue.length)
  void fetch('/api/activity', {
    method: 'POST',
    keepalive: true,
    headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId, events }),
  }).catch(() => undefined)
}

function labelOf(el: Element): string | undefined {
  return clean(el.getAttribute('aria-label')) ?? clean((el as HTMLElement).innerText) ?? clean(el.getAttribute('title')) ?? clean(el.getAttribute('name'))
}

function fieldLabel(el: HTMLInputElement | HTMLSelectElement): string | undefined {
  const own = el.labels?.[0]
  const text = own ? clean(own.innerText) : undefined
  return text ?? clean(el.getAttribute('aria-label')) ?? clean(el.name) ?? el.tagName.toLowerCase()
}

let installed = false
export function installTracking() {
  if (installed) return
  installed = true

  document.addEventListener(
    'click',
    (e) => {
      const el = (e.target as Element | null)?.closest('button, a[href], [role="button"], [role="tab"], summary, th.sortable')
      if (!el || el.closest('[data-no-track]')) return
      const kind = el.tagName === 'A' ? `link ${el.getAttribute('href') ?? ''}` : el.tagName === 'TH' ? 'sort column' : 'button'
      track('Click', labelOf(el), kind)
    },
    true,
  )

  // Choices: drop-downs, checkboxes and radio buttons. Text fields are never recorded.
  document.addEventListener(
    'change',
    (e) => {
      const el = e.target
      if (el instanceof HTMLSelectElement) {
        track('Choice', fieldLabel(el), el.selectedOptions[0]?.text ?? '')
      } else if (el instanceof HTMLInputElement && (el.type === 'checkbox' || el.type === 'radio')) {
        const what = el.type === 'radio' ? (clean(el.labels?.[0]?.innerText) ?? el.value) : fieldLabel(el)
        track('Choice', el.type === 'radio' ? (el.name || 'option') : what, el.type === 'radio' ? what : el.checked ? 'ticked' : 'unticked')
      } else if (el instanceof HTMLInputElement && el.type === 'date') {
        track('Choice', fieldLabel(el), el.value)
      }
    },
    true,
  )

  window.addEventListener('pagehide', flush)
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden') flush()
  })
}
