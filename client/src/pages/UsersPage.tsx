import { type FormEvent, useEffect, useState } from 'react'
import { editUser, type InviteLink, inviteUser, listUsers, reinviteUser, removeUser, type User } from '../api'
import { CopyValue } from '../components/InstanceForms'
import { useSession } from '../session'

/** An invite link the instance did not mail, for the admin to pass on, with why. */
interface PassOn extends InviteLink {
  email: string
  link: string
}

/** The instance's users, for admins: invite one, make or unmake an admin, send a new invite, and remove one. */
export function UsersPage() {
  const { me } = useSession()
  const [users, setUsers] = useState<User[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [passOn, setPassOn] = useState<PassOn | null>(null)
  const [email, setEmail] = useState('')
  const [admin, setAdmin] = useState(false)

  useEffect(() => {
    if (!me?.isAdmin) return
    listUsers()
      .then(setUsers)
      .catch((e: Error) => setError(e.message))
  }, [me?.isAdmin])

  if (!me?.isAdmin) return <p className="page muted">Only admins manage users.</p>

  async function act(action: () => Promise<void>) {
    setError(null)
    setNotice(null)
    setPassOn(null)
    try {
      await action()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const replace = (user: User) => setUsers((current) => current?.map((u) => (u.id === user.id ? user : u)) ?? null)

  function invite(event: FormEvent) {
    event.preventDefault()
    void act(async () => {
      const invite = await inviteUser(email.trim(), admin)
      const made = invite.user
      setUsers((current) => [...(current ?? []), made].sort((a, b) => a.email.localeCompare(b.email)))
      if (invite.link) setPassOn({ ...invite, email: made.email, link: invite.link })
      else setNotice(`Invited ${made.email}. The invite link works for 7 days.`)
      setEmail('')
      setAdmin(false)
    })
  }

  return (
    <div className="page">
      <h1>Users</h1>
      {error && <p className="error">{error}</p>}
      {notice && <p className="success">{notice}</p>}
      {passOn && (
        <div className="panel">
          <p className={passOn.mailProblem ? 'error' : undefined}>
            {passOn.mailProblem
              ? `Nexus could not mail ${passOn.email} the invite: ${passOn.mailProblem}`
              : `This instance has no mail set up, so pass this invite link on to ${passOn.email}.`}
          </p>
          <CopyValue label="Invite link" value={passOn.link} note="It signs them in once, within 7 days." />
        </div>
      )}

      {users === null ? (
        <p className="muted">Loading…</p>
      ) : (
        <ul className="list">
          {users.map((user) => (
            <li key={user.id}>
              <div>
                <strong>{user.email}</strong>
                <p className="muted">
                  {user.isAdmin ? 'Admin' : 'Member'}
                  {user.signedInAt ? `, last signed in ${new Date(user.signedInAt).toLocaleString()}` : ', not signed in yet'}
                </p>
              </div>
              <div className="row-actions">
                {user.id !== me.id && (
                  <button type="button" className="secondary" onClick={() => act(async () => replace(await editUser(user.id, !user.isAdmin)))}>
                    {user.isAdmin ? 'Make member' : 'Make admin'}
                  </button>
                )}
                <button
                  type="button"
                  className="secondary"
                  onClick={() =>
                    act(async () => {
                      const invite = await reinviteUser(user.id)
                      if (invite.link) setPassOn({ ...invite, email: user.email, link: invite.link })
                      else setNotice(`Mailed ${user.email} a new invite link.`)
                    })
                  }
                >
                  New invite
                </button>
                {user.id !== me.id && (
                  <button
                    type="button"
                    className="quiet"
                    onClick={() =>
                      window.confirm(`Remove ${user.email}? They are signed out everywhere.`) &&
                      act(async () => {
                        await removeUser(user.id)
                        setUsers((current) => current?.filter((u) => u.id !== user.id) ?? null)
                      })
                    }
                  >
                    Remove
                  </button>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}

      <form className="form inline" onSubmit={invite}>
        <label>
          Invite by email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
        </label>
        <label className="check">
          <input type="checkbox" checked={admin} onChange={(e) => setAdmin(e.target.checked)} />
          As an admin
        </label>
        <button type="submit">Invite</button>
      </form>
    </div>
  )
}
