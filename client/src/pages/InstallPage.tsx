import { type FormEvent, useEffect, useState } from 'react'
import { useNavigate } from 'react-router'
import {
  askForInstallCode,
  type DatabaseForm,
  getInstall,
  getSettings,
  installAddress,
  installAdmin,
  installDatabase,
  type InstallView,
  type Settings,
} from '../api'
import { AddressesForm, DatabaseFields, MailForm } from '../components/InstanceForms'
import { rememberNext } from '../next'
import { useSession } from '../session'

const MIN_PASSWORD = 12

// The code arrives after the # of the link the service logs.
function codeInLink(): string | null {
  return decodeURIComponent(window.location.hash.slice(1)).trim() || null
}

/**
 * The first-run installer: the database, then the client's address, then the first admin, then the addresses and mail.
 * On an instance already set up, it makes an admin for someone locked out.
 */
export function InstallPage() {
  const { me, setMe } = useSession()
  const navigate = useNavigate()
  const [code, setCode] = useState<string | null>(codeInLink)
  const [view, setView] = useState<InstallView | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [finishing, setFinishing] = useState<Settings | null>(null)

  // The code leaves the address bar once read, so it stays out of the browser's history.
  useEffect(() => {
    if (window.location.hash) window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search)
  }, [])

  useEffect(() => {
    getInstall(code)
      .then(setView)
      .catch((e: Error) => setError(e.message))
  }, [code])

  async function madeAdmin(made: Awaited<ReturnType<typeof installAdmin>>) {
    setMe(made)
    if (view?.installed) {
      navigate('/')
      return
    }
    setFinishing(await getSettings())
    setView(await getInstall(code))
  }

  function body() {
    if (view === null) return error ? null : <p className="muted">Loading…</p>
    if (finishing) return <Finish settings={finishing} onSaved={setFinishing} onDone={() => navigate('/')} />
    if (view.installed) return <Installed view={view} code={code} signedIn={!!me?.signedIn} onCode={setCode} onAdmin={madeAdmin} />
    if (!view.allowed) return <CodeStage given={code} recovery={false} onCode={setCode} />
    switch (view.stage) {
      case 'Database':
        return <DatabaseStage view={view} onSave={async (database) => setView(await installDatabase(code, database))} />
      case 'Address':
        return <AddressStage onSave={async (origin) => setView(await installAddress(code, origin))} />
      case 'Admin':
        return <AdminStage onMake={async (email, password) => madeAdmin(await installAdmin(code, email, password))} />
      default:
        return null
    }
  }

  return (
    <div className="page narrow">
      <h1>Set up Nexus</h1>
      {error && <p className="error">{error}</p>}
      {body()}
    </div>
  )
}

function Steps({ at }: { at: number }) {
  const names = ['Database', 'Address', 'First admin', 'Mail']
  return (
    <p className="muted">
      Step {at} of {names.length}: {names[at - 1]}
    </p>
  )
}

