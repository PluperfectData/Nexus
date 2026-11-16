import { type FormEvent, useEffect, useState } from 'react'
import { Navigate, useNavigate } from 'react-router'
import { getSetup, mailLink, signIn } from '../api'
import { peekNext } from '../next'
import { useSession } from '../session'

/** Signing in with a password, or asking for a link by mail. */
export function SignInPage() {
  const { me, setMe } = useSession()
  const navigate = useNavigate()
  const [byLink, setByLink] = useState(false)
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [remember, setRemember] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [mailed, setMailed] = useState(false)

  // An instance with no users has no one to sign in, so its first visitor sets it up.
  useEffect(() => {
    getSetup()
      .then((setup) => {
        if (setup.needed) navigate('/setup', { replace: true })
      })
      .catch(() => {})
  }, [navigate])

  // The one place a sign-in moves on: setting the session re-renders this page signed in, and a second navigation
  // started beside it would race this one.
  if (me?.signedIn) return <Navigate to={peekNext()} replace />

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (byLink) {
        await mailLink(email.trim())
        setMailed(true)
      } else {
        setMe(await signIn(email.trim(), password, remember))
      }
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page narrow">
      <h1>Sign in to Nexus</h1>
      {mailed ? (
        <p>
          If {email.trim()} has an account, a sign-in link is on its way. It works once, for 15 minutes.
        </p>
      ) : (
        <form className="form stacked" onSubmit={submit}>
          <label>
            Email
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required autoComplete="email" />
          </label>
          {!byLink && (
            <>
              <label>
                Password
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                  autoComplete="current-password"
                />
              </label>
              <label className="check">
                <input type="checkbox" checked={remember} onChange={(e) => setRemember(e.target.checked)} />
                Keep me signed in on this device
              </label>
            </>
          )}
          {error && <p className="error">{error}</p>}
          <div className="form-actions">
            <button type="submit" disabled={busy}>
              {byLink ? 'Mail me a link' : 'Sign in'}
            </button>
            <button type="button" className="link-button" onClick={() => setByLink(!byLink)}>
              {byLink ? 'Sign in with a password' : 'Mail me a sign-in link instead'}
            </button>
          </div>
        </form>
      )}
    </div>
  )
}
