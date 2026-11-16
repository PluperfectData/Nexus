import { type FormEvent, useEffect, useState } from 'react'
import { cancelConnect, connectConnection, connectStreams, type ConnectView, finishConnect, getConnect, type Stream } from '../api'
import { leaveFor } from '../browser'
import { FieldInput } from '../components/FieldInput'
import { StreamPicks } from '../components/StreamPicks'
import { initialPicks, type Pick, picked } from '../streams'

/**
 * The page an app sends a person to: they set up the connection the app asked for, pick its streams, and go back to
 * the app. The token after the # is the page's only credential.
 */
export function ConnectPage() {
  const [token] = useState(() => window.location.hash.slice(1))
  const [view, setView] = useState<ConnectView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [values, setValues] = useState<Record<string, string>>({})
  const [streams, setStreams] = useState<Stream[] | null>(null)
  const [picks, setPicks] = useState<Record<string, Pick>>({})
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    getConnect(token)
      .then(setView)
      .catch((e: Error) => setError(e.message))
  }, [token])

  const connected = view?.connection != null
  useEffect(() => {
    if (!connected) return
    connectStreams(token)
      .then((found) => {
        setStreams(found)
        setPicks(initialPicks(found))
      })
      .catch((e: Error) => setError(e.message))
  }, [connected, token])

  async function act(action: () => Promise<void>) {
    setBusy(true)
    setError(null)
    try {
      await action()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  function connect(event: FormEvent) {
    event.preventDefault()
    void act(async () => setView(await connectConnection(token, name.trim(), values)))
  }

  function finish(event: FormEvent) {
    event.preventDefault()
    void act(async () => leaveFor((await finishConnect(token, picked(picks))).returnUrl))
  }

  const cancel = () => act(async () => leaveFor((await cancelConnect(token)).returnUrl))

  if (!view) {
    return <div className="page narrow">{error ? <p className="error">{error}</p> : <p className="muted">Loading…</p>}</div>
  }

  const chosen = picked(picks)
  const fields = view.connector.fields.filter((f) => f.kind !== 'File')

  return (
    <div className="page narrow">
      <h1>Connect {view.connector.name}</h1>
      <p>
        {view.appName} asked to read from {view.connector.name} for the {view.workspaceName} workspace. This page works until{' '}
        {new Date(view.expiresAt).toLocaleTimeString()}.
      </p>

      {view.connection === null ? (
        <form className="form stacked" onSubmit={connect}>
          <p className="muted">{view.connector.description}</p>
          <label>
            Name
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Shop database" required maxLength={200} />
          </label>
          {fields.map((field) => (
            <FieldInput
              key={field.name}
              field={field}
              value={values[field.name] ?? ''}
              onChange={(value) => setValues((current) => ({ ...current, [field.name]: value }))}
            />
          ))}
          {error && <p className="error">{error}</p>}
          <div className="form-actions">
            <button type="submit" disabled={busy}>
              {busy ? 'Connecting…' : 'Connect'}
            </button>
            <button type="button" className="quiet" onClick={cancel} disabled={busy}>
              Cancel
            </button>
          </div>
        </form>
      ) : (
        <form className="form stacked" onSubmit={finish}>
          <p>
            Connected to <strong>{view.connection.name}</strong>. Pick what {view.appName} reads from it.
          </p>
          {streams === null ? (
            <p className="muted">Listing its streams…</p>
          ) : (
            <StreamPicks
              streams={streams}
              picks={picks}
              onPick={(stream, change) => setPicks((current) => ({ ...current, [stream]: { ...current[stream], ...change } }))}
            />
          )}
          {error && <p className="error">{error}</p>}
          <div className="form-actions">
            <button type="submit" disabled={busy || chosen.length === 0}>
              {busy ? 'Finishing…' : `Finish and go back to ${view.appName}`}
            </button>
            <button type="button" className="quiet" onClick={cancel} disabled={busy}>
              Cancel
            </button>
          </div>
        </form>
      )}
    </div>
  )
}
