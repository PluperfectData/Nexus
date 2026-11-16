import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router'
import { type DatabaseForm, getInstall, getSettings, saveDatabase, type Settings } from '../api'
import { AddressesForm, DatabaseFields, MailForm } from '../components/InstanceForms'
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
            <MailForm settings={settings} onSaved={setSettings} />
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
        </>
      )}
    </div>
  )
}
