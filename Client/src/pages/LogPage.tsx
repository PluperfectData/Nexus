import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { type EntryLevel, getLog, type LogEntries, type LogEntry, type LogFilter, listWorkspaces, type Workspace } from '../api'
import { LevelIcon } from '../components/LevelIcon'
import { useSession } from '../session'

const INSTANCE = 'instance'

const LABELS: Record<EntryLevel, string> = { Information: 'Info', Warning: 'Warning', Error: 'Error' }

/** The filter the address asks for. */
function filterOf(params: URLSearchParams): LogFilter {
  const number = (name: string) => (params.get(name) ? Number(params.get(name)) : undefined)
  return {
    workspace: params.get('workspace') === INSTANCE ? undefined : number('workspace'),
    instance: params.get('workspace') === INSTANCE || undefined,
    connection: number('connection'),
    sync: number('sync'),
    level: params.get('level') === 'Warning' ? 'Warning' : undefined,
  }
}

/** What happened in the instance, newest first: an admin's every entry, and a member's own workspaces'. */
export function LogPage() {
  const { me } = useSession()
  const [params, setParams] = useSearchParams()
  const key = params.toString()
  const [workspaces, setWorkspaces] = useState<Workspace[]>([])
  const [loaded, setLoaded] = useState<{ key: string; page: LogEntries } | null>(null)
  const [failed, setFailed] = useState<{ key: string; message: string } | null>(null)
  const [older, setOlder] = useState(false)

  useEffect(() => {
    listWorkspaces()
      .then(setWorkspaces)
      .catch(() => {})
  }, [])

  useEffect(() => {
    let current = true
    getLog(filterOf(new URLSearchParams(key)))
      .then((page) => current && setLoaded({ key, page }))
      .catch((e: Error) => current && setFailed({ key, message: e.message }))
    return () => {
      current = false
    }
  }, [key])

  const page = loaded?.key === key ? loaded.page : null
  const error = failed?.key === key ? failed.message : null

  function change(name: string, value: string) {
    const next = new URLSearchParams(params)
    if (value) next.set(name, value)
    else next.delete(name)
    setParams(next)
  }

  function showEverything() {
    const next = new URLSearchParams(params)
    next.delete('connection')
    next.delete('sync')
    setParams(next)
  }

  async function showOlder() {
    if (!page?.next) return
    setOlder(true)
    try {
      const more = await getLog({ ...filterOf(params), before: page.next })
      setLoaded({ key, page: { ...more, entries: [...page.entries, ...more.entries] } })
    } catch (e) {
      setFailed({ key, message: (e as Error).message })
    } finally {
      setOlder(false)
    }
  }

  return (
    <div className="page">
      <h1>Log</h1>
      <p className="muted">
        {me?.isAdmin
          ? 'What happened in this instance, newest first.'
          : 'What happened in your workspaces, newest first.'}
      </p>

      <div className="form inline log-filters">
        <label>
          Workspace
          <select value={params.get('workspace') ?? ''} onChange={(e) => change('workspace', e.target.value)}>
            <option value="">{me?.isAdmin ? 'Everything' : 'All my workspaces'}</option>
            {me?.isAdmin && <option value={INSTANCE}>The instance: users, settings, and setup</option>}
            {workspaces.map((w) => (
              <option key={w.id} value={w.id}>
                {w.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Show
          <select value={params.get('level') ?? ''} onChange={(e) => change('level', e.target.value)}>
            <option value="">Every entry</option>
            <option value="Warning">Warnings and errors</option>
          </select>
        </label>
      </div>

      {page?.subject && (
        <p className="log-subject">
          Showing {page.subject} only.{' '}
          <button type="button" className="link-button" onClick={showEverything}>
            Show every entry
          </button>
        </p>
      )}

      {error && <p className="error">{error}</p>}
      {page === null ? (
        !error && <p className="muted">Loading…</p>
      ) : page.entries.length === 0 ? (
        <p className="muted">Nothing has been logged here yet.</p>
      ) : (
        <>
          <ol className="list log">
            {page.entries.map((entry) => (
              <Entry key={entry.id} entry={entry} />
            ))}
          </ol>
          {page.next !== null && (
            <p>
              <button type="button" className="secondary" onClick={showOlder} disabled={older}>
                {older ? 'Loading…' : 'Show older'}
              </button>
            </p>
          )}
        </>
      )}
    </div>
  )
}

function Entry({ entry }: { entry: LogEntry }) {
  return (
    <li className={`log-entry ${entry.level.toLowerCase()}`}>
      <p className="log-meta">
        <span className="level">
          <LevelIcon level={entry.level} />
          {LABELS[entry.level]}
        </span>
        <time dateTime={entry.at}>{new Date(entry.at).toLocaleString()}</time>
        {entry.workspaceId !== null ? (
          <Link to={`/workspaces/${entry.workspaceId}`}>{entry.workspaceName}</Link>
        ) : (
          <span>The instance</span>
        )}
        {entry.actor && <span className="muted">by {entry.actor}</span>}
      </p>
      <p className="log-text">{entry.text}</p>
    </li>
  )
}
