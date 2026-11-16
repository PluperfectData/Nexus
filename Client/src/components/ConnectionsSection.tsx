import { type FormEvent, useRef, useState } from 'react'
import { Link } from 'react-router'
import { addConnection, type App, type Connection, type Connector, startSignIn, type Sync, type SyncEdit } from '../api'
import { leaveFor } from '../browser'
import { placesOf } from '../places'
import { defaultsOf } from '../fields'
import { FieldInput } from './FieldInput'

/** A button that picks a new copy of the file a connection reads. */
function ReplaceFile({ connection, onPick }: { connection: Connection; onPick: (file: File) => void }) {
  const input = useRef<HTMLInputElement>(null)
  return (
    <>
      <button type="button" className="secondary" onClick={() => input.current?.click()}>
        Replace file
      </button>
      <input
        ref={input}
        type="file"
        hidden
        aria-label={`New copy of ${connection.fileName ?? 'its file'}`}
        onChange={(e) => {
          const file = e.target.files?.[0]
          e.target.value = ''
          if (file) onPick(file)
        }}
      />
    </>
  )
}
import { SyncTables } from './SyncTables'

/** A form for a connection: a name, a connector, and the settings that connector asks for, checked by the service. */
function ConnectionForm({
  workspaceId,
  connectors,
  onAdded,
  onCancel,
}: {
  workspaceId: number
  connectors: Connector[]
  onAdded: (connection: Connection) => void
  onCancel: () => void
}) {
  const [name, setName] = useState('')
  const [connectorId, setConnectorId] = useState(connectors[0]?.id ?? '')
  const [values, setValues] = useState<Record<string, string>>(() => defaultsOf(connectors[0]?.fields ?? []))
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const connector = connectors.find((c) => c.id === connectorId)
  const fields = connector?.fields ?? []

  const signsIn = connector?.auth === 'OAuth'
  const notReady = signsIn ? connector?.notReady : null

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (signsIn) {
        leaveFor((await startSignIn(workspaceId, name.trim(), connectorId, values)).url)
        return
      }
      onAdded(await addConnection(workspaceId, name.trim(), connectorId, values, file ?? undefined))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="form panel" onSubmit={submit}>
      <label>
        Name
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Shop database" required maxLength={200} />
      </label>
      <label>
        Connector
        <select
          value={connectorId}
          onChange={(e) => {
            setConnectorId(e.target.value)
            setValues(defaultsOf(connectors.find((c) => c.id === e.target.value)?.fields ?? []))
            setFile(null)
          }}
        >
          {connectors.map((c) => (
            <option key={c.id} value={c.id}>
              {c.name}
            </option>
          ))}
        </select>
      </label>
      {connector && <p className="hint muted wide">{connector.description}</p>}
      {fields.map((field) => (
        <FieldInput
          key={`${connectorId}.${field.name}`}
          field={field}
          value={values[field.name] ?? ''}
          onChange={(value) => setValues((current) => ({ ...current, [field.name]: value }))}
          onFile={setFile}
        />
      ))}
      {notReady && <p className="error wide">{notReady}</p>}
      {error && <p className="error wide">{error}</p>}
      <div className="form-actions wide">
        {!notReady && (
          <button type="submit" disabled={busy}>
            {busy ? 'Connecting…' : signsIn ? `Sign in to ${connector?.name}` : 'Add connection'}
          </button>
        )}
        <button type="button" className="quiet" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}

/**
 * A workspace's connections, each with its name, its connector, and the settings it is safe to show, and beneath it
 * the tables it syncs into apps' destinations.
 */
export function ConnectionsSection({
  workspaceId,
  connectors,
  connections,
  syncs,
  apps,
  onAdded,
  onRemove,
  onSyncsAdded,
  onRunSync,
  onRemoveSync,
  onEditSync,
  onReplaceFile,
  onSignInAgain,
}: {
  workspaceId: number
  connectors: Connector[]
  connections: Connection[]
  syncs: Sync[]
  apps: App[]
  onAdded: (connection: Connection) => void
  onRemove: (connection: Connection) => void
  onReplaceFile: (connection: Connection, file: File) => void
  onSignInAgain: (connection: Connection) => void
  onSyncsAdded: (syncs: Sync[]) => void
  onRunSync: (sync: Sync) => void
  onRemoveSync: (sync: Sync) => void
  onEditSync: (sync: Sync, edit: SyncEdit) => Promise<void>
}) {
  const [adding, setAdding] = useState(false)
  const nameOf = (id: string) => connectors.find((c) => c.id === id)?.name ?? id
  const places = placesOf(apps)

  return (
    <section>
      <div className="section-head">
        <h2>Connections</h2>
        {!adding && (
          <button type="button" className="secondary" onClick={() => setAdding(true)}>
            Add connection
          </button>
        )}
      </div>
      {adding && (
        <ConnectionForm
          workspaceId={workspaceId}
          connectors={connectors}
          onAdded={(connection) => {
            setAdding(false)
            onAdded(connection)
          }}
          onCancel={() => setAdding(false)}
        />
      )}
      {connections.length === 0 ? (
        <p className="muted">No connections yet.</p>
      ) : (
        <ul className="list cards">
          {connections.map((connection) => (
            <li key={connection.id} aria-label={`Connection ${connection.name}`}>
              <div className="card-head">
                <div>
                  <strong>{connection.name}</strong>
                  <p className="muted">
                    {nameOf(connection.connector)}
                    {connection.fileName && `, file ${connection.fileName}`}
                    {Object.entries(connection.settings)
                      .filter(([, value]) => value)
                      .map(([key, value]) => `, ${key} ${value}`)
                      .join('')}
                  </p>
                  {connection.needsSignIn && <p className="error">It needs a new sign-in.</p>}
                </div>
                <div className="row-actions">
                  {connection.needsSignIn && (
                    <button type="button" className="secondary" onClick={() => onSignInAgain(connection)}>
                      Sign in again
                    </button>
                  )}
                  {connection.fileName && <ReplaceFile connection={connection} onPick={(file) => onReplaceFile(connection, file)} />}
                  <Link to={`/log?connection=${connection.id}`} className="row-link" aria-label={`Log for connection ${connection.name}`}>
                    Log
                  </Link>
                  <button type="button" className="quiet" onClick={() => onRemove(connection)}>
                    Remove
                  </button>
                </div>
              </div>
              <SyncTables
                connection={connection}
                syncs={syncs.filter((s) => s.connectionId === connection.id)}
                places={places}
                onAdded={onSyncsAdded}
                onRun={onRunSync}
                onRemove={onRemoveSync}
                onEdit={onEditSync}
              />
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
