import { useState } from 'react'

/** A key the page shows only this once, with a one-click copy and a note on keeping it. */
export function KeyShownOnce({ label, value, note, onDone }: { label: string; value: string; note: string; onDone: () => void }) {
  const [copied, setCopied] = useState(false)
  const [failed, setFailed] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setFailed(false)
    } catch {
      setFailed(true)
    }
  }

  return (
    <div className="form">
      <label>
        {label}
        <input value={value} readOnly onFocus={(e) => e.target.select()} />
      </label>
      <p className="muted hint">{note}</p>
      {failed && <p className="error hint">The key could not be copied. Select it and copy it yourself.</p>}
      <div className="form-actions">
        <button type="button" onClick={copy}>
          {copied ? 'Copied' : 'Copy key'}
        </button>
        <button type="button" className="quiet" onClick={onDone}>
          Done
        </button>
      </div>
    </div>
  )
}
