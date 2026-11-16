import { useEffect, useState } from 'react'
import { addMember, listUsers, removeMember, type User, type WorkspaceDetail } from '../api'

/** A workspace's members, for admins: add any user, or remove one. */
export function MembersSection({ workspace, onChange }: { workspace: WorkspaceDetail; onChange: (workspace: WorkspaceDetail) => void }) {
  const [users, setUsers] = useState<User[]>([])
  const [picked, setPicked] = useState('')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    listUsers()
      .then(setUsers)
      .catch((e: Error) => setError(e.message))
  }, [])

  const outside = users.filter((user) => !workspace.members.some((member) => member.id === user.id))

  async function act(action: () => Promise<WorkspaceDetail>) {
    setError(null)
    try {
      onChange(await action())
      setPicked('')
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <section>
      <h2>Members</h2>
      <p className="muted">Members see this workspace and change its connections and syncs. Admins see every workspace.</p>
      {error && <p className="error">{error}</p>}
      {workspace.members.length === 0 ? (
        <p className="muted">No members yet.</p>
      ) : (
        <ul className="list">
          {workspace.members.map((member) => (
            <li key={member.id}>
              <span>{member.email}</span>
              <button type="button" className="quiet" onClick={() => act(() => removeMember(workspace.id, member.id))}>
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
      {outside.length > 0 && (
        <form
          className="form inline"
          onSubmit={(event) => {
            event.preventDefault()
            void act(() => addMember(workspace.id, Number(picked)))
          }}
        >
          <label>
            Add a member
            <select value={picked} onChange={(e) => setPicked(e.target.value)} required>
              <option value="">Pick a user</option>
              {outside.map((user) => (
                <option key={user.id} value={user.id}>
                  {user.email}
                </option>
              ))}
            </select>
          </label>
          <button type="submit">Add member</button>
        </form>
      )}
    </section>
  )
}
