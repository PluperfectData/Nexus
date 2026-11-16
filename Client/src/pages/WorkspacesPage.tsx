import { type FormEvent, useEffect, useState } from 'react'
import { Link } from 'react-router'
import { listWorkspaces, makeWorkspace, type Workspace } from '../api'
import { arrivalQuery, clearQuery } from '../query'
import { useSession } from '../session'

/** The workspaces the user may see, and for an admin, a form to make one. */
export function WorkspacesPage() {
  const { me } = useSession()
  const [workspaces, setWorkspaces] = useState<Workspace[] | null>(null)
  // A sign-in the service lost track of comes back here with its reason.
  const [error, setError] = useState<string | null>(() => arrivalQuery().get('error'))
  const [name, setName] = useState('')

  useEffect(() => {
    clearQuery()
    listWorkspaces()
      .then(setWorkspaces)
      .catch((e: Error) => setError(e.message))
  }, [])

  async function make(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      const made = await makeWorkspace(name.trim())
      setWorkspaces((current) => [...(current ?? []), made].sort((a, b) => a.name.localeCompare(b.name)))
      setName('')
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <div className="page">
      <h1>Workspaces</h1>
      {error && <p className="error">{error}</p>}
      {workspaces === null ? (
        <p className="muted">Loading…</p>
      ) : workspaces.length === 0 ? (
        <p className="muted">
          {me?.isAdmin ? 'No workspaces yet. Make the first one below.' : 'You are in no workspace yet. Ask an admin to add you to one.'}
        </p>
      ) : (
        <ul className="list">
          {workspaces.map((workspace) => (
            <li key={workspace.id}>
              <Link to={`/workspaces/${workspace.id}`}>{workspace.name}</Link>
            </li>
          ))}
        </ul>
      )}

      {me?.isAdmin && (
        <form className="form inline" onSubmit={make}>
          <label>
            New workspace
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Acme" required maxLength={200} />
          </label>
          <button type="submit">Make workspace</button>
        </form>
      )}
    </div>
  )
}
