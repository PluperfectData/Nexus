import { type FormEvent, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { addSyncs, type Connection, listStreams, type Stream, type Sync, type SyncEdit, type SyncMode } from '../api'
import { type Place, placeLabel, targetOf } from '../places'
import { initialPicks, MODES, type Pick, picked } from '../streams'
import { StreamPicks } from './StreamPicks'

const NO_DESTINATION = 'Its destination was removed. Pick a new one to send it to.'

const ALL_MODES = Object.keys(MODES) as SyncMode[]

/** Adds syncs from one connection's streams, each picked with a table name and a mode, into one destination. */
function AddTablesForm({
  connection,
  places,
  onAdded,
  onCancel,
}: {
  connection: Connection
  places: Place[]
  onAdded: (syncs: Sync[]) => void
  onCancel: () => void
}) {
  const [destinationId, setDestinationId] = useState(places[0]?.id ?? 0)
  const [streams, setStreams] = useState<Stream[] | null>(null)
  const [picks, setPicks] = useState<Record<string, Pick>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function load() {
    setError(null)
    try {
      const found = await listStreams(connection.id)
      setStreams(found)
      setPicks(initialPicks(found))
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const pick = (stream: string, change: Partial<Pick>) => setPicks((current) => ({ ...current, [stream]: { ...current[stream], ...change } }))
  const chosen = picked(picks)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onAdded(await addSyncs(connection.id, destinationId, chosen))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (places.length === 0) {
    return (
      <p className="muted">
        A table needs a destination, and this workspace has none. An admin adds one to an app below.{' '}
        <button type="button" className="link-button" onClick={onCancel}>
          Close
        </button>
      </p>
    )
  }

  return (
    <form className="form panel" onSubmit={submit}>
      <label className="wide">
        Into
        <select value={destinationId} onChange={(e) => setDestinationId(Number(e.target.value))}>
          {places.map((place) => (
            <option key={place.id} value={place.id}>
              {placeLabel(place)}
            </option>
          ))}
        </select>
      </label>
      {streams === null ? (
        <div className="wide">
          <button type="button" className="secondary" onClick={() => void load()}>
            List its streams
          </button>
        </div>
      ) : (
        <StreamPicks streams={streams} picks={picks} onPick={pick} />
      )}
      {error && <p className="error wide">{error}</p>}
      <div className="form-actions wide">
        <button type="submit" disabled={busy || chosen.length === 0}>
          {busy ? 'Adding…' : chosen.length > 1 ? `Add ${chosen.length} syncs` : 'Add sync'}
        </button>
        <button type="button" className="quiet" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}

/** Changes where one sync writes and how it reads its stream. */
function EditForm({
  sync,
  places,
  onSave,
  onCancel,
}: {
  sync: Sync
  places: Place[]
  onSave: (edit: SyncEdit) => Promise<void>
  onCancel: (() => void) | null
}) {
  const [destinationId, setDestinationId] = useState(sync.destinationId ?? places[0]?.id ?? 0)
  const [table, setTable] = useState(sync.table)
  const [mode, setMode] = useState<SyncMode>(sync.mode)
  const [cursorField, setCursorField] = useState(sync.cursorField ?? '')
  const [stream, setStream] = useState<Stream | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // The stream's modes and cursor columns. Without them, the service still checks a change of mode when it is saved.
  useEffect(() => {
    let live = true
    listStreams(sync.connectionId)
      .then((found) => {
        if (live) setStream(found.find((s) => s.name === sync.stream) ?? null)
      })
      .catch(() => {})
    return () => {
      live = false
    }
  }, [sync.connectionId, sync.stream])

  const modes = stream?.syncModes ?? ALL_MODES
  const cursors = stream?.cursorFields ?? (sync.cursorField ? [sync.cursorField] : [])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onSave({ destinationId, table: table.trim(), mode, cursorField: mode === 'Incremental' && cursorField ? cursorField : null })
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  if (places.length === 0) {
    return <p className="muted">There is no destination to send it to. An admin adds one to an app below.</p>
  }

  return (
    <form className="form panel" onSubmit={submit}>
      <label className="wide">
        Into
        <select value={destinationId} onChange={(e) => setDestinationId(Number(e.target.value))}>
          {places.map((place) => (
            <option key={place.id} value={place.id}>
              {placeLabel(place)}
            </option>
          ))}
        </select>
      </label>
      <label>
        Table
        <input value={table} onChange={(e) => setTable(e.target.value)} required maxLength={63} />
      </label>
      <label>
        Read
        <select value={mode} onChange={(e) => setMode(e.target.value as SyncMode)}>
          {modes.map((m) => (
            <option key={m} value={m}>
              {MODES[m]}
            </option>
          ))}
        </select>
      </label>
      {mode === 'Incremental' && (
        <label>
          Track
          <select value={cursorField} onChange={(e) => setCursorField(e.target.value)}>
            <option value="">The stream's own choice</option>
            {cursors.map((field) => (
              <option key={field} value={field}>
                {field}
              </option>
            ))}
          </select>
        </label>
      )}
      <p className="hint muted wide">Saving reads the stream from the start. The table it wrote before stays where it is.</p>
      {error && <p className="error wide">{error}</p>}
      <div className="form-actions wide">
        <button type="submit" disabled={busy}>
          {busy ? 'Saving…' : 'Save'}
        </button>
        {onCancel && (
          <button type="button" className="quiet" onClick={onCancel}>
            Cancel
          </button>
        )}
      </div>
    </form>
  )
}

function lastRunText(sync: Sync): string {
  if (sync.status === 'Queued') return 'Queued'
  if (sync.status === 'Running') return 'Running'
  const run = sync.lastRun
  if (!run) return 'Not run yet'
  const when = run.endedAt ? new Date(run.endedAt).toLocaleString() : ''
  return run.status === 'Succeeded' ? `Sent ${run.rows.toLocaleString()} rows, ${when}` : `Failed, ${when}: ${run.error ?? 'no reason given'}`
}

/** One sync: what it reads, where it writes, how its last run went, and what can be done with it. */
function SyncRow({
  sync,
  places,
  onRun,
  onRemove,
  onEdit,
}: {
  sync: Sync
  places: Place[]
  onRun: (sync: Sync) => void
  onRemove: (sync: Sync) => void
  onEdit: (sync: Sync, edit: SyncEdit) => Promise<void>
}) {
  const target = targetOf(sync)
  const [editing, setEditing] = useState(target === null)
  const busy = sync.status === 'Queued' || sync.status === 'Running'

  return (
    <li>
      <div className="card-head">
        <div>
          <strong>{sync.table}</strong>
          <p className="muted">
            Reads {sync.stream} from {sync.connectionName} ({sync.connectorName}), {MODES[sync.mode].toLowerCase()}
            {sync.cursorField ? `, tracking ${sync.cursorField}` : ''}.
          </p>
          {target ? <p className="muted">Writes into {target}.</p> : <p className="error">{NO_DESTINATION}</p>}
          <p className={sync.status === 'Failed' ? 'error' : 'muted'}>{lastRunText(sync)}</p>
        </div>
        <div className="row-actions">
          <button type="button" className="secondary" disabled={busy || target === null} onClick={() => onRun(sync)}>
            Run now
          </button>
          <button type="button" className="secondary" disabled={busy} aria-expanded={editing} onClick={() => setEditing(!editing)}>
            Edit
          </button>
          <Link to={`/log?sync=${sync.id}`} className="row-link" aria-label={`Log for sync ${sync.table}`}>
            Log
          </Link>
          <button type="button" className="quiet" disabled={sync.status === 'Running'} onClick={() => onRemove(sync)}>
            Remove
          </button>
        </div>
      </div>
      {editing && (
        <EditForm
          sync={sync}
          places={places}
          onSave={async (edit) => {
            await onEdit(sync, edit)
            setEditing(false)
          }}
          onCancel={target === null ? null : () => setEditing(false)}
        />
      )}
    </li>
  )
}

/** One connection's tables: the syncs that read its streams, each with where it writes, and the form that adds more. */
export function SyncTables({
  connection,
  syncs,
  places,
  onAdded,
  onRun,
  onRemove,
  onEdit,
}: {
  connection: Connection
  syncs: Sync[]
  places: Place[]
  onAdded: (syncs: Sync[]) => void
  onRun: (sync: Sync) => void
  onRemove: (sync: Sync) => void
  onEdit: (sync: Sync, edit: SyncEdit) => Promise<void>
}) {
  const [adding, setAdding] = useState(false)

  return (
    <div className="tables-block">
      <div className="section-head">
        <h3>Tables</h3>
        {!adding && (
          <button type="button" className="secondary" onClick={() => setAdding(true)}>
            Add tables
          </button>
        )}
      </div>
      {adding && (
        <AddTablesForm
          connection={connection}
          places={places}
          onAdded={(added) => {
            setAdding(false)
            onAdded(added)
          }}
          onCancel={() => setAdding(false)}
        />
      )}
      {syncs.length === 0 ? (
        <p className="muted">No tables yet. Add tables to sync this connection's streams into an app.</p>
      ) : (
        <ul className="tables">
          {/* A sync whose destination changes mounts again, so one left without a destination opens its form. */}
          {syncs.map((sync) => (
            <SyncRow key={`${sync.id}.${sync.destinationId}`} sync={sync} places={places} onRun={onRun} onRemove={onRemove} onEdit={onEdit} />
          ))}
        </ul>
      )}
    </div>
  )
}
