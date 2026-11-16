import { type FormEvent, useEffect, useState } from 'react'
import { Navigate, useNavigate } from 'react-router'
import { getSetup, setUp } from '../api'
import { peekNext } from '../next'
import { useSession } from '../session'

const MIN_PASSWORD = 12

/** Claims an instance with no users: the setup code from the service's log makes the first admin, signed in at once. */
export function SetupPage() {
  const { me, setMe } = useSession()
  const navigate = useNavigate()
  const [needed, setNeeded] = useState<boolean | null>(null)
  const [code, setCode] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getSetup()
      .then((setup) => setNeeded(setup.needed))
      .catch((e: Error) => setError(e.message))
  }, [])

  // Setting the session re-renders this page signed in, and this guard moves on from there.
  if (me?.signedIn) return <Navigate to={peekNext()} replace />

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (password !== confirm) {
      setError('The two passwords differ.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      setMe(await setUp(code.trim(), email.trim(), password))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  if (needed === false) {
    return (
      <div className="page narrow">
        <h1>Set up Nexus</h1>
        <p>This instance is set up. Sign in instead.</p>
        <button type="button" onClick={() => navigate('/sign-in')}>
          Sign in
        </button>
      </div>
    )
  }

  return (
    <div className="page narrow">
      <h1>Set up Nexus</h1>
      <p>
        This instance has no users yet. The service wrote a setup code to its log when it started. Enter it to make yourself
        the first admin.
      </p>
      <form className="form stacked" onSubmit={submit}>
        <label>
          Setup code
          <input value={code} onChange={(e) => setCode(e.target.value)} placeholder="ABCD-EFGH-JKMN" required autoComplete="off" />
        </label>
        <label>
          Your email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="email" />
        </label>
        <label>
          Password
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            minLength={MIN_PASSWORD}
            required
            autoComplete="new-password"
          />
        </label>
        <label>
          The same password again
          <input type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required autoComplete="new-password" />
        </label>
        <p className="hint muted">At least {MIN_PASSWORD} characters.</p>
        {error && <p className="error">{error}</p>}
        <div className="form-actions">
          <button type="submit" disabled={busy || needed === null}>
            {busy ? 'Setting up…' : 'Set up and sign in'}
          </button>
        </div>
      </form>
    </div>
  )
}
