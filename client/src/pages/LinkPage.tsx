import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { redeemLink } from '../api'
import { peekNext } from '../next'
import { useSession } from '../session'

/** Where a mailed link lands: the token after the # signs in once, and the page moves on. */
export function LinkPage() {
  const { setMe } = useSession()
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)
  // A link works once, so the second run of the effect in development must not redeem it again.
  const redeemed = useRef(false)

  useEffect(() => {
    if (redeemed.current) return
    redeemed.current = true
    const token = window.location.hash.slice(1)
    redeemLink(token, false)
      .then((me) => {
        setMe(me)
        navigate(peekNext(), { replace: true })
      })
      .catch((e: Error) => setError(e.message))
  }, [navigate, setMe])

  return (
    <div className="page narrow">
      <h1>Signing in</h1>
      {error ? (
        <>
          <p className="error">{error}</p>
          <p>
            <Link to="/sign-in">Back to sign in</Link>
          </p>
        </>
      ) : (
        <p className="muted">Checking your link…</p>
      )}
    </div>
  )
}
