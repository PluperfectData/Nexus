import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { approvePairing, declinePairing, getPairing, listWorkspaces, type Pairing, type Workspace } from '../api'
import { leaveFor } from '../browser'
import { useSession } from '../session'

/** Where an app sends an admin to approve linking it: pick its workspace, and go back to the app linked. */
export function PairPage() {
  const { me } = useSession()
  const [id] = useState(() => new URLSearchParams(window.location.search).get('id') ?? '')
  const [pairing, setPairing] = useState<Pairing | null>(null)
  const [workspaces, setWorkspaces] = useState<Workspace[] | null>(null)
  const [workspaceId, setWorkspaceId] = useState(0)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const admin = me?.isAdmin ?? false
  useEffect(() => {
    if (!admin) return
    Promise.all([getPairing(id), listWorkspaces()])
      .then(([found, list]) => {
        setPairing(found)
        setWorkspaces(list)
        setWorkspaceId(list[0]?.id ?? 0)
      })
      .catch((e: Error) => setError(e.message))
  }, [admin, id])

  if (!admin) {
    return (
      <div className="page narrow">
        <h1>Link an app</h1>
        <p>Only an admin of this instance can link an app. Ask one to open this page.</p>
      </div>
    )
  }

  async function answer(action: () => Promise<{ returnUrl: string }>) {
    setBusy(true)
    setError(null)
    try {
      leaveFor((await action()).returnUrl)
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  if (!pairing || !workspaces) {
    return <div className="page narrow">{error ? <p className="error">{error}</p> : <p className="muted">Loading…</p>}</div>
  }

  const host = new URL(pairing.ingestUrl).host

  return (
    <div className="page narrow">
      <h1>Link {pairing.appName}</h1>
      <p>
        <strong>{pairing.appName}</strong> at {host} asks to link to Nexus. Approving makes it an app in the workspace you pick,
        with a key of its own, and lets it open connect pages whose rows go to it at <code>{pairing.ingestUrl}</code>.
      </p>
      {workspaces.length === 0 ? (
        <p>
          There is no workspace to link it to yet. <Link to="/workspaces">Make one</Link>, then open this page again.
        </p>
      ) : (
        <form
          className="form stacked"
          onSubmit={(event) => {
            event.preventDefault()
            void answer(() => approvePairing(id, workspaceId))
          }}
        >
          <label>
            Workspace
            <select value={workspaceId} onChange={(e) => setWorkspaceId(Number(e.target.value))}>
              {workspaces.map((workspace) => (
                <option key={workspace.id} value={workspace.id}>
                  {workspace.name}
                </option>
              ))}
            </select>
          </label>
          {error && <p className="error">{error}</p>}
          <div className="form-actions">
            <button type="submit" disabled={busy}>
              Approve and go back to {pairing.appName}
            </button>
            <button type="button" className="quiet" disabled={busy} onClick={() => void answer(() => declinePairing(id))}>
              Decline
            </button>
          </div>
        </form>
      )}
    </div>
  )
}
