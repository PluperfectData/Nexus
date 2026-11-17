import { type FormEvent, useEffect, useState } from 'react'
import { useNavigate } from 'react-router'
import { type DatabaseForm, getInstall, getSettings, saveDatabase, saveLogDays, type Settings } from '../api'
import { AddressesForm, DatabaseFields, MailForm } from '../components/InstanceForms'
import { settingsMail } from '../mail'
import { useSession } from '../session'

/** The instance's own settings, for admins: its addresses, its mail, and its database. */
export function SettingsPage() {
  const { me } = useSession()
  const navigate = useNavigate()
  const [settings, setSettings] = useState<Settings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [moved, setMoved] = useState(false)

  useEffect(() => {
    if (!me?.isAdmin) return
    getSettings()
      .then(setSettings)
      .catch((e: Error) => setError(e.message))
  }, [me?.isAdmin])

  if (!me?.isAdmin) return <p className="page muted">Only admins change the instance's settings.</p>

  async function changeDatabase(database: DatabaseForm) {
    const current = settings?.database
    const elsewhere = !current || current.host !== database.host || current.port !== database.port || current.name !== database.name
    if (elsewhere && !window.confirm(`Move Nexus to ${database.name} on ${database.host}? It shows the records there from now on.`)) return
    setMoved(false)
    setSettings(await saveDatabase(database))
    setMoved(true)
    // A database with no admin, or none Nexus knows, sends the instance back to the installer.
    if (!(await getInstall()).installed) navigate('/install')
  }

  return (
    <div className="page">
      <h1>Settings</h1>
      {error && <p className="error">{error}</p>}
      {settings === null ? (
        !error && <p className="muted">Loading…</p>
      ) : (
        <>
          <section>
            <h2>Addresses</h2>
            <AddressesForm settings={settings} onSaved={setSettings} />
          </section>
          <section>
            <h2>Mail</h2>
            <MailForm mail={settings.mail} actions={settingsMail(setSettings)} />
          </section>
          <section>
            <h2>Database</h2>
            {settings.databaseConfigured ? (
              <p className="muted">
                The service's configuration sets the database, in ConnectionStrings:Nexus
                {settings.database ? `: ${settings.database.name} on ${settings.database.host}:${settings.database.port}, as ${settings.database.user}.` : '.'}
              </p>
            ) : (
              <>
                <p className="muted">Nexus opens a new database and brings it up to date before it moves to it.</p>
                {moved && <p className="success">Nexus uses this database now.</p>}
                <DatabaseFields place={settings.database} keepsPassword submitLabel="Test and use this database" onSubmit={changeDatabase} />
              </>
            )}
          </section>
          <section>
            <h2>Log</h2>
            <LogDaysForm settings={settings} onSaved={setSettings} />
          </section>
        </>
      )}
    </div>
  )
}

/** How many days the Log page keeps an entry. */
function LogDaysForm({ settings, onSaved }: { settings: Settings; onSaved: (settings: Settings) => void }) {
  const [days, setDays] = useState(String(settings.logDays))
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  if (settings.logDaysConfigured) {
    return <p className="muted">The service's configuration keeps log entries for {settings.logDays} days, in Nexus:LogDays.</p>
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      onSaved(await saveLogDays(Number(days)))
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="form inline" onSubmit={submit}>
      <label>
        Keep entries for this many days
        <input type="number" min={1} max={365} step={1} value={days} onChange={(e) => setDays(e.target.value)} required />
      </label>
      <div className="form-actions">
        <button type="submit" disabled={busy}>
          {busy ? 'Saving…' : 'Save'}
        </button>
        {saved && <span className="success">Saved.</span>}
      </div>
      {error && <p className="error wide">{error}</p>}
    </form>
  )
}