/** Asks for the install code, for a page on another machine than the service's. */
function CodeStage({ given, recovery, onCode }: { given: string | null; recovery: boolean; onCode: (code: string) => void }) {
  const [entered, setEntered] = useState('')
  const [asked, setAsked] = useState(false)
  const [error, setError] = useState<string | null>(null)

  function submit(event: FormEvent) {
    event.preventDefault()
    onCode(entered.trim())
  }

  async function ask() {
    setError(null)
    try {
      await askForInstallCode()
      setAsked(true)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <>
      <p>
        {recovery
          ? 'This page is not on the machine Nexus runs on, so it needs an install code. Ask for one, and the service writes it to its log, good for an hour.'
          : 'This page is not on the machine Nexus runs on, so it needs the install code. The service writes the code to its log when it starts, with a link to this page that carries it.'}
      </p>
      {given && <p className="error">That is not the install code. Copy it from the service's log.</p>}
      <form className="form stacked" onSubmit={submit}>
        <label>
          Install code
          <input value={entered} onChange={(e) => setEntered(e.target.value)} placeholder="ABCD-EFGH-JKMN" required autoComplete="off" />
        </label>
        {error && <p className="error">{error}</p>}
        {asked && <p className="success">The service wrote the code to its log.</p>}
        <div className="form-actions">
          <button type="submit">Continue</button>
          <button type="button" className="link-button" onClick={ask}>
            {recovery ? 'Write a code to the log' : 'Write the code to the log again'}
          </button>
        </div>
      </form>
    </>
  )
}

function DatabaseStage({ view, onSave }: { view: InstallView; onSave: (database: DatabaseForm) => Promise<void> }) {
  if (view.databaseConfigured) {
    return (
      <>
        <Steps at={1} />
        <p>
          The service's configuration sets its database, in ConnectionStrings:Nexus, and Nexus cannot open it
          {view.databaseProblem ? `: ${view.databaseProblem}` : '.'}
        </p>
        <p className="muted">Fix the connection string, or remove it to set the database here, then restart the service.</p>
      </>
    )
  }

  return (
    <>
      <Steps at={1} />
      {view.databaseProblem ? (
        <p className="error">Nexus cannot open its database: {view.databaseProblem}</p>
      ) : (
        <p>Nexus keeps its users, workspaces, connections, and syncs in a MySQL database.</p>
      )}
      <DatabaseFields place={view.database} keepsPassword={false} submitLabel="Test and save" onSubmit={onSave} />
    </>
  )
}

function AddressStage({ onSave }: { onSave: (origin: string) => Promise<void> }) {
  const [origin, setOrigin] = useState(window.location.origin)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onSave(origin.trim())
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Steps at={2} />
      <p>Nexus sends people to its client from mailed links and connect pages, and takes requests from its pages alone.</p>
      <form className="form stacked" onSubmit={submit}>
        <label>
          Client address
          <input type="url" value={origin} onChange={(e) => setOrigin(e.target.value)} required />
        </label>
        <p className="hint muted">This page's address, unless people open Nexus somewhere else.</p>
        {error && <p className="error">{error}</p>}
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            Save address
          </button>
        </div>
      </form>
    </>
  )
}

/** An email and a password, twice: the first admin, or an admin made for someone locked out. */
function AdminForm({ submitLabel, onMake }: { submitLabel: string; onMake: (email: string, password: string) => Promise<void> }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (password !== confirm) {
      setError('The two passwords differ.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await onMake(email.trim(), password)
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  return (
    <form className="form stacked" onSubmit={submit}>
      <label>
        Email
        <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="email" />
      </label>
      <label>
        Password
        <input
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          minLength={MIN_PASSWORD}
          required
          autoComplete="new-password"
        />
      </label>
      <label>
        The same password again
        <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required autoComplete="new-password" />
      </label>
      <p className="hint muted">At least {MIN_PASSWORD} characters.</p>
      {error && <p className="error">{error}</p>}
      <div className="form-actions">
        <button type="submit" disabled={busy}>
          {submitLabel}
        </button>
      </div>
    </form>
  )
}

function AdminStage({ onMake }: { onMake: (email: string, password: string) => Promise<void> }) {
  return (
    <>
      <Steps at={3} />
      <p>The first admin makes the workspaces, invites everyone else, and links apps such as Scry.</p>
      <AdminForm submitLabel="Make the admin and sign in" onMake={onMake} />
    </>
  )
}

function Finish({ settings, onSaved, onDone }: { settings: Settings; onSaved: (settings: Settings) => void; onDone: () => void }) {
  return (
    <>
      <Steps at={4} />
      <p>
        Nexus is set up. Set the address providers send people back to and how it sends mail now, or later on the Settings
        page.
      </p>
      <h2>Addresses</h2>
      <AddressesForm settings={settings} onSaved={onSaved} />
      <h2>Mail</h2>
      <MailForm settings={settings} onSaved={onSaved} />
      <div className="form-actions">
        <button type="button" onClick={onDone}>
          Done
        </button>
      </div>
    </>
  )
}

/** An instance already set up: signing in, and making an admin for someone locked out. */
function Installed({
  view,
  code,
  signedIn,
  onCode,
  onAdmin,
}: {
  view: InstallView
  code: string | null
  signedIn: boolean
  onCode: (code: string) => void
  onAdmin: (made: Awaited<ReturnType<typeof installAdmin>>) => Promise<void>
}) {
  const navigate = useNavigate()
  const [recovering, setRecovering] = useState(false)

  function signIn() {
    rememberNext('/settings')
    navigate('/sign-in')
  }

  return (
    <>
      <p>Nexus is set up.</p>
      {!signedIn && (
        <>
          <p className="muted">Sign in to finish on the Settings page: the address providers send people back to, and mail.</p>
          <div className="form-actions">
            <button type="button" onClick={signIn}>
              Sign in
            </button>
          </div>
        </>
      )}
      <section>
        <h2>Locked out</h2>
        {!recovering ? (
          <>
            <p className="muted">When no admin can sign in, make one here.</p>
            <button type="button" className="secondary" onClick={() => setRecovering(true)}>
              Make an admin
            </button>
          </>
        ) : view.allowed ? (
          <>
            <p className="muted">
              This makes the address an admin with this password, and signs it out everywhere else. A new address becomes a
              new admin.
            </p>
            <AdminForm submitLabel="Make the admin and sign in" onMake={async (email, password) => onAdmin(await installAdmin(code, email, password))} />
          </>
        ) : (
          <CodeStage given={code} recovery onCode={onCode} />
        )}
      </section>
    </>
  )
}
