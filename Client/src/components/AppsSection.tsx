import { type FormEvent, useState } from 'react'
import {
  addDestination,
  type App,
  type AppWithKey,
  checkDestination,
  type Destination,
  editApp,
  makeApp,
  newAppKey,
  removeApp,
  removeDestination,
  revokeAppKey,
} from '../api'
import { keyLabel } from '../places'
import { KeyShownOnce } from './KeyShownOnce'

/** What the push API said of a destination's key at the last check. */
function KeyState({ destination }: { destination: Destination }) {
  const when = destination.keyCheckedAt ? new Date(destination.keyCheckedAt).toLocaleString() : null
  if (destination.keyProblem) {
    return <span className="error">Key refused at {when}: {destination.keyProblem}</span>
  }
  return (
    <span className="muted">
      Push {keyLabel(destination.keyPrefix, destination.keyName)}
      {when && `, checked ${when}`}
    </span>
  )
}

function ShownKey({ made, onDone }: { made: AppWithKey; onDone: () => void }) {
  return (
    <div className="panel" role="status">
      <KeyShownOnce
        label={`${made.app.name}'s key`}
        value={made.key}
        note="Nexus shows this key only once. Copy it now, and keep it where the app reads its settings."
        onDone={onDone}
      />
    </div>
  )
}

/** The addresses an app's connect pages may send a person back to, one a line. */
function ReturnUrlsForm({ app, onSaved }: { app: App; onSaved: (app: App) => void }) {
  const [text, setText] = useState(app.returnUrls.join('\n'))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onSaved(await editApp(app.id, app.name, text.split('\n').map((url) => url.trim()).filter(Boolean)))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="form inline" onSubmit={submit}>
      <label className="return-urls">
        Return addresses, one a line
        <textarea value={text} onChange={(e) => setText(e.target.value)} rows={3} placeholder="http://localhost:6160/sources" />
      </label>
      <button type="submit" className="secondary" disabled={busy}>
        {busy ? 'Saving…' : 'Save return addresses'}
      </button>
      {error && <p className="error wide">{error}</p>}
    </form>
  )
}

function DestinationForm({ app, onAdded }: { app: App; onAdded: (app: App) => void }) {
  const [url, setUrl] = useState('')
  const [key, setKey] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const added = await addDestination(app.id, url.trim(), key.trim())
      onAdded({ ...app, destinations: [...app.destinations, added] })
      setUrl('')
      setKey('')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="form inline" onSubmit={submit}>
      <label>
        Push API address
        <input type="url" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="http://localhost:6161/ingest" required />
      </label>
      <label>
        Push API key
        <input type="password" value={key} onChange={(e) => setKey(e.target.value)} required autoComplete="off" />
      </label>
      <button type="submit" disabled={busy}>
        {busy ? 'Checking…' : 'Add destination'}
      </button>
      {error && <p className="error wide">{error}</p>}
    </form>
  )
}

/** A workspace's apps and their destinations. Members see them; admins make and change them. */
export function AppsSection({
  workspaceId,
  apps,
  admin,
  onChange,
}: {
  workspaceId: number
  apps: App[]
  admin: boolean
  onChange: (apps: App[]) => void
}) {
  const [name, setName] = useState('')
  const [returnUrls, setReturnUrls] = useState('')
  const [shown, setShown] = useState<AppWithKey | null>(null)
  const [error, setError] = useState<string | null>(null)

  const replace = (app: App) => onChange(apps.map((a) => (a.id === app.id ? app : a)))

  async function act(action: () => Promise<void>) {
    setError(null)
    try {
      await action()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  function make(event: FormEvent) {
    event.preventDefault()
    void act(async () => {
      const urls = returnUrls.split('\n').map((url) => url.trim()).filter(Boolean)
      const made = await makeApp(workspaceId, name.trim(), urls)
      onChange([...apps, made.app].sort((a, b) => a.name.localeCompare(b.name)))
      setShown(made)
      setName('')
      setReturnUrls('')
    })
  }

  return (
    <section>
      <h2>Apps</h2>
      <p className="muted">An app, such as Scry, uses Nexus with its key, and each of its destinations is where its rows go.</p>
      {error && <p className="error">{error}</p>}
      {shown && <ShownKey made={shown} onDone={() => setShown(null)} />}
      {apps.length === 0 ? (
        <p className="muted">No apps yet.</p>
      ) : (
        <ul className="list cards">
          {apps.map((app) => (
            <li key={app.id}>
              <div className="card-head">
                <div>
                  <strong>{app.name}</strong>
                  <p className="muted">
                    Made {new Date(app.createdAt).toLocaleString()}.{' '}
                    {app.keyPrefix
                      ? `Key ${app.keyPrefix}…${app.keyCreatedAt ? `, made ${new Date(app.keyCreatedAt).toLocaleString()}` : ''}.`
                      : 'No key.'}
                  </p>
                </div>
                {admin && (
                  <div className="row-actions">
                    <button type="button" className="secondary" onClick={() => act(async () => setShown(await newAppKey(app.id)))}>
                      New key
                    </button>
                    {app.keyPrefix && (
                      <button type="button" className="quiet" onClick={() => act(async () => replace(await revokeAppKey(app.id)))}>
                        Revoke key
                      </button>
                    )}
                    <button
                      type="button"
                      className="quiet"
                      onClick={() =>
                        window.confirm(`Remove ${app.name} and its destinations? Their syncs stay and ask for a new destination.`) &&
                        act(async () => {
                          await removeApp(app.id)
                          onChange(apps.filter((a) => a.id !== app.id))
                        })
                      }
                    >
                      Remove
                    </button>
                  </div>
                )}
              </div>
              {app.returnUrls.length > 0 && <p className="muted">Returns to {app.returnUrls.join(', ')}</p>}
              <ul className="sublist">
                {app.destinations.map((destination) => (
                  <li key={destination.id}>
                    <span>
                      Destination {destination.url}{' '}
                      <span className="muted">
                        (id {destination.id}, added {new Date(destination.createdAt).toLocaleString()})
                      </span>
                      <br />
                      <KeyState destination={destination} />
                    </span>
                    {admin && (
                      <span className="row-actions">
                        <button
                          type="button"
                          className="secondary"
                          onClick={() =>
                            act(async () => {
                              const checked = await checkDestination(destination.id)
                              replace({ ...app, destinations: app.destinations.map((d) => (d.id === checked.id ? checked : d)) })
                            })
                          }
                        >
                          Check key
                        </button>
                        <button
                          type="button"
                          className="quiet"
                          onClick={() =>
                            window.confirm(`Remove the destination ${destination.url}? Its syncs stay and ask for a new destination.`) &&
                            act(async () => {
                              await removeDestination(destination.id)
                              replace({ ...app, destinations: app.destinations.filter((d) => d.id !== destination.id) })
                            })
                          }
                        >
                          Remove
                        </button>
                      </span>
                    )}
                  </li>
                ))}
              </ul>
              {admin && <ReturnUrlsForm app={app} onSaved={replace} />}
              {admin && <DestinationForm app={app} onAdded={replace} />}
            </li>
          ))}
        </ul>
      )}

      {admin && (
        <form className="form panel" onSubmit={make}>
          <label>
            New app
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Scry" required maxLength={200} />
          </label>
          <label className="wide return-urls">
            Addresses its connect pages may return to, one a line
            <textarea value={returnUrls} onChange={(e) => setReturnUrls(e.target.value)} rows={3} placeholder="http://localhost:6160/sources" />
          </label>
          <div className="form-actions wide">
            <button type="submit">Make app</button>
          </div>
        </form>
      )}
    </section>
  )
}
