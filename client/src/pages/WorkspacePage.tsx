import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import {
  type App,
  type Connection,
  type Connector,
  editSync,
  getWorkspace,
  listApps,
  listConnections,
  listConnectors,
  listSyncs,
  removeConnection,
  removeSync,
  removeWorkspace,
  replaceConnectionFile,
  runSync,
  type Sync,
  type WorkspaceDetail,
} from '../api'
import { AppsSection } from '../components/AppsSection'
import { ConnectionsSection } from '../components/ConnectionsSection'
import { MembersSection } from '../components/MembersSection'
import { useSession } from '../session'

// How often the page asks again while a sync is queued or running.
const POLL_MS = 2000

const busy = (syncs: Sync[]) => syncs.some((s) => s.status === 'Queued' || s.status === 'Running')

/** One workspace: its connections with the tables each syncs, its apps, and, for an admin, its members. */
export function WorkspacePage() {
  const id = Number(useParams().id)
  const { me } = useSession()
  const navigate = useNavigate()
  const [workspace, setWorkspace] = useState<WorkspaceDetail | null>(null)
  const [connectors, setConnectors] = useState<Connector[]>([])
  const [connections, setConnections] = useState<Connection[]>([])
  const [apps, setApps] = useState<App[]>([])
  const [syncs, setSyncs] = useState<Sync[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    Promise.all([getWorkspace(id), listConnectors(), listConnections(id), listApps(id), listSyncs(id)])
      .then(([found, connectorList, connectionList, appList, syncList]) => {
        setWorkspace(found)
        setConnectors(connectorList)
        setConnections(connectionList)
        setApps(appList)
        setSyncs(syncList)
      })
      .catch((e: Error) => setError(e.message))
  }, [id])

  const polling = busy(syncs)
  useEffect(() => {
    if (!polling) return
    const timer = setInterval(() => {
      listSyncs(id).then(setSyncs).catch(() => {})
    }, POLL_MS)
    return () => clearInterval(timer)
  }, [id, polling])

  async function act(action: () => Promise<void>) {
    setError(null)
    try {
      await action()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  if (!workspace) return <p className={error ? 'page error' : 'page muted'}>{error ?? 'Loading…'}</p>

  return (
    <div className="page">
      <p>
        <Link to="/">Workspaces</Link>
      </p>
      <div className="section-head">
        <h1>{workspace.name}</h1>
        {me?.isAdmin && (
          <button
            type="button"
            className="quiet"
            onClick={() =>
              window.confirm(`Remove ${workspace.name} with its apps, connections, and syncs? The tables they filled stay.`) &&
              act(async () => {
                await removeWorkspace(workspace.id)
                navigate('/')
              })
            }
          >
            Remove workspace
          </button>
        )}
      </div>
      {error && <p className="error">{error}</p>}

      <ConnectionsSection
        workspaceId={workspace.id}
        connectors={connectors}
        connections={connections}
        syncs={syncs}
        apps={apps}
        onAdded={(connection) => setConnections((current) => [...current, connection].sort((a, b) => a.name.localeCompare(b.name)))}
        onRemove={(connection) =>
          window.confirm(`Remove ${connection.name} and its tables' syncs? The tables they filled stay.`) &&
          act(async () => {
            await removeConnection(connection.id)
            setConnections((current) => current.filter((c) => c.id !== connection.id))
            setSyncs((current) => current.filter((s) => s.connectionId !== connection.id))
          })
        }
        onSyncsAdded={(added) => setSyncs((current) => [...current, ...added].sort((a, b) => a.table.localeCompare(b.table)))}
        onRunSync={(sync) => act(async () => {
          const queued = await runSync(sync.id)
          setSyncs((current) => current.map((s) => (s.id === queued.id ? queued : s)))
        })}
        onRemoveSync={(sync) => act(async () => {
          await removeSync(sync.id)
          setSyncs((current) => current.filter((s) => s.id !== sync.id))
        })}
        onEditSync={async (sync, edit) => {
          const saved = await editSync(sync.id, edit)
          setSyncs((current) => current.map((s) => (s.id === saved.id ? saved : s)))
        }}
        onReplaceFile={(connection, file) => act(async () => {
          const replaced = await replaceConnectionFile(connection.id, file)
          setConnections((current) => current.map((c) => (c.id === replaced.id ? replaced : c)))
          // Replacing the file queues a run of each of its syncs.
          setSyncs(await listSyncs(id))
        })}
      />

      <AppsSection
        workspaceId={workspace.id}
        apps={apps}
        admin={me?.isAdmin ?? false}
        onChange={(next) => {
          setApps(next)
          // Removing an app or a destination leaves its syncs without one.
          listSyncs(id).then(setSyncs).catch(() => {})
        }}
      />

      {me?.isAdmin && <MembersSection workspace={workspace} onChange={setWorkspace} />}
    </div>
  )
}
