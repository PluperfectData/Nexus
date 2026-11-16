import { type FormEvent, useState } from 'react'
import { listSignInApps, removeSignInApp, saveSignInApp, type SignInApp } from '../api'
import { CopyValue } from './InstanceForms'

/** One provider's app: whether it is set, the callback to register on it, and its key and secret, which are never shown. */
function SignInAppCard({ workspaceId, app, onChange }: { workspaceId: number; app: SignInApp; onChange: (apps: SignInApp[]) => void }) {
  const [key, setKey] = useState('')
  const [secret, setSecret] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  async function act(action: () => Promise<void>) {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      await action()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    void act(async () => {
      await saveSignInApp(workspaceId, app.connector, key.trim(), secret.trim())
      onChange(await listSignInApps(workspaceId))
      setKey('')
      setSecret('')
      setNotice('Saved.')
    })
  }

  const remove = () =>
    window.confirm(`Remove the ${app.name} app? This workspace's ${app.name} connections stop syncing until another is set.`) &&
    act(async () => {
      await removeSignInApp(workspaceId, app.connector)
      onChange(await listSignInApps(workspaceId))
    })

  const status = app.configured
    ? `The service's configuration sets this app for every workspace, with the key ending ${app.keyEnding}.`
    : app.set
      ? `Set, with the key ending ${app.keyEnding}${app.updatedAt ? `, on ${new Date(app.updatedAt).toLocaleDateString()}` : ''}.`
      : 'Not set yet.'

  return (
    <>
      <div className="card-head">
        <strong>{app.name}</strong>
      </div>
      <p className="muted">{status}</p>
      {app.set && app.problem && <p className="error">{app.problem}</p>}
      {app.callbackUrl && (
        <CopyValue
          label="Callback to register on the app"
          value={app.callbackUrl}
          note={`Register this exact address on the ${app.name} app, as its own entry.`}
        />
      )}
      {!app.configured && (
        <form className="form" onSubmit={submit}>
          <label>
            App key
            <input
              value={key}
              onChange={(e) => setKey(e.target.value)}
              required={!app.set}
              placeholder={app.set ? `Ending ${app.keyEnding}` : ''}
              autoComplete="off"
            />
          </label>
          <label>
            App secret
            <input
              type="password"
              value={secret}
              onChange={(e) => setSecret(e.target.value)}
              required={!app.set}
              placeholder={app.set ? 'Stored' : ''}
              autoComplete="off"
            />
          </label>
          <p className="hint muted wide">
            {app.set
              ? `Leave either blank to keep the stored one. Nexus never shows the secret again.`
              : `Both are on the app's page in the ${app.name} developer portal.`}
          </p>
          {error && <p className="error wide">{error}</p>}
          {notice && <p className="success wide">{notice}</p>}
          <div className="form-actions wide">
            <button type="submit" disabled={busy}>
              Save app
            </button>
            {app.set && (
              <button type="button" className="quiet" onClick={remove} disabled={busy}>
                Remove app
              </button>
            )}
          </div>
        </form>
      )}
    </>
  )
}

/** The apps a workspace signs in to providers through, such as its Schwab app. */
export function SignInAppsSection({ workspaceId, apps, onChange }: { workspaceId: number; apps: SignInApp[]; onChange: (apps: SignInApp[]) => void }) {
  if (apps.length === 0) return null

  return (
    <section>
      <div className="section-head">
        <h2>Sign-in apps</h2>
      </div>
      <p className="muted">
        Providers such as Charles Schwab sign people in through an app this workspace registers with them. Its connections
        to that provider sign in and stay signed in through it.
      </p>
      <ul className="list cards">
        {apps.map((app) => (
          <li key={app.connector}>
            <SignInAppCard workspaceId={workspaceId} app={app} onChange={onChange} />
          </li>
        ))}
      </ul>
    </section>
  )
}
