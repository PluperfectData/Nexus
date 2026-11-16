import { type FormEvent, useState } from 'react'
import { setPassword } from '../api'
import { useSession } from '../session'

const MIN_PASSWORD = 12

/** The signed-in user's address, and setting or changing their password. */
export function AccountPage() {
  const { me, setMe } = useSession()
  const [password, setValue] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (password !== confirm) {
      setError('The two passwords differ.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      setMe(await setPassword(password))
      setValue('')
      setConfirm('')
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="page narrow">
      <h1>Account</h1>
      <p>
        Signed in as <strong>{me?.email}</strong>
        {me?.isAdmin && ', an admin'}.
      </p>

      <h2>{me?.hasPassword ? 'Change your password' : 'Set a password'}</h2>
      <p className="muted">
        Without a password you sign in with a mailed link. Setting one signs you out everywhere else.
      </p>
      <form className="form stacked" onSubmit={submit}>
        <label>
          New password
          <input
            type="password"
            value={password}
            onChange={(e) => {
              setValue(e.target.value)
              setSaved(false)
            }}
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
        {saved && <p className="success">Your password is set.</p>}
        <div className="form-actions">
          <button type="submit" disabled={busy}>
            {busy ? 'Saving…' : 'Save password'}
          </button>
        </div>
      </form>
    </div>
  )
}
