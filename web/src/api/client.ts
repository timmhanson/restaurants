import type { DuplicateCheckResult } from './types'

const TOKEN_KEY = 'gf.token'
const GROUP_KEY = 'gf.groupId'

export class ApiError extends Error {
  status: number
  body: unknown
  constructor(status: number, message: string, body: unknown) {
    super(message)
    this.status = status
    this.body = body
  }
  /** Populated when a POST /comments returned 409 with a duplicate-check payload. */
  get duplicate(): DuplicateCheckResult | null {
    const b = this.body as Partial<DuplicateCheckResult> | null
    return this.status === 409 && b && typeof b === 'object' && 'guestKey' in b ? (b as DuplicateCheckResult) : null
  }
}

export const tokenStore = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (t: string | null) => (t ? localStorage.setItem(TOKEN_KEY, t) : localStorage.removeItem(TOKEN_KEY)),
}

export const groupStore = {
  get: (): number | null => {
    const v = localStorage.getItem(GROUP_KEY)
    return v ? Number(v) : null
  },
  set: (id: number | null) => (id ? localStorage.setItem(GROUP_KEY, String(id)) : localStorage.removeItem(GROUP_KEY)),
}

type Listener = (event: 'unauthorized' | 'password-change-required') => void
let listener: Listener | null = null
export function onAuthEvent(fn: Listener | null) {
  listener = fn
}

function extractMessage(status: number, body: unknown): string {
  if (body && typeof body === 'object') {
    const b = body as Record<string, unknown>
    if (typeof b.message === 'string') return b.message
    if (b.errors && typeof b.errors === 'object') {
      const all = Object.values(b.errors as Record<string, string[]>).flat()
      if (all.length) return all.join(' ')
    }
    if (typeof b.title === 'string') return b.title
  }
  if (status === 401) return 'Your session has expired. Please sign in again.'
  if (status === 403) return 'You do not have permission to do that.'
  if (status === 404) return 'Not found.'
  return `Request failed (${status}).`
}

async function request<T>(method: string, path: string, body?: unknown, raw = false): Promise<T> {
  const headers: Record<string, string> = {}
  const token = tokenStore.get()
  if (token) headers.Authorization = `Bearer ${token}`
  const gid = groupStore.get()
  if (gid) headers['X-Group-Id'] = String(gid)
  if (body !== undefined) headers['Content-Type'] = 'application/json'

  const res = await fetch(`/api${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) })
  if (res.status === 204) return undefined as T
  if (raw && res.ok) return (await res.blob()) as T

  const text = await res.text()
  let parsed: unknown = null
  try {
    parsed = text ? JSON.parse(text) : null
  } catch {
    parsed = text
  }
  if (!res.ok) {
    const code = parsed && typeof parsed === 'object' ? (parsed as { code?: string }).code : undefined
    if (res.status === 401 && !path.startsWith('/auth/login')) listener?.('unauthorized')
    if (res.status === 403 && code === 'PasswordChangeRequired') listener?.('password-change-required')
    throw new ApiError(res.status, extractMessage(res.status, parsed), parsed)
  }
  return parsed as T
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T>(path: string, body?: unknown) => request<T>('POST', path, body),
  put: <T>(path: string, body?: unknown) => request<T>('PUT', path, body),
  delete: <T = void>(path: string) => request<T>('DELETE', path),
  blob: (path: string) => request<Blob>('GET', path, undefined, true),
}

export function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const sp = new URLSearchParams()
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === '') continue
    sp.set(k, String(v))
  }
  const s = sp.toString()
  return s ? `?${s}` : ''
}

export function downloadBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
