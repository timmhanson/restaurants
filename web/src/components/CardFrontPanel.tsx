import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { CardArt } from '../api/types'
import { Alert, Badge, Confirm, Spinner, fmtDateTime } from './ui'

const IDEAL_W = 1875
const IDEAL_H = 1275

/** The picture's size in pixels, read in the browser before uploading. */
function pixels(file: File): Promise<{ w: number; h: number } | null> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file)
    const img = new Image()
    img.onload = () => {
      resolve({ w: img.naturalWidth, h: img.naturalHeight })
      URL.revokeObjectURL(url)
    }
    img.onerror = () => {
      resolve(null)
      URL.revokeObjectURL(url)
    }
    img.src = url
  })
}

/**
 * The picture PostGrid prints on the front of this group's coupon cards (PostGrid prints on blank stock, so the
 * pre-printed card front cannot be used). Without one, cards get a plain front in the group's colour with the
 * restaurant's name and the offer.
 */
export default function CardFrontPanel() {
  const [art, setArt] = useState<CardArt | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [removing, setRemoving] = useState(false)
  const input = useRef<HTMLInputElement>(null)

  useEffect(() => {
    api.get<CardArt>('/settings/card-front').then(setArt).catch((e) => setError((e as Error).message))
  }, [])

  const upload = async (file: File | undefined) => {
    if (!file) return
    setError(null)
    setNotice(null)
    if (!['image/png', 'image/jpeg'].includes(file.type)) return setError('Choose a PNG or JPEG picture.')
    if (file.size > 5 * 1024 * 1024) return setError('The picture is larger than 5 MB. Save it as a JPEG, or at a smaller size, and try again.')
    const size = await pixels(file)
    setBusy(true)
    try {
      setArt(await api.upload<CardArt>('/settings/card-front', file))
      const ratio = size ? size.w / size.h : 0
      setNotice(
        !size
          ? 'Uploaded.'
          : ratio < 1.3 || ratio > 1.65
            ? `Uploaded, but it is ${size.w} × ${size.h} pixels, not the shape of a 6 × 4 card: PostGrid will crop it to fit. A picture ${IDEAL_W} × ${IDEAL_H} pixels fits exactly.`
            : size.w < 1200
              ? `Uploaded, but at ${size.w} × ${size.h} pixels it may print blurry. ${IDEAL_W} × ${IDEAL_H} pixels prints sharp.`
              : 'Uploaded. Cards sent to PostGrid from now on use this picture.',
      )
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
      if (input.current) input.current.value = ''
    }
  }

  const remove = async () => {
    setBusy(true)
    setError(null)
    try {
      setArt(await api.delete<CardArt>('/settings/card-front'))
      setNotice('Removed. Cards sent to PostGrid get a plain front in the group’s colour.')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
      setRemoving(false)
    }
  }

  return (
    <div className="panel">
      <h2>Card front for PostGrid</h2>
      <p className="muted">
        PostGrid prints on blank card stock, so it prints the front too. Upload the front of your card as a picture: {IDEAL_W} × {IDEAL_H} pixels (6¼ × 4¼
        inches at 300 dpi, which includes ⅛ inch that is trimmed off each edge), PNG or JPEG, up to 5 MB. Keep words at least ¼ inch from the edges.
        Without a picture, cards get a plain front in the group’s colour with the restaurant’s name and the offer.
      </p>
      {error && <Alert kind="error" onClose={() => setError(null)}>{error}</Alert>}
      {notice && <Alert kind="success" onClose={() => setNotice(null)}>{notice}</Alert>}
      {!art ? (
        !error && <Spinner />
      ) : (
        <>
          {art.hasArt && art.url ? (
            <div style={{ display: 'flex', gap: '1rem', flexWrap: 'wrap', alignItems: 'flex-start' }}>
              <img src={art.url} alt="The card front PostGrid prints" style={{ width: 300, maxWidth: '100%', aspectRatio: '6.25 / 4.25', objectFit: 'cover', border: '1px solid var(--border)', borderRadius: 4 }} />
              <dl className="kv" style={{ margin: 0 }}>
                <dt>Picture</dt>
                <dd>
                  <Badge kind="ok">uploaded</Badge> {art.widthPx && art.heightPx ? `${art.widthPx} × ${art.heightPx} pixels, ` : ''}
                  {Math.max(1, Math.round(art.bytes / 1024))} KB
                </dd>
                <dt>Added</dt>
                <dd>{art.uploadedAt ? fmtDateTime(art.uploadedAt) : '—'}{art.uploadedBy ? ` by ${art.uploadedBy}` : ''}</dd>
              </dl>
            </div>
          ) : (
            <p><Badge kind="neutral">no picture yet</Badge> Cards get a plain front in the group’s colour.</p>
          )}
          <div className="row-actions" style={{ marginTop: '0.75rem' }}>
            <input ref={input} type="file" accept="image/png,image/jpeg" hidden onChange={(e) => void upload(e.target.files?.[0])} />
            <button type="button" className="btn btn-primary" onClick={() => input.current?.click()} disabled={busy}>
              {busy ? 'Uploading…' : art.hasArt ? 'Replace picture' : 'Upload picture'}
            </button>
            {art.hasArt && <button type="button" className="btn btn-danger" onClick={() => setRemoving(true)} disabled={busy}>Remove picture</button>}
          </div>
        </>
      )}
      {removing && (
        <Confirm
          title="Remove the card front picture?"
          message="Cards sent to PostGrid from now on get a plain front in the group’s colour. Cards already sent are not changed."
          confirmLabel="Remove picture"
          danger
          busy={busy}
          onConfirm={() => void remove()}
          onCancel={() => setRemoving(false)}
        />
      )}
    </div>
  )
}
