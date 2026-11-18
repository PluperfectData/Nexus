import { useEffect, useState } from 'react'
import { Link, NavLink, useNavigate, useParams } from 'react-router'
import {
  type App,
  type Connection,
  type Connector,
  editSync,
  getWorkspace,
  listApps,
  listConnections,
  listConnectors,
  listProviders,
  listSyncs,
  removeConnection,
  removeSync,
  removeWorkspace,
  renewSignIn,
  replaceConnectionFile,
  runSync,
  type Provider,
  type Sync,
  type WorkspaceDetail,
} from '../api'
import { AppsSection } from '../components/AppsSection'
import { ConnectionsSection } from '../components/ConnectionsSection'
import { MembersSection } from '../components/MembersSection'
import { ProvidersSection } from '../components/ProvidersSection'
import { ReminderEmailSection } from '../components/ReminderEmailSection'
import { leaveFor } from '../browser'
import { arrivalQuery, clearQuery } from '../query'
import { useSession } from '../session'
import { rememberWorkspace, useWorkspaces, workspacePath } from '../workspace'

// How often the page asks again while a sync is queued or running.
const POLL_MS = 2000

const busy = (syncs: Sync[]) => syncs.some((s) => s.status === 'Queued' || s.status === 'Running')

/** The page's tabs, by the last part of their path. Connections, at the workspace's own path, has none. */
type Tab = '' | 'providers' | 'reminder-email' | 'apps' | 'members'

const LABELS: Record<Tab, string> = {
  '': 'Connections',
  providers: 'Providers',
  'reminder-email': 'Session expiry reminder email',
  apps: 'Apps',
  members: 'Members',
}

const TABS = Object.keys(LABELS) as Tab[]

/** One workspace, a tab for each of its connections, providers, reminder email, apps, and, for an admin, members. */
export function WorkspacePage() {
  const params = useParams()
  const id = Number(params.id)
  const tab = params.tab ?? ''
  const { me } = useSession()
  const { refresh } = useWorkspaces()
  const navigate = useNavigate()
  const [workspace, setWorkspace] = useState<WorkspaceDetail | null>(null)
  const [connectors, setConnectors] = useState<Connector[]>([])
  const [connections, setConnections] = useState<Connection[]>([])
  const [apps, setApps] = useState<App[]>([])
  const [syncs, setSyncs] = useState<Sync[]>([])
  const [providers, setProviders] = useState<Provider[]>([])
  // A sign-in to a provider comes back here with the connection it made or renewed, or with why it failed.
  const [arrival] = useState(arrivalQuery)
  const [error, setError] = useState<string | null>(() => arrival.get('error'))
  const notice = arrival.has('signedIn') ? 'Signed in. Its tables are added from its card below.' : null
  // The connections whose tables show. The one a sign-in came back with starts open.
  const [open, setOpen] = useState<ReadonlySet<number>>(() => new Set(arrival.has('signedIn') ? [Number(arrival.get('signedIn'))] : []))

  useEffect(clearQuery, [])

  useEffect(() => {
    Promise.all([getWorkspace(id), listConnectors(), listConnections(id), listApps(id), listSyncs(id), listProviders(id)])
      .then(([found, connectorList, connectionList, appList, syncList, providerList]) => {
        rememberWorkspace(found.id)
        setWorkspace(found)
        setConnectors(connectorList)
        setConnections(connectionList)
        setApps(appList)
        setSyncs(syncList)
        setProviders(providerList)
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

  // Providers and the reminder email matter only where a connector signs in, and only admins manage members.
  const tabs = TABS.filter((t) =>
    t === 'providers' || t === 'reminder-email' ? providers.length > 0 : t === 'members' ? (me?.isAdmin ?? false) : true,
  )
  const shown = workspace !== null && (tabs as string[]).includes(tab)
  useEffect(() => {
    if (workspace && !shown) navigate(workspacePath(workspace.id), { replace: true })
  }, [workspace, shown, navigate])

  async function act(action: () => Promise<void>) {
    setError(null)
    try {
      await action()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const toggle = (connection: Connection) =>
    setOpen((current) => {
      const next = new Set(current)
      if (!next.delete(connection.id)) next.add(connection.id)
      return next
    })

  if (!workspace) return <p className={error ? 'page error' : 'page muted'}>{error ?? 'Loading…'}</p>

  // A connector that signs in is ready here once this workspace's app for it is.
  const ready = connectors.map((c) => ({ ...c, notReady: providers.find((a) => a.connector === c.id)?.problem ?? c.notReady }))

  return (
    <div className="page">
      <p>
        <Link to="/workspaces">Workspaces</Link>
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
                refresh()
                navigate('/workspaces')
              })
            }
          >
            Remove workspace
          </button>
        )}
      </div>
      <nav className="tabs" aria-label="Workspace sections">
        {tabs.map((t) => (
          <NavLink
            key={t || 'connections'}
            to={t ? `${workspacePath(workspace.id)}/${t}` : workspacePath(workspace.id)}
            end
            className={({ isActive }) => (isActive ? 'is-active' : '')}
          >
            {LABELS[t]}
          </NavLink>
        ))}
      </nav>
      {notice && <p className="success">{notice}</p>}
      {error && <p className="error">{error}</p>}

      {tab === '' && (
        <ConnectionsSection
          workspaceId={workspace.id}
          connectors={ready}
          connections={connections}
          syncs={syncs}
          apps={apps}
          open={open}
          onToggle={toggle}
          onAdded={(connection) => {
            setConnections((current) => [...current, connection].sort((a, b) => a.name.localeCompare(b.name)))
            setOpen((current) => new Set(current).add(connection.id))
          }}
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
          onSignInAgain={(connection) => act(async () => leaveFor((await renewSignIn(connection.id)).url))}
          onReplaceFile={(connection, file) => act(async () => {
            const replaced = await replaceConnectionFile(connection.id, file)
            setConnections((current) => current.map((c) => (c.id === replaced.id ? replaced : c)))
            // Replacing the file queues a run of each of its syncs.
            setSyncs(await listSyncs(id))
          })}
        />
      )}

      {tab === 'providers' && shown && <ProvidersSection workspaceId={workspace.id} apps={providers} onChange={setProviders} />}

      {tab === 'reminder-email' && shown && <ReminderEmailSection workspaceId={workspace.id} />}

      {tab === 'apps' && (
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
      )}

      {tab === 'members' && shown && <MembersSection workspace={workspace} onChange={setWorkspace} />}
    </div>
  )
}
