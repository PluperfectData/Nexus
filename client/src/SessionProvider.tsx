import { useEffect, useState, type ReactNode } from 'react'
import { getMe, onSignedOut, type Me } from './api'
import { SessionContext, SIGNED_OUT } from './session'

export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null)

  useEffect(() => {
    onSignedOut(() => setMe(SIGNED_OUT))
    getMe()
      .then(setMe)
      .catch(() => setMe(SIGNED_OUT))
  }, [])

  return <SessionContext.Provider value={{ me, setMe }}>{children}</SessionContext.Provider>
}
