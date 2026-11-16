import { createContext, useContext } from 'react'
import type { Me } from './api'

export const SIGNED_OUT: Me = { signedIn: false, id: null, email: null, isAdmin: false, hasPassword: false }

/** Who is signed in, or null while the client is still asking. */
export const SessionContext = createContext<{ me: Me | null; setMe: (me: Me) => void }>({ me: null, setMe: () => {} })

export const useSession = () => useContext(SessionContext)
